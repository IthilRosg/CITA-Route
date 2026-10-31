using System.Diagnostics;
using System.Text.Json;

namespace RoutePilot.Agent;

internal sealed record NativeDynamicSocketSnapshot(int ProcessId,long StartedUtcTicks,NativeConnectorPermission? Scope,bool Current,long HeartbeatTimestamp);

// Owns a private helper and its dynamic kernel permission for one held socket.
// Production publication remains gated until end-to-end acceptance completes.
internal sealed class NativeDynamicSocketLease(NativeWfpGuard guard,string image,string run,Func<bool> current,CancellationToken sessionStop):INativeSocketInterception
{
    readonly SemaphoreSlim gate=new(1,1);
    readonly CancellationTokenSource lost=new(),monitorStop=new();
    CancellationTokenRegistration sessionRegistration;
    Process? helper;
    NativeProcessJob? job;
    Task<JsonElement>? pendingReply;
    Task? monitor;
    NativeConnectorPermission? permission;
    string socketLease="";
    long heartbeat;
    int started,retiring,confirmed;
    internal Func<bool>? DataPlaneCurrent{get;set;}
    internal CancellationToken Lost=>lost.Token;
    string failure="";
    internal string Failure=>Volatile.Read(ref failure);
    internal string HelperReportedStage{get;private set;}="";
    internal NativeDynamicSocketSnapshot Snapshot=>new(helper?.Id??0,helper?.StartTime.ToUniversalTime().Ticks??0,permission,IsCurrent,Interlocked.Read(ref heartbeat));
    internal bool IsCurrent
    {
        get{try{return Volatile.Read(ref started)==1&&Volatile.Read(ref retiring)==0&&!lost.IsCancellationRequested&&current()&&helper is {HasExited:false}&&Stopwatch.GetElapsedTime(Interlocked.Read(ref heartbeat))<TimeSpan.FromSeconds(6);}catch(InvalidOperationException){return false;}}
    }
    public bool RetirementConfirmed=>Volatile.Read(ref confirmed)==1;
    void Fail(string reason)
    {
        Interlocked.CompareExchange(ref failure,reason,"");try{lost.Cancel();}catch(AggregateException){}
    }
    public async Task WaitReadyAsync(NativeBoundSocketIdentity socket,CancellationToken stop)
    {
        await gate.WaitAsync(stop);
        try{
            if(Interlocked.Exchange(ref started,1)!=0||Volatile.Read(ref retiring)!=0)throw new IOException("Dynamic socket lease is single-use.");
            if(!current())throw new IOException("Dynamic socket session is stale.");
            socketLease=socket.Id.ToString("N")+socket.Epoch;
            if(socketLease.Length!=64||!socketLease.All(Uri.IsHexDigit))throw new IOException("Socket epoch identity required.");
            permission=new(Environment.ProcessPath!,socket.LocalAddress,socket.LocalPort,socket.RemoteAddress,socket.PhysicalIndex);
            sessionRegistration=sessionStop.Register(()=>Fail("session-stopped"));sessionStop.ThrowIfCancellationRequested();
            var info=StartInfo();helper=Process.Start(info)??throw new IOException("Dynamic socket helper unavailable.");_=helper.Handle;
            job=new NativeProcessJob(64UL*1024*1024);job.AssignBlockedOnPrivateInput(helper);
            _=helper.StandardError.BaseStream.CopyToAsync(Stream.Null);
            pendingReply=NativeAwgBackend.ReadReply(helper.StandardOutput,CancellationToken.None,8192);
            await helper.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{Action="HoldSystemConnector",Run=run,ExpectedPolicy=guard.Digest,ExpectedEndpoint=guard.EndpointDigest,Connector=permission,Continuous=true,SocketLease=socketLease}).AsMemory(),stop);
            await helper.StandardInput.FlushAsync(stop);
            Check(await pendingReply.WaitAsync(stop),"SystemLeaseHeld");pendingReply=null;
            Interlocked.Exchange(ref heartbeat,Stopwatch.GetTimestamp());
            sessionStop.ThrowIfCancellationRequested();if(!current())throw new IOException("Dynamic socket session changed before readiness.");
            monitor=Monitor();
        }catch{Fail("startup-failed");throw;}
        finally{gate.Release();}
    }
    ProcessStartInfo StartInfo()=>new(image){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
    void Check(JsonElement reply,string stage)
    {
        if(reply.ValueKind==JsonValueKind.Object&&reply.TryGetProperty("stage",out var phase)&&phase.ValueKind==JsonValueKind.String&&phase.GetString() is "SystemLeaseHeld" or "SystemLeaseCurrent" or "SystemLeaseLost" or "SystemLeaseReleased")HelperReportedStage=phase.GetString()!;
        ValidateAcknowledgement(reply,run,socketLease,stage);
    }
    internal static void ValidateAcknowledgement(JsonElement reply,string run,string socketLease,string stage)
    {
        if(reply.ValueKind!=JsonValueKind.Object||!reply.TryGetProperty("success",out var success)||success.ValueKind!=JsonValueKind.True||
            !reply.TryGetProperty("run",out var owner)||owner.ValueKind!=JsonValueKind.String||owner.GetString()!=run||
            !reply.TryGetProperty("stage",out var phase)||phase.ValueKind!=JsonValueKind.String||phase.GetString()!=stage||
            !reply.TryGetProperty("socketLease",out var id)||id.ValueKind!=JsonValueKind.String||id.GetString()!=socketLease)throw new IOException("Dynamic socket acknowledgement mismatch.");
    }
    async Task Monitor()
    {
        try{
            while(true){
                await Task.Delay(TimeSpan.FromSeconds(2),monitorStop.Token);
                if(!current()){Fail("session-stale");throw new IOException("Socket session epoch is stale.");}
                if(!IsCurrent||DataPlaneCurrent?.Invoke()==false)throw new IOException("Dynamic socket or data plane disappeared.");
                await helper!.StandardInput.WriteLineAsync("{\"Renew\":true}".AsMemory(),monitorStop.Token);await helper.StandardInput.FlushAsync(monitorStop.Token);
                pendingReply=NativeAwgBackend.ReadReply(helper.StandardOutput,CancellationToken.None,8192);
                Check(await pendingReply.WaitAsync(TimeSpan.FromSeconds(3),monitorStop.Token),"SystemLeaseCurrent");pendingReply=null;
                Interlocked.Exchange(ref heartbeat,Stopwatch.GetTimestamp());
            }
        }catch(OperationCanceledException)when(monitorStop.IsCancellationRequested){}
        catch(Exception error)when(error is IOException or JsonException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception){Fail(error.GetType().Name);}
        finally{if(Volatile.Read(ref retiring)==0){Fail("dynamic-monitor-stopped");try{helper?.StandardInput.Close();}catch(IOException){};if(job!=null){try{using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));await job.TerminateAsync(deadline.Token);}catch(Exception error)when(error is IOException or OperationCanceledException or System.ComponentModel.Win32Exception){Interlocked.Exchange(ref failure,"helper-termination-unconfirmed");}}}}
    }
    internal async Task<bool> AbsentAsync(CancellationToken stop)
    {
        if(permission==null)return true;
        using var check=Process.Start(StartInfo())??throw new IOException("Dynamic socket verifier unavailable.");_=check.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try{
            await check.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{Action="CheckSystemConnector",Run=run,ExpectedPolicy=guard.Digest,ExpectedEndpoint=guard.EndpointDigest,Connector=permission}).AsMemory(),stop);check.StandardInput.Close();
            var reply=await NativeAwgBackend.ReadReply(check.StandardOutput,stop,8192);await check.WaitForExitAsync(stop);
            if(check.ExitCode!=0||!reply.GetProperty("success").GetBoolean()||reply.GetProperty("run").GetString()!=run)throw new IOException("Independent socket inspection failed.");
            return reply.GetProperty("stage").GetString()=="SystemLeaseAbsent";
        }finally{if(!check.HasExited){check.Kill();await check.WaitForExitAsync();}}
    }
    public async Task RetireAsync(CancellationToken stop)
    {
        Interlocked.Exchange(ref retiring,1);monitorStop.Cancel();
        await gate.WaitAsync(stop);
        try{
            if(RetirementConfirmed)return;
            if(monitor!=null){try{await monitor.WaitAsync(stop);}catch(OperationCanceledException)when(stop.IsCancellationRequested){throw;}catch(Exception error){Interlocked.CompareExchange(ref failure,"monitor-failed:"+error.GetType().Name,"");}}
            if(helper!=null){
                try{helper.StandardInput.Close();}catch(IOException){}
                if(job!=null){await job.TerminateAsync(stop);if(job.ReadUsage().ActiveProcesses!=0)throw new IOException("Dynamic helper job retirement unconfirmed.");}
                else if(!helper.HasExited)helper.Kill(); // no control request could precede successful job assignment
                await helper.WaitForExitAsync(stop);
                try{if(pendingReply!=null)await pendingReply.WaitAsync(stop);}catch(Exception error)when(error is IOException or JsonException or InvalidOperationException){}
                if(!await AbsentAsync(stop))throw new IOException("Dynamic permission retirement unconfirmed.");
                helper.Dispose();helper=null;
                job?.Dispose();job=null;
            }
            sessionRegistration.Dispose();Volatile.Write(ref confirmed,1);
        }finally{gate.Release();}
    }
}
