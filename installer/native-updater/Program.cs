using System.Diagnostics;
using System.Security.Principal;
using Cita.NativeUpdater;
using RoutePilot.Agent;

if(args is ["--fixture-child"]){await Task.Delay(Timeout.Infinite);return 0;}
if(args is ["--self-test"])
{
    if(!ProtectedRun.IdValid(new string('a',32))||ProtectedRun.IdValid(new string('A',32))||!ProtectedRun.HashValid(new string('A',64))||ProtectedRun.HashValid(new string('a',64)))throw new IOException("Upgrade identity test failed.");
    using var job=new NativeProcessJob();using var child=job.StartOwned(Environment.ProcessPath!,new[]{"--fixture-child"},Environment.CurrentDirectory);
    for(int i=0;i<30&&!child.HasExited&&job.ReadUsage().ActiveProcesses==0;i++)await Task.Delay(100);
    if(child.HasExited||job.ReadUsage().ActiveProcesses<1||!ServiceFence.Contains(child,job.DescriptorHandleValue))throw new IOException("Upgrade child not owned; exited="+child.HasExited+", active="+job.ReadUsage().ActiveProcesses+".");
    uint before=job.ReadUsage().ActiveProcesses;
    await job.TerminateAsync(CancellationToken.None);await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    if(job.ReadUsage().ActiveProcesses!=0)throw new IOException("Upgrade retirement unconfirmed.");
    Console.WriteLine("Upgrade identity and process-job retirement PASS; active before="+before+", after=0; no SCM or pair test.");return 0;
}
if(args.Length!=2||args[0] is not ("--controller" or "--primary" or "--backup" or "--recover-on-boot"))throw new ArgumentException("Upgrade mode refused.");
using var run=new ProtectedRun(args[1]);string role=args[0][2..];
try
{
    string sid=WindowsIdentity.GetCurrent().User?.Value??throw new UnauthorizedAccessException("Upgrade identity unavailable.");
    bool controller=role=="controller";
    if(controller?sid!=run.Plan.OwnerSid:sid!="S-1-5-18")throw new UnauthorizedAccessException("Upgrade role identity refused.");
    string boot=NativeBootIdentity.Current;
    if(boot.Length==0)throw new IOException("Upgrade boot identity unavailable.");
    if(role=="recover-on-boot")
    {
        // A previous boot proves all old processes/jobs are retired. Never infer
        // that from a stale PID, a timeout, or a missing controller file.
        if(boot==run.Plan.Boot)throw new IOException("Boot recovery requires a different verified boot.");
        using var serial=await RecoveryLock(run,TimeSpan.FromSeconds(10));
        string bootStarted=Path.Combine(run.Root,"boot-recovery-started.json");
        if(File.Exists(bootStarted))
        {
            var prior=run.Read<BootRecoveryStarted>("boot-recovery-started.json");
            if(prior.Run!=run.Id||!long.TryParse(prior.Boot,out long oldBoot)||oldBoot<=0)throw new IOException("Boot recovery owner record refused.");
            if(prior.Boot==boot)
            {
                var done=run.Read<VerifiedResult>("verified-recover-on-boot.json");
                if(done.Run!=run.Id||done.Boot!=boot||!done.Success||done.ActorProcesses!=0||done.RecoveryProcesses!=0)throw new IOException("Previous boot recovery is unconfirmed.");
                return 0;
            }
            File.Move(bootStarted,Path.Combine(run.Root,"boot-recovery-started-"+prior.Boot+".json"));
            // Results belong to a boot, so never overwrite an older witness.
            foreach(string prefix in new[]{"recovered-","recovering-","verified-"})
            {
                string old=Path.Combine(run.Root,prefix+"recover-on-boot.json");
                if(File.Exists(old))File.Move(old,Path.Combine(run.Root,prefix+"recover-on-boot-"+prior.Boot+".json"));
            }
        }
        run.Save("boot-recovery-started.json",new BootRecoveryStarted(run.Id,boot));
        using var recovery=new NativeProcessJob();
        await RunRecovery(run,recovery,role,()=>{});return 0;
    }
    if(boot!=run.Plan.Boot||run.Plan.DeadlineUtc<=DateTimeOffset.UtcNow||run.Plan.DeadlineUtc>DateTimeOffset.UtcNow.AddMinutes(5))throw new IOException("Upgrade admission expired.");
    using var intentHolder=new IntentHolder(run,role);
    if(controller)
    {
        using var actorJob=new NativeProcessJob();using var primaryRecovery=new NativeProcessJob();using var backupRecovery=new NativeProcessJob();using var self=Process.GetCurrentProcess();
        var descriptor=new JobDescriptor(run.Id,boot,ProtectedRun.Identity(self),actorJob.DescriptorHandleValue,primaryRecovery.DescriptorHandleValue,backupRecovery.DescriptorHandleValue);
        run.Save("controller.json",descriptor);
        await Until(()=>File.Exists(Path.Combine(run.Root,"ready-primary.json"))&&File.Exists(Path.Combine(run.Root,"ready-backup.json")),TimeSpan.FromSeconds(25));
        var watchers=new List<Process>();
        try
        {
            foreach(string watcherRole in new[]{"primary","backup"})
            {
                var proof=run.Read<WatcherProof>("ready-"+watcherRole+".json");
                if(proof.Run!=run.Id||proof.Role!=watcherRole||!proof.JobHeld)throw new IOException("Upgrade watcher admission refused.");
                watchers.Add(ProtectedRun.Hold(proof.Process,run.Image));
            }
            if(watchers[0].Id==watchers[1].Id||watchers.Any(p=>p.HasExited)||DateTimeOffset.UtcNow>=run.Plan.DeadlineUtc.AddSeconds(-90))throw new IOException("Upgrade watcher admission changed or time budget insufficient.");
            using var actor=StartScript(actorJob,run,"Actor.ps1","controller");
            run.Save("actor.json",new{run=run.Id,process=ProtectedRun.Identity(actor)});
            while(!actor.HasExited&&watchers.All(p=>!p.HasExited)&&DateTimeOffset.UtcNow<run.Plan.DeadlineUtc)
            {intentHolder.ObserveRelease();await Task.Delay(100);}
            // Watchers always retire the entire job before trusting any result.
            // Exiting here is a wake-up, never a success/rollback assertion.
            return actor.HasExited&&actor.ExitCode==0?0:1;
        }finally{foreach(var watcher in watchers)watcher.Dispose();}
    }
    var d=run.Read<JobDescriptor>("controller.json");
    if(d.Run!=run.Id||d.Boot!=boot)throw new IOException("Upgrade controller descriptor differs.");
    using var parent=ProtectedRun.Hold(d.Process,run.Image);
    using var actorOwned=NativeProcessJob.DuplicateFromHeldController(parent,d.ActorJob);
    using var primaryOwned=NativeProcessJob.DuplicateFromHeldController(parent,d.PrimaryRecoveryJob);
    using var backupOwned=NativeProcessJob.DuplicateFromHeldController(parent,d.BackupRecoveryJob);
    using var watcherSelf=Process.GetCurrentProcess();
    // Two-phase admission: hold the peer's process before advertising final
    // readiness. Later death is observed through this exact kernel handle,
    // without reopening a possibly reused PID during recovery.
    string peerRole=role=="primary"?"backup":"primary";
    run.Save("admitted-"+role+".json",new WatcherProof(run.Id,role,ProtectedRun.Identity(watcherSelf),true));
    await Until(()=>File.Exists(Path.Combine(run.Root,"admitted-"+peerRole+".json")),TimeSpan.FromSeconds(20));
    var peerProof=run.Read<WatcherProof>("admitted-"+peerRole+".json");
    if(peerProof.Run!=run.Id||peerProof.Role!=peerRole||!peerProof.JobHeld)throw new IOException("Peer watcher admission differs.");
    using var peer=ProtectedRun.Hold(peerProof.Process,run.Image);
    if(peer.Id==watcherSelf.Id)throw new IOException("Peer watcher is not independent.");
    run.Save("ready-"+role+".json",new WatcherProof(run.Id,role,ProtectedRun.Identity(watcherSelf),true));
    while(!parent.HasExited&&DateTimeOffset.UtcNow<run.Plan.DeadlineUtc){intentHolder.ObserveRelease();await Task.Delay(100);}
    if(!parent.HasExited){parent.Kill();await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}
    await actorOwned.TerminateAsync(CancellationToken.None);
    if(actorOwned.ReadUsage().ActiveProcesses!=0)throw new IOException("Upgrade actor retirement unconfirmed.");
    run.Save("retired-"+role+".json",new{run=run.Id,actorProcesses=0,controllerExited=parent.HasExited});
    using var recoveryLock=await RecoveryLock(run,TimeSpan.FromSeconds(180),intentHolder.ObserveRelease);
    // Each watcher has a separate recovery job, duplicated into BOTH watchers.
    // If its peer dies while restoring, retire that peer's entire recovery job
    // before the surviving watcher may run a second restorer.
    var peerJob=role=="primary"?backupOwned:primaryOwned;
    if(peer.HasExited)
    {
        if(peerJob.ReadUsage().ActiveProcesses>0)await peerJob.TerminateAsync(CancellationToken.None);
        if(peerJob.ReadUsage().ActiveProcesses!=0)throw new IOException("Previous recovery retirement unconfirmed.");
    }
    else if(peerJob.ReadUsage().ActiveProcesses!=0)throw new IOException("Another live recovery still owns a worker.");
    string peerVerified="verified-"+peerRole+".json";
    if(File.Exists(Path.Combine(run.Root,peerVerified)))
    {
        var done=run.Read<VerifiedResult>(peerVerified);
        if(done.Run!=run.Id||done.Boot!=boot||!done.Success||done.ActorProcesses!=0||done.RecoveryProcesses!=0||peerJob.ReadUsage().ActiveProcesses!=0)throw new IOException("Peer recovery witness refused.");
        run.Save("verified-"+role+".json",done);return 0;
    }
    await RunRecovery(run,role=="primary"?primaryOwned:backupOwned,role,intentHolder.ObserveRelease);
    return 0;
}
catch(Exception error)
{
    try{run.Save("failure-"+role+".json",new{run=run.Id,role,errorType=error.GetType().Name,hresult=error.HResult});}catch{}
    return 1;
}

