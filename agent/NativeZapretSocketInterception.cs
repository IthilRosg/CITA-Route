namespace RoutePilot.Agent;

internal sealed record NativeZapretTarget(string Epoch,string Domain,string Application,string Address,ushort Port);

// Constructed before startup, retained by NativeBoundTcpLease even if startup
// fails. The caller must authorize the target/epoch; this is not a public proxy.
internal sealed class NativeZapretSocketInterception(NativeZapretBundle bundle,NativeZapretTarget target,
    uint physicalIndex,string localAddress,IReadOnlySet<string> excludedAddresses,Func<bool> sessionCurrent,
    CancellationToken serviceStop,NativeZapretCapture capture=NativeZapretCapture.WholeConnection,bool packetDiagnostics=false,bool flowLifetime=false,NativeAdmissionQueue.Lease? admission=null):INativeSocketInterception
{
    readonly SemaphoreSlim gate=new(1,1);
    readonly CancellationTokenSource leaseStop=new();
    CancellationTokenSource? workerStop;
    NativeZapretWorker? worker;
    bool started;
    int retiring,confirmed;
    long readyTimestamp;
    int? lastNaturalExit;
    string lastWorkerOutput="";
    internal long ReadyTimestamp=>Interlocked.Read(ref readyTimestamp);
    internal int WorkerPid{get;private set;}
    internal bool Alive=>worker?.State is "StartedUnverified" or "Validated";
    internal int? WorkerNaturalExitCode=>worker?.NaturalExitCode??lastNaturalExit;
    internal string WorkerOutput=>worker?.StartupText??lastWorkerOutput;
    public bool RetirementConfirmed=>Volatile.Read(ref retiring)==1&&Volatile.Read(ref confirmed)==1;
    bool Current()=>Volatile.Read(ref retiring)==0&&!serviceStop.IsCancellationRequested&&!leaseStop.IsCancellationRequested&&sessionCurrent();
    public async Task WaitReadyAsync(NativeBoundSocketIdentity socket,CancellationToken stop)
    {
        await gate.WaitAsync(stop);
        try
        {
            if(started||Volatile.Read(ref retiring)!=0)throw new InvalidOperationException("Zapret interception lease is single-use.");
            started=true;
            if(socket.Epoch!=target.Epoch||socket.PhysicalIndex!=physicalIndex||socket.LocalAddress!=localAddress||
                socket.RemoteAddress!=target.Address||socket.RemotePort!=target.Port||!sessionCurrent())
                throw new InvalidDataException("Zapret connector identity differs from its authorized target.");
            var scope=new NativeZapretScope(target.Epoch,target.Application,physicalIndex,localAddress,socket.LocalPort,target.Address,target.Port,"tcp");
            var plan=NativeZapretPlan.ForTlsTcp(scope,target.Epoch,physicalIndex,localAddress,excludedAddresses,capture);
            var linked=workerStop=CancellationTokenSource.CreateLinkedTokenSource(stop,serviceStop,leaseStop.Token);
            try{worker=NativeZapretWorker.Start(bundle,plan,Current,leaseStop.Token,linked.Token,packetDiagnostics,flowLifetime,admission);}
            catch(NativeZapretRetirementException failed){worker=failed.Worker;throw;}
            var identity=worker.CaptureIdentity();WorkerPid=identity.Pid;
            await NativeWinDivertReflection.WaitAsync(bundle.Root,plan.Filter,identity.Pid,identity.StartedTimestamp,
                ()=>Current()&&worker.IdentityCurrent(identity.Pid,identity.StartedTimestamp),linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if(!Current()||!worker.IdentityCurrent(identity.Pid,identity.StartedTimestamp))throw new IOException("Zapret retired before connector readiness.");
            Interlocked.Exchange(ref readyTimestamp,System.Diagnostics.Stopwatch.GetTimestamp());
        }
        finally{gate.Release();}
    }
    public async Task RetireAsync(CancellationToken stop)
    {
        if(Interlocked.Exchange(ref retiring,1)==0)leaseStop.Cancel();
        await gate.WaitAsync(stop);
        try
        {
            if(RetirementConfirmed)return;
            worker?.Dispose();
            if(worker!=null&&!worker.RetirementConfirmed)throw new IOException("Zapret kernel worker retirement is unconfirmed.");
            if(worker!=null){lastNaturalExit=worker.NaturalExitCode;lastWorkerOutput=worker.StartupText;}
            worker=null;workerStop?.Dispose();workerStop=null;leaseStop.Dispose();Volatile.Write(ref confirmed,1);
        }
        finally{gate.Release();}
    }
}
