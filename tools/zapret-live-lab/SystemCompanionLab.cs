using System.Diagnostics;
using System.Net;
using System.Text.Json;
using RoutePilot.Agent;

// Laboratory decorator. Production connector admission remains unchanged.
internal sealed class SystemCompanionLab(INativeSocketInterception inner,NativeWfpGuard guard,string run,bool cancelStartupAck=false,bool malformedStartupAck=false):INativeSocketInterception
{
    Process? helper;
    Task<JsonElement>? startupReply;
    NativeConnectorPermission? permission;
    bool released;
    public bool RetirementConfirmed=>inner.RetirementConfirmed&&released;
    public async Task WaitReadyAsync(NativeBoundSocketIdentity socket,CancellationToken stop)
    {
        if(Environment.GetEnvironmentVariable("CITA_PHYSICAL_GUARD_LAB")!="1")throw new IOException("Physical lab required.");
        await inner.WaitReadyAsync(socket,stop);
        var info=new ProcessStartInfo(@"C:\Program Files\RoutePilot\Agent\zapret-live-lab\native\native-guard.exe"){
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        helper=Process.Start(info)??throw new IOException("Dynamic System lease helper unavailable.");
        _=helper.StandardError.BaseStream.CopyToAsync(Stream.Null);
        permission=new NativeConnectorPermission(Environment.ProcessPath!,socket.LocalAddress,socket.LocalPort,socket.RemoteAddress,socket.PhysicalIndex);
        // Cancelling a read after it consumed half a JSON line would corrupt
        // the subsequent cleanup ACK. Retain one read task across cancellation.
        startupReply=ReadStartup(helper.StandardOutput,malformedStartupAck);
        await helper.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{Action="HoldSystemConnector",Run=run,ExpectedPolicy=guard.Digest,ExpectedEndpoint=guard.EndpointDigest,Connector=permission}).AsMemory(),stop);
        await helper.StandardInput.FlushAsync(stop);
        if(cancelStartupAck){await startupReply.WaitAsync(stop);throw new OperationCanceledException("Controlled cancellation before delivering committed startup ACK.");}
        var reply=await startupReply.WaitAsync(stop);
        if(!reply.GetProperty("success").GetBoolean()||reply.GetProperty("run").GetString()!=run||reply.GetProperty("stage").GetString()!="SystemLeaseHeld")throw new IOException("Dynamic System lease refused: "+reply.GetRawText());
    }
    public async Task RetireAsync(CancellationToken stop)
    {
        if(RetirementConfirmed)return;
        await inner.RetireAsync(stop);
        if(!inner.RetirementConfirmed)throw new IOException("Interceptor retirement unconfirmed.");
        if(helper!=null)
        {
            helper.StandardInput.Close();
            await helper.WaitForExitAsync(stop);
            try{if(startupReply!=null)await startupReply.WaitAsync(stop);}catch(Exception error)when(error is IOException or JsonException or InvalidOperationException){}
            if(permission!=null&&!await CheckAbsent(guard,run,permission,stop))throw new IOException("Dynamic System lease retirement unconfirmed by independent inspection.");
            helper.Dispose();helper=null;
        }
        released=true;
    }
    internal async Task KillHelper(CancellationToken stop)
    {
        if(helper==null)throw new IOException("No held helper to kill.");helper.Kill();await helper.WaitForExitAsync(stop);
    }
    static async Task<JsonElement> ReadStartup(StreamReader reader,bool malformed)
    {
        var reply=await NativeAwgBackend.ReadReply(reader,CancellationToken.None,8192);
        if(malformed)throw new JsonException("Controlled malformed startup reply after kernel grant.");
        return reply;
    }
    internal sealed record LifecycleResult(bool LostAckReleased,bool MalformedAckReleased,bool KilledHelperReleased,bool OtherPortDenied);
    internal static async Task<LifecycleResult> CheckLifecycle(NativeWfpGuard guard,string run,string epoch,IPAddress local,uint index,IPAddress remote,CancellationToken stop)
    {
        bool lostAck=false,malformedReleased=false,killed=false,denied=false;
        foreach(string fault in new[]{"cancel","malformed","kill"})
        {
            bool cancelAck=fault=="cancel",malformed=fault=="malformed";
            var lease=NativeBoundTcpLease.Bind(local,index,epoch);
            var identity=new NativeBoundSocketIdentity(lease.Id,epoch,local.ToString(),checked((ushort)lease.Local.Port),remote.ToString(),443,index);
            var companion=new SystemCompanionLab(new NativeGuardedSocketInterception(guard,new ReadyControl(),Environment.ProcessPath!),guard,run,cancelAck,malformed);
            try{
                try{await companion.WaitReadyAsync(identity,stop);if(cancelAck)throw new IOException("Controlled lost ACK did not cancel.");}
                catch(OperationCanceledException)when(cancelAck){}
                catch(JsonException)when(malformed){}
                if(fault=="kill")
                {
                    var permission=new NativeConnectorPermission(Environment.ProcessPath!,identity.LocalAddress,identity.LocalPort,identity.RemoteAddress,index);
                    if(await CheckAbsent(guard,run,permission,stop))throw new IOException("System lease missing while helper is alive.");
                    using var probe=new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,System.Net.Sockets.SocketType.Stream,System.Net.Sockets.ProtocolType.Tcp);
                    probe.SetSocketOption(System.Net.Sockets.SocketOptionLevel.IP,(System.Net.Sockets.SocketOptionName)31,IPAddress.HostToNetworkOrder(checked((int)index)));probe.Bind(new IPEndPoint(local,0));
                    try{await probe.ConnectAsync(new IPEndPoint(remote,443),stop);throw new IOException("Different source port unexpectedly permitted.");}
                    catch(System.Net.Sockets.SocketException error)when(error.SocketErrorCode==System.Net.Sockets.SocketError.AccessDenied){denied=true;}
                    await companion.KillHelper(stop);
                    killed=await CheckAbsent(guard,run,permission,stop);
                    if(!killed)throw new IOException("Dynamic permission survived hard-kill.");
                }
            }finally{
                // Keep the exclusive socket bound until independent revocation
                // inspection has succeeded, including the lost-ACK case.
                await companion.RetireAsync(stop);
                if(!companion.RetirementConfirmed)throw new IOException("Lifecycle lease did not retire.");
                await lease.DisposeAsync();
            }
            if(cancelAck)lostAck=true;
            if(malformed)malformedReleased=true;
            if(!await guard.VerifyAsync(stop))throw new IOException("Base guard changed during lifecycle test.");
        }
        return new(lostAck,malformedReleased,killed,denied);
    }
    sealed class ReadyControl:INativeSocketInterception
    {
        public bool RetirementConfirmed{get;private set;}
        public Task WaitReadyAsync(NativeBoundSocketIdentity socket,CancellationToken stop)=>Task.CompletedTask;
        public Task RetireAsync(CancellationToken stop){RetirementConfirmed=true;return Task.CompletedTask;}
    }
    internal static async Task<bool> CheckAbsent(NativeWfpGuard guard,string run,NativeConnectorPermission permission,CancellationToken stop)
    {
        var info=new ProcessStartInfo(@"C:\Program Files\RoutePilot\Agent\zapret-live-lab\native\native-guard.exe"){
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        using var check=Process.Start(info)??throw new IOException("Dynamic lease verifier unavailable.");_=check.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try{
            await check.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{Action="CheckSystemConnector",Run=run,ExpectedPolicy=guard.Digest,ExpectedEndpoint=guard.EndpointDigest,Connector=permission}).AsMemory(),stop);check.StandardInput.Close();
            var reply=await NativeAwgBackend.ReadReply(check.StandardOutput,stop,8192);await check.WaitForExitAsync(stop);
            if(check.ExitCode!=0||!reply.GetProperty("success").GetBoolean()||reply.GetProperty("run").GetString()!=run)throw new IOException("Independent dynamic lease inspection failed.");
            return reply.GetProperty("stage").GetString()=="SystemLeaseAbsent";
        }finally{if(!check.HasExited){check.Kill();await check.WaitForExitAsync();}}
    }
    internal static async Task<NativeZapretConnection> Open(NativeZapretBundle bundle,NativeZapretTarget target,uint index,IPAddress local,NativeWfpGuard guard,CancellationToken session,CancellationToken setup,NativeZapretCapture capture,bool debug,string run)
    {
        var lease=NativeBoundTcpLease.Bind(local,index,target.Epoch);
        var owner=new NativeZapretSocketInterception(bundle,target,index,local.ToString(),new HashSet<string>{"143.20.37.241"},()=>true,session,capture,debug);
        var combined=new SystemCompanionLab(new NativeGuardedSocketInterception(guard,owner,Environment.ProcessPath!),guard,run);
        try{return new(lease,owner,await lease.ConnectAsync(new IPEndPoint(IPAddress.Parse(target.Address),443),combined,setup));}
        catch(Exception error)
        {
            error.Data["NativeWinDivertReady"]=owner.ReadyTimestamp>0;error.Data["NativeWinDivertStarted"]=owner.WorkerPid>0;error.Data["NativeWorkerNaturalExit"]=owner.WorkerNaturalExitCode;error.Data["NativeWorkerOutput"]=owner.WorkerOutput;
            try{await lease.DisposeAsync();}catch(Exception cleanup){throw new NativeConnectorRetirementException(new AggregateException(error,cleanup));}throw;
        }
    }
}
