using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RoutePilot.Agent;

internal static class GoPeerLab
{
    internal static async Task<int> Run(NativeZapretBundle bundle,uint index,IPAddress local,string domain,IPAddress remote,bool fileLockVerified,bool guarded=false)
    {
        const string image=@"C:\CITA-Lab\ZapretLiveLab\Cita.CoreConnectorTests.exe";
        using var measuring=Process.GetCurrentProcess();var initialCpu=measuring.TotalProcessorTime;var duration=Stopwatch.StartNew();
        bool continuous=Environment.GetEnvironmentVariable("CITA_GUARD_CONTINUOUS")=="1";
        string faultMode=Environment.GetEnvironmentVariable("CITA_DYNAMIC_LAB_DROP")??"None";bool faultExpected=faultMode is "system" or "agent" or "epoch" or "stop";
        int epochCurrent=1;bool faultInjected=false;
        int parallel=Environment.GetEnvironmentVariable("CITA_GUARD_PARALLEL")=="4"?4:1;
        var connections=new System.Collections.Concurrent.ConcurrentBag<NativeZapretConnection>();int peakWorkers=0;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(continuous?60:30));using var session=new CancellationTokenSource();
        var info=new ProcessStartInfo(image){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,
            RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(image)};
        info.ArgumentList.Add("-test.run=^TestCitaNativePolicyConnectorPhysical$");info.ArgumentList.Add(continuous?"-test.timeout=55s":"-test.timeout=25s");
        info.Environment["CITA_ZAPRET_PHYSICAL_LAB"]="1";
        using var peer=Process.Start(info)??throw new IOException("Go peer did not start.");
        var flowReady=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output=ReadBounded(peer.StandardOutput,deadline.Token,flowReady);var errors=ReadBounded(peer.StandardError,deadline.Token);
        string epoch=Guid.NewGuid().ToString("N"),failure="",stdout="",stderr="";
        NativeZapretConnection? connection=null;int factoryCalls=0;
        NativeWfpGuard? guard=null;bool baselineConnected=false,unpermittedBlocked=false,guardVerified=false,wfpRevoked=false,wfpCleanup=!guarded;
        string factoryFailure="";long factoryMs=0;
        string factoryPhase="";bool filterReady=false,workerStarted=false;
        int factoryPort=0;int? workerNaturalExit=null;JsonElement? dropEvidence=null;string evidenceError="";
        string workerOutput="";
        bool scopedTcpControl=false;
        SystemCompanionLab.LifecycleResult? systemLifecycle=null;
        var capture=NativeZapretCapture.WholeConnection;
        if(guarded&&Environment.GetEnvironmentVariable("CITA_GUARD_CAPTURE") is {} selected&&!Enum.TryParse(selected,out capture))throw new IOException("Unknown lab capture mode.");
        string? oldGuardLab=Environment.GetEnvironmentVariable("CITA_PHYSICAL_GUARD_LAB");
        var server=new NativeCoreConnector(peer,epoch,()=>Volatile.Read(ref epochCurrent)==1,
            r=>r.Domain==domain&&r.Application.Equals(image,StringComparison.OrdinalIgnoreCase)&&r.Address==remote.ToString(),
            async(r,stop)=>{
                var started=Stopwatch.StartNew();
                try{
                    Interlocked.Increment(ref factoryCalls);var target=new NativeZapretTarget(epoch,domain,r.Application,r.Address,r.Port);
                    var result=guard==null?await NativeZapretConnection.OpenAsync(bundle,target,index,local.ToString(),new HashSet<string>{"143.20.37.241"},()=>true,session.Token,stop):
                        continuous?await NativeZapretConnection.OpenGuardedAsync(bundle,target,index,local.ToString(),new HashSet<string>{"143.20.37.241"},()=>Volatile.Read(ref epochCurrent)==1,session.Token,stop,guard,useDynamic:true):
                        Environment.GetEnvironmentVariable("CITA_GUARD_SYSTEM")=="1"?await SystemCompanionLab.Open(bundle,target,index,local,guard,session.Token,stop,capture,Environment.GetEnvironmentVariable("CITA_GUARD_DEBUG")=="1",Environment.GetEnvironmentVariable("CITA_GUARD_LAB_RUN")!):
                        await NativeZapretConnection.OpenGuardedAsync(bundle,target,index,local.ToString(),new HashSet<string>{"143.20.37.241"},()=>true,session.Token,stop,guard,capture,Environment.GetEnvironmentVariable("CITA_GUARD_DEBUG")=="1",Environment.GetEnvironmentVariable("CITA_GUARD_INJECTOR")=="1");
                    connections.Add(result);connection=result;
                    int seen;do{seen=Volatile.Read(ref peakWorkers);}while(NativeZapretWorker.SlotsInUse>seen&&Interlocked.CompareExchange(ref peakWorkers,NativeZapretWorker.SlotsInUse,seen)!=seen);
                    return result;
                }catch(Exception error){factoryFailure=error.GetType().Name+": "+error.Message+"; base="+error.GetBaseException().Message;
                    factoryPhase=error.Data["NativeSocketPhase"]?.ToString()??"";filterReady=error.Data["NativeWinDivertReady"] is true;workerStarted=error.Data["NativeWinDivertStarted"] is true;
                    factoryPort=error.Data["NativeSocketPort"] is int port?port:0;workerNaturalExit=error.Data["NativeWorkerNaturalExit"] as int?;workerOutput=error.Data["NativeWorkerOutput"]?.ToString()??"";throw;}
                finally{factoryMs=started.ElapsedMilliseconds;}
            },session.Token);
        int exit=-1;bool tlsValidated=false,wrongServerPidRefused=false;string status="";
        try
        {
            if(guarded)
            {
                string run=Environment.GetEnvironmentVariable("CITA_GUARD_LAB_RUN")??"";
                if(run.Length!=32||!run.All(Uri.IsHexDigit))throw new IOException("Exact WFP lab ownership required.");
                string installed=@"C:\Program Files\RoutePilot\Agent\zapret-live-lab";
                if(!string.Equals(Environment.ProcessPath,Path.Combine(installed,"RoutePilot.Agent.exe"),StringComparison.OrdinalIgnoreCase))throw new IOException("Protected WFP lab image required.");
                Environment.SetEnvironmentVariable("CITA_PHYSICAL_GUARD_LAB","1");
                baselineConnected=await ProbeSocket(local,index,remote,deadline.Token);
                if(!baselineConnected)throw new IOException("Unfiltered TCP baseline unavailable.");
                var policy=JsonSerializer.SerializeToElement(new{Vpn=new[]{remote+"/32"},Direct=Array.Empty<string>()});
                guard=new NativeWfpGuard(Path.Combine(installed,"native","native-guard.exe"),run,policy,
                    endpoint:new(Path.Combine(installed,"native","native-owner.exe"),"143.20.37.241",3080));
                await guard.PlanAsync(deadline.Token);await guard.ArmAsync(deadline.Token);
                guardVerified=await guard.VerifyAsync(deadline.Token);
                unpermittedBlocked=!await ProbeSocket(local,index,remote,deadline.Token);
                if(!guardVerified||!unpermittedBlocked)throw new IOException("Unpermitted physical socket was not blocked.");
                scopedTcpControl=await ProbeScopedSocket(guard,epoch,local,index,remote,deadline.Token);
                if(!scopedTcpControl)throw new IOException("Exact WFP socket control failed before Zapret.");
                if(Environment.GetEnvironmentVariable("CITA_GUARD_SYSTEM")=="1")systemLifecycle=await SystemCompanionLab.CheckLifecycle(guard,run,epoch,local,index,remote,deadline.Token);
            }
            server.Start();
            await peer.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{Endpoint=server.Endpoint,Domain=domain,Application=image,Address=remote.ToString(),HoldMilliseconds=continuous&&!faultExpected&&parallel==1?25000:0,FaultExpected=faultExpected,RejectStale=faultMode is "epoch" or "stop",ParallelConnections=parallel==4?4:0}).AsMemory(),deadline.Token);
            peer.StandardInput.Close();
            if(faultMode is "epoch" or "stop"){
                await flowReady.Task.WaitAsync(deadline.Token);faultInjected=true;
                if(faultMode=="epoch")Interlocked.Exchange(ref epochCurrent,0);else session.Cancel();
            }
            if(Environment.GetEnvironmentVariable("CITA_GUARD_CONTROLLER_CRASH")=="1"){
                await flowReady.Task.WaitAsync(deadline.Token);
                string run=Environment.GetEnvironmentVariable("CITA_GUARD_LAB_RUN")!;
                File.WriteAllText(@"C:\CITA-Lab\ZapretLiveLab\flow-ready-"+run+".json",JsonSerializer.Serialize(new{run,controller=Environment.ProcessId,peer=peer.Id,tlsResponseReceived=true,policyDigest=guard!.Digest,endpointDigest=guard.EndpointDigest,permission=connection!.Permission!.Snapshot,at=Stopwatch.GetTimestamp()}));
            }
            await peer.WaitForExitAsync(deadline.Token);exit=peer.ExitCode;
            stdout=await output;stderr=await errors;
            var report=stdout.Split('\n').SingleOrDefault(line=>line.StartsWith("CITA_CONNECTOR_INTEROP ",StringComparison.Ordinal));
            if(report==null)throw new IOException("Go peer produced no interoperability report.");
            using var parsed=JsonDocument.Parse(report[23..]);
            tlsValidated=parsed.RootElement.GetProperty("tlsValidated").GetBoolean();
            wrongServerPidRefused=parsed.RootElement.GetProperty("wrongServerPidRefused").GetBoolean();
            status=parsed.RootElement.GetProperty("status").GetString()!;
            if(faultExpected){
                if(!parsed.RootElement.GetProperty("failClosedObserved").GetBoolean())throw new IOException("Path did not close after fault.");
                bool attributed=faultMode switch{"system" or "agent"=>connection?.Permission?.HelperReportedStage=="SystemLeaseLost","epoch"=>faultInjected&&connection?.Permission?.Failure=="session-stale","stop"=>faultInjected&&connection?.Permission?.Failure=="session-stopped",_=>false};
                if(!attributed)throw new IOException("Fault did not produce an independently attributed path shutdown.");
            }
        }
        catch(Exception error){failure=error.GetType().Name+": "+error.Message;}
        finally
        {
            session.Cancel();if(!peer.HasExited){peer.Kill();await peer.WaitForExitAsync();}
            try{await server.DisposeAsync();}catch(Exception error){failure+=" cleanup: "+error.GetType().Name+": "+error.Message;}
            if(guard!=null)
            {
                using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(12));
                if(factoryPort>=1024){try{dropEvidence=await guard.ReadConnectorDropsAsync(checked((ushort)factoryPort),cleanup.Token);}catch(Exception error){evidenceError=error.GetType().Name+": "+error.Message;}}
                try{
                    wfpRevoked=guard.ConnectorPermits==0&&await guard.VerifyAsync(cleanup.Token);
                    await guard.RemoveAsync(cleanup.Token);wfpCleanup=true;
                }catch(Exception error){failure+=" WFP cleanup: "+error.GetType().Name+": "+error.Message;}
            }
            Environment.SetEnvironmentVariable("CITA_PHYSICAL_GUARD_LAB",oldGuardLab);
            try{stdout=await output;stderr=await errors;}catch{failure+=" output unavailable";}
        }
        bool readyBeforeConnect=connections.Count==parallel&&connections.All(c=>c.Owner.ReadyTimestamp>0&&c.ConnectTimestamp>=c.Owner.ReadyTimestamp);
        bool success=exit==0&&failure.Length==0&&tlsValidated&&wrongServerPidRefused&&factoryCalls==parallel&&server.Accepted==parallel&&server.Rejected is >=1 and <=4&&
            readyBeforeConnect&&fileLockVerified&&connections.All(c=>c.Owner.RetirementConfirmed)&&!server.CleanupUnconfirmed&&(parallel!=4||peakWorkers==4)&&
            NativeBoundTcpLease.RetainedCount==0&&NativeZapretWorker.SlotsInUse==0&&(!guarded||baselineConnected&&unpermittedBlocked&&guardVerified&&wfpRevoked&&wfpCleanup);
        measuring.Refresh();
        Console.WriteLine(JsonSerializer.Serialize(new{success,exit,failure,tlsValidated,wrongServerPidRefused,status,readyBeforeConnect,
            elapsedMs=duration.ElapsedMilliseconds,agentCpuMs=(measuring.TotalProcessorTime-initialCpu).TotalMilliseconds,
            guarded,baselineConnected,unpermittedBlocked,guardVerified,wfpRevoked,wfpCleanup,
            scopedTcpControl,
            capture=capture.ToString(),
            systemCompanion=Environment.GetEnvironmentVariable("CITA_GUARD_SYSTEM")=="1",
            systemLifecycle,
            continuous,
            parallel,peakWorkers,
            faultMode,faultInjected,permissionFailure=connection?.Permission?.Failure,helperReportedStage=connection?.Permission?.HelperReportedStage,
            factoryFailure,factoryMs,factoryPhase,factoryPort,filterReady,workerStarted,workerNaturalExit,workerOutput,dropEvidence,evidenceError,guardFailureStage=guard?.LastFailureStage,guardFailureReason=guard?.LastFailureReason,
            fileLockVerified,factoryCalls,accepted=server.Accepted,rejected=server.Rejected,retirementConfirmed=connection?.Owner.RetirementConfirmed==true,
            socketLeases=NativeBoundTcpLease.RetainedCount,workerSlots=NativeZapretWorker.SlotsInUse,peerOutput=stdout,peerError=stderr,
            scope="Real Go connector + Windows pipe + Zapret"+(guarded?" + exact WFP socket permission":"")+"; no TUN/selector integration claim"}));
        return success?0:1;
    }
    static async Task<bool> ProbeSocket(IPAddress local,uint index,IPAddress remote,CancellationToken stop)
    {
        using var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
        socket.SetSocketOption(SocketOptionLevel.IP,(SocketOptionName)31,IPAddress.HostToNetworkOrder(checked((int)index)));
        socket.Bind(new IPEndPoint(local,0));using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try{await socket.ConnectAsync(new IPEndPoint(remote,443),deadline.Token);return true;}
        catch(SocketException error)when(error.SocketErrorCode==SocketError.AccessDenied){return false;}
    }
    static async Task<bool> ProbeScopedSocket(NativeWfpGuard guard,string epoch,IPAddress local,uint index,IPAddress remote,CancellationToken stop)
    {
        var lease=NativeBoundTcpLease.Bind(local,index,epoch);
        var control=new NativeGuardedSocketInterception(guard,new NoInterceptionControl(),Environment.ProcessPath!);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try{await lease.ConnectAsync(new IPEndPoint(remote,443),control,deadline.Token);return true;}
        finally{await lease.DisposeAsync();}
    }
    // Isolates WFP permission correctness from WinDivert effects in this lab
    // control only. Never used by the production Zapret factory.
    sealed class NoInterceptionControl:INativeSocketInterception
    {
        public bool RetirementConfirmed{get;private set;}
        public Task WaitReadyAsync(NativeBoundSocketIdentity identity,CancellationToken stop)=>Task.CompletedTask;
        public Task RetireAsync(CancellationToken stop){RetirementConfirmed=true;return Task.CompletedTask;}
    }
    static async Task<string> ReadBounded(StreamReader reader,CancellationToken stop,TaskCompletionSource? ready=null)
    {
        var text=new StringBuilder();char[] buffer=new char[1024];int count;
        while((count=await reader.ReadAsync(buffer,stop))>0){if(text.Length+count>16384)throw new IOException("Go peer output budget exceeded.");text.Append(buffer,0,count);if(ready!=null&&text.ToString().Contains("CITA_FLOW_READY\n",StringComparison.Ordinal))ready.TrySetResult();}
        return text.ToString();
    }
}