static Process StartScript(NativeProcessJob job,ProtectedRun run,string script,string role)
{
    string shell=Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
    return job.StartOwned(shell,new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",Path.Combine(run.Root,script),"-RunRoot",run.Root,"-Role",role},run.Root);
}
static async Task RunRecovery(ProtectedRun run,NativeProcessJob owned,string role,Action observeRelease)
{
    string resultName="recovered-"+role+".json";
    if(File.Exists(Path.Combine(run.Root,resultName)))throw new IOException("Recovery result already exists; reconcile before retrying.");
    using var self=Process.GetCurrentProcess();
    run.Save("recovery-controller-"+role+".json",new{Run=run.Id,Process=ProtectedRun.Identity(self),JobHandle=owned.DescriptorHandleValue});
    using var worker=StartScript(owned,run,"Recover.ps1",role);
    run.Save("recovering-"+role+".json",new{run=run.Id,process=ProtectedRun.Identity(worker)});
    var timer=Stopwatch.StartNew();
    while(!worker.HasExited&&timer.Elapsed<TimeSpan.FromSeconds(150)){observeRelease();await Task.Delay(100);}
    if(!worker.HasExited){await owned.TerminateAsync(CancellationToken.None);throw new IOException("Recovery exceeded its deadline.");}
    int code=worker.ExitCode;
    // Also retire descendants after a normal exit; leader exit is not job zero.
    if(owned.ReadUsage().ActiveProcesses>0)await owned.TerminateAsync(CancellationToken.None);
    if(owned.ReadUsage().ActiveProcesses!=0)throw new IOException("Recovery worker retirement unconfirmed.");
    if(code!=0)throw new IOException("Recovery worker failed.");
    var result=run.Read<RecoveryResult>(resultName);
    if(result.Run!=run.Id||!result.Success)throw new IOException("Recovery verification missing.");
    run.Save("verified-"+role+".json",new VerifiedResult(run.Id,NativeBootIdentity.Current,true,0,0));
}
static async Task<FileStream> RecoveryLock(ProtectedRun run,TimeSpan budget,Action? observe=null)
{
    var timer=Stopwatch.StartNew();
    while(true)
    {
        try{return new FileStream(Path.Combine(run.Root,"recovery.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException){if(timer.Elapsed>=budget)throw new IOException("Upgrade recovery lock unavailable.");observe?.Invoke();await Task.Delay(100);}
    }
}
static async Task Until(Func<bool> condition,TimeSpan budget)
{
    var timer=Stopwatch.StartNew();while(!condition()){if(timer.Elapsed>=budget)throw new TimeoutException("Upgrade watcher admission timed out.");await Task.Delay(100);}
}
internal sealed record JobDescriptor(string Run,string Boot,ProcessProof Process,long ActorJob,long PrimaryRecoveryJob,long BackupRecoveryJob);
internal sealed class IntentHolder:IDisposable
{
    readonly ProtectedRun run;readonly string role;FileStream? intent;
    internal IntentHolder(ProtectedRun run,string role){this.run=run;this.role=role;intent=run.Intent();}
    internal void ObserveRelease()
    {
        if(intent==null||!File.Exists(Path.Combine(run.Root,"release-old.json")))return;
        var request=run.Read<ReleaseRequest>("release-old.json");
        if(request.Run!=run.Id||!ServiceFence.DisabledStopped(run.AgentRoot))throw new IOException("Upgrade old-intent release fence unconfirmed.");
        intent.Dispose();intent=null;
        run.Save("released-"+role+".json",new{run=run.Id,released=true});
    }
    public void Dispose(){intent?.Dispose();intent=null;}
}
internal sealed record ReleaseRequest(string Run);
internal sealed record BootRecoveryStarted(string Run,string Boot);
internal sealed record VerifiedResult(string Run,string Boot,bool Success,int ActorProcesses,int RecoveryProcesses);
