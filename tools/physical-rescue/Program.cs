using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using RoutePilot.Agent;

if(args.Length==1&&args[0]=="--self-test"){await RescueTests.Run();return;}
if(args.Length==1&&args[0]=="--test-blocked-child"){await Console.In.ReadLineAsync();return;}
if(args.Length==1&&args[0]=="--test-request-child"){if(await Console.In.ReadLineAsync()!=null)Console.WriteLine("{\"success\":true}");return;}
if(args.Length==2&&args[0]=="--test-owner"&&args[1] is "before" or "after")
{
    using var testJob=new NativeProcessJob();
    using var child=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"--test-blocked-child") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true})??throw new IOException("Test child failed.");
    if(args[1]=="after")testJob.AssignBlockedOnPrivateInput(child);
    Console.WriteLine(child.Id);Console.Out.Flush();
    await Task.Delay(Timeout.InfiniteTimeSpan);return;
}
if(args.Length!=2||args[0] is not ("--controller" or "--controller-arm" or "--watch-primary" or "--watch-backup" or "--guard-eof-check" or "--maintenance-check" or "--maintenance-write-check"))throw new ArgumentException("Exact rescue mode and protected root required.");
string root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
string id=Path.GetFileName(root).Replace("cita-rescue-","");
if(Path.GetDirectoryName(root)!=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RoutePilot","Agent")||
    Path.GetFileName(root)!="cita-rescue-"+id||id.Length!=32||id.Any(c=>!char.IsAsciiHexDigit(c)||char.IsUpper(c)))throw new IOException("Rescue root refused.");
Check(root);for(var p=Directory.GetParent(root);p!=null;p=p.Parent)Check(p.FullName,true);
string image=Path.Combine(root,"Cita.PhysicalRescue.exe");
if(!string.Equals(Environment.ProcessPath,image,StringComparison.OrdinalIgnoreCase))throw new IOException("Rescue image location differs.");
using var imageLease=Locked(image);
if(args[0]=="--maintenance-write-check")
{
    // Never write or truncate: only request a write-capable handle after the
    // controlling parent has attached this process to its job.
    string? command=await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
    if(command!=JsonSerializer.Serialize(new{Action="WriteOpenCheck"}))throw new IOException("Private check request refused.");
    string intent=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RoutePilot","Agent","native","intent.json");
    Check(intent);for(var dir=new FileInfo(intent).Directory;dir!=null;dir=dir.Parent)Check(dir.FullName,true);
    int code=0;
    try{using var unexpected=new FileStream(intent,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);}
    catch(IOException error){code=error.HResult&0xffff;}
    int deleteCode=MaintenanceLease.TestDeleteOpen(intent);
    Console.WriteLine(JsonSerializer.Serialize(new{sharingViolation=code is 32 or 33,code,deleteSharingViolation=deleteCode is 32 or 33,deleteCode}));
    if(code is not (32 or 33)||deleteCode is not (32 or 33))Environment.ExitCode=1;
    return;
}
if(args[0]=="--maintenance-check")
{
    using var job=new NativeProcessJob();
    using var first=await OpenMaintenance();using var second=await OpenMaintenance();using var third=await OpenMaintenance();
    async Task CheckWrite()
    {
        var info=new ProcessStartInfo(image){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add("--maintenance-write-check");info.ArgumentList.Add(root);
        using var check=GuardChild.Start(info,job,()=>{});var errors=Capture(check.StandardError);
        using var result=await GuardChild.Request(check,new{Action="WriteOpenCheck"},false,()=>{});
        await check.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));await errors.WaitAsync(TimeSpan.FromSeconds(3));
        int code=result.RootElement.GetProperty("code").GetInt32();
        int deleteCode=result.RootElement.GetProperty("deleteCode").GetInt32();
        if(check.ExitCode!=0||!result.RootElement.GetProperty("sharingViolation").GetBoolean()||code is not (32 or 33)||!result.RootElement.GetProperty("deleteSharingViolation").GetBoolean()||deleteCode is not (32 or 33))throw new IOException("Intent write/delete opens were not denied by sharing.");
    }
    await CheckWrite();first.Dispose();await CheckWrite();second.Dispose();await CheckWrite();
    Console.WriteLine(JsonSerializer.Serialize(new{success=true,leases=3,sharingChecks=3,deleteSharingChecks=3,afterFirstAndSecondClose=true,scope="No write or delete; native status and sharing only"}));
    return;
}
if(args[0]=="--guard-eof-check")
{
    using var manifest=Locked(Path.Combine(root,"rescue.json"));
    if(manifest.Length>4096)throw new IOException("Rescue manifest budget exceeded.");
    var lease=JsonSerializer.Deserialize<Lease>(manifest,new JsonSerializerOptions{UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new IOException("Missing rescue lease.");
    if(lease.Run!=id||lease.GuardSha256.Length!=64||lease.GuardSha256.Any(c=>!char.IsAsciiHexDigit(c)))throw new IOException("Guard EOF lease refused.");
    string fixture=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RoutePilot","Agent","zapret-live-lab","native","native-guard.exe");
    for(var part=new FileInfo(fixture).Directory;part!=null;part=part.Parent)Check(part.FullName,true);
    using var fixtureLease=Locked(fixture);
    if(!Convert.ToHexString(SHA256.HashData(fixtureLease)).Equals(lease.GuardSha256,StringComparison.OrdinalIgnoreCase))throw new IOException("EOF fixture digest differs.");
    using var job=new NativeProcessJob();
    var info=new ProcessStartInfo(fixture){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
    info.Environment["CITA_PHYSICAL_GUARD_LAB"]="1";
    using var guard=GuardChild.Start(info,job,()=>{});var output=Capture(guard.StandardOutput);var error=Capture(guard.StandardError);
    guard.StandardInput.Close();
    try
    {
        await guard.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        string response=await output.WaitAsync(TimeSpan.FromSeconds(3));await error.WaitAsync(TimeSpan.FromSeconds(3));
        using var parsed=JsonDocument.Parse(response);
        if(guard.ExitCode!=1||parsed.RootElement.GetProperty("stage").GetString()!="Decode"||parsed.RootElement.GetProperty("errorType").GetString()!="InvalidRequest")throw new IOException("EOF guard response unconfirmed.");
        Console.WriteLine("PASS: real pinned guard exits on EOF before decoding any request.");
    }
    finally{await job.TerminateAsync(CancellationToken.None);}
    return;
}
if(args[0] is "--controller" or "--controller-arm")
{
    using var job=new NativeProcessJob();
    using var child=Process.Start(new ProcessStartInfo(image,"--test-blocked-child") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true})??throw new IOException("Blocked helper start failed.");
    job.AssignBlockedOnPrivateInput(child);
    if(job.ReadUsage().ActiveProcesses!=1||child.HasExited)throw new IOException("Blocked helper membership unconfirmed.");
    using var self=Process.GetCurrentProcess();Save("controller.json",new{pid=self.Id,startedTicks=self.StartTime.ToUniversalTime().Ticks,jobHandle=job.DescriptorHandleValue,childPid=child.Id,childStartedTicks=child.StartTime.ToUniversalTime().Ticks});
    if(args[0]=="--controller-arm")await RunNarrowArm(job,self);
    await Task.Delay(Timeout.InfiniteTimeSpan);return;
}
string role=args[0]=="--watch-primary"?"primary":"backup";
CleanupLease? cleanupOwner=null;
bool success=false;string failure="";bool controllerStopped=false;int rules=-1;uint jobActiveAfter=uint.MaxValue;
try
{
    using var manifest=Locked(Path.Combine(root,"rescue.json"));
    if(manifest.Length>4096)throw new IOException("Rescue manifest budget exceeded.");
    var lease=JsonSerializer.Deserialize<Lease>(manifest,new JsonSerializerOptions{UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new IOException("Missing rescue lease.");
    var now=DateTimeOffset.UtcNow;
    if(lease.Run!=id||lease.ControllerPid<=0||lease.ControllerPid==Environment.ProcessId||lease.ControllerStartedTicks<=0||
        lease.CreatedUtc>now.AddSeconds(5)||lease.DeadlineUtc<=lease.CreatedUtc||lease.DeadlineUtc>lease.CreatedUtc.AddSeconds(180)||
        lease.DeadlineUtc>now.AddSeconds(180)||lease.JobHandle<=0||lease.ChildPid<=0||lease.ChildStartedTicks<=0||lease.GuardSha256.Length!=64||lease.GuardSha256.Any(c=>!char.IsAsciiHexDigit(c)))throw new IOException("Rescue lease refused.");
    using var guardLease=Locked(Path.Combine(root,"native-guard.exe"));
    if(!Convert.ToHexString(SHA256.HashData(guardLease)).Equals(lease.GuardSha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Rescue guard digest differs.");
    Process? target=null;
    try{target=Process.GetProcessById(lease.ControllerPid);_=target.Handle;
        // PID reuse proves that the original controller is gone; never kill
        // its replacement, but still retire this run's old kernel state.
        if(target.StartTime.ToUniversalTime().Ticks!=lease.ControllerStartedTicks){target.Dispose();target=null;}
        else if(!string.Equals(target.MainModule?.FileName,image,StringComparison.OrdinalIgnoreCase))throw new IOException("Rescue controller identity differs.");}
    catch(ArgumentException){target?.Dispose();target=null;}
    using var maintenance=lease.EnableArm?await OpenMaintenance():null;
    using(target)
    {
        if(target==null)throw new IOException("Controller job cannot be verified after controller exit.");
        using var ownedJob=NativeProcessJob.DuplicateFromHeldController(target,lease.JobHandle);
        using var child=Process.GetProcessById(lease.ChildPid);_=child.Handle;
        if(child.HasExited||child.StartTime.ToUniversalTime().Ticks!=lease.ChildStartedTicks||!string.Equals(child.MainModule?.FileName,image,StringComparison.OrdinalIgnoreCase))throw new IOException("Blocked helper identity differs.");
        uint jobActiveBefore=ownedJob.ReadUsage().ActiveProcesses;
        if(jobActiveBefore!=1)throw new IOException("Controller job is not occupied by the expected helper.");
        using var watcher=Process.GetCurrentProcess();
        Save("ready-"+role+".json",new{run=id,role,pid=Environment.ProcessId,startedTicks=watcher.StartTime.ToUniversalTime().Ticks,controller=lease.ControllerPid,deadline=lease.DeadlineUtc,jobDuplicated=true,jobActiveProcesses=jobActiveBefore,maintenanceHeld=maintenance!=null},true);
        // No SSH connection or controller heartbeat is required for expiry.
        var timer=Stopwatch.StartNew();var remaining=lease.DeadlineUtc-DateTimeOffset.UtcNow;if(remaining<TimeSpan.Zero)remaining=TimeSpan.Zero;
        while(timer.Elapsed<remaining&&target is {HasExited:false}&&(maintenance==null||maintenance.ServiceStillMatches()))await Task.Delay(250);
        cleanupOwner=CleanupLease.Acquire(root);
        if(cleanupOwner==null)throw new IOException("Cleanup ownership unconfirmed.");
        if(File.Exists(Path.Combine(root,"result.json")))
        {
            using var previous=Locked(Path.Combine(root,"result.json"));
            if(previous.Length>4096)throw new IOException("Rescue result budget exceeded.");
            using var result=JsonDocument.Parse(previous);var value=result.RootElement;
            if(value.GetProperty("run").GetString()==id&&value.GetProperty("success").GetBoolean()&&
                value.GetProperty("controllerStopped").GetBoolean()&&value.GetProperty("rules").GetInt32()==0&&value.GetProperty("jobActiveAfter").GetUInt32()==0){cleanupOwner.Dispose();cleanupOwner=null;return;}
            throw new IOException("Existing cleanup result unconfirmed.");
        }
        if(target is {HasExited:false}){target.Kill();await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}
        controllerStopped=target==null||target.HasExited;
        await ownedJob.TerminateAsync(CancellationToken.None);
        jobActiveAfter=ownedJob.ReadUsage().ActiveProcesses;
        if(jobActiveAfter!=0)throw new IOException("Rescue job retirement unconfirmed.");
    }
    // Use the guard's existing physical-fixture identity gate. Never replace
    // the live service's binary or relax its production execution location.
    string fixtureGuard=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RoutePilot","Agent","zapret-live-lab","native","native-guard.exe");
    for(var part=new FileInfo(fixtureGuard).Directory;part!=null;part=part.Parent)Check(part.FullName,true);
    using var fixtureLease=Locked(fixtureGuard);
    if(!Convert.ToHexString(SHA256.HashData(fixtureLease)).Equals(lease.GuardSha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Independent fixture guard differs.");
    var info=new ProcessStartInfo(fixtureGuard){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
    info.Environment["CITA_PHYSICAL_GUARD_LAB"]="1";
    using var guard=Process.Start(info)??throw new IOException("Rescue guard start failed.");
    var output=Capture(guard.StandardOutput);var error=Capture(guard.StandardError);
    try
    {
        await guard.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{Action="Cleanup",Run=id}));guard.StandardInput.Close();
        await guard.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        string reply=await output.WaitAsync(TimeSpan.FromSeconds(3));await error.WaitAsync(TimeSpan.FromSeconds(3));
        using var result=JsonDocument.Parse(reply);var value=result.RootElement;
        rules=value.GetProperty("rules").GetInt32();
        success=controllerStopped&&guard.ExitCode==0&&value.GetProperty("success").GetBoolean()&&value.GetProperty("run").GetString()==id&&rules==0;
        if(!success)throw new IOException("Independent rescue cleanup unconfirmed.");
        }
    finally{if(!guard.HasExited){guard.Kill();await guard.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}}
}
catch(Exception error){failure=error.GetType().Name;}
try
{
    if(success)Save("result.json",new{run=id,success,controllerStopped,rules,jobActiveAfter,failure,utc=DateTimeOffset.UtcNow},true);
    else Save("failure-"+role+".json",new{run=id,role,success,controllerStopped,rules,jobActiveAfter,failure,utc=DateTimeOffset.UtcNow},true);
}
finally{cleanupOwner?.Dispose();}
Environment.ExitCode=success?0:1;

FileStream Locked(string path){Check(path);return new(path,FileMode.Open,FileAccess.Read,FileShare.Read);}
async Task<MaintenanceLease> OpenMaintenance()
{
    string native=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RoutePilot","Agent","native");
    string intent=Path.Combine(native,"intent.json");Check(intent);for(var dir=new FileInfo(intent).Directory;dir!=null;dir=dir.Parent)Check(dir.FullName,true);
    var held=MaintenanceLease.Open(intent);
    try{await held.ConfirmServiceAsync(p=>Check(p));return held;}catch{held.Dispose();throw;}
}
async Task RunNarrowArm(NativeProcessJob job,Process self)
{
    string leasePath=Path.Combine(root,"rescue.json");
    var wait=Stopwatch.StartNew();
    while(!File.Exists(leasePath)||!File.Exists(Path.Combine(root,"ready-primary.json"))||!File.Exists(Path.Combine(root,"ready-backup.json")))
    {if(wait.Elapsed>TimeSpan.FromSeconds(20))throw new IOException("Both watcher acknowledgements are required.");await Task.Delay(100);}
    using var leaseStream=Locked(leasePath);
    if(leaseStream.Length>4096)throw new IOException("Rescue manifest budget exceeded.");
    var lease=JsonSerializer.Deserialize<Lease>(leaseStream,new JsonSerializerOptions{UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new IOException("Missing lease.");
    if(!lease.EnableArm||lease.Run!=id||lease.ControllerPid!=self.Id||lease.ControllerStartedTicks!=self.StartTime.ToUniversalTime().Ticks||lease.JobHandle!=job.DescriptorHandleValue||lease.DeadlineUtc<DateTimeOffset.UtcNow.AddSeconds(12)||lease.DeadlineUtc>lease.CreatedUtc.AddSeconds(180))throw new IOException("Narrow test lease refused.");
    using var maintenance=await OpenMaintenance();
    Process HoldWatcher(string role)
    {
        using var file=Locked(Path.Combine(root,"ready-"+role+".json"));
        if(file.Length>4096)throw new IOException("Watcher acknowledgement budget exceeded.");
        using var data=JsonDocument.Parse(file);var item=data.RootElement;
        if(item.GetProperty("run").GetString()!=id||item.GetProperty("role").GetString()!=role||item.GetProperty("controller").GetInt32()!=self.Id||!item.GetProperty("jobDuplicated").GetBoolean()||!item.GetProperty("maintenanceHeld").GetBoolean()||item.GetProperty("jobActiveProcesses").GetUInt32()!=1||item.GetProperty("deadline").GetDateTimeOffset()!=lease.DeadlineUtc)throw new IOException("Watcher acknowledgement refused.");
        var held=Process.GetProcessById(item.GetProperty("pid").GetInt32());
        try{_=held.Handle;if(held.HasExited||held.StartTime.ToUniversalTime().Ticks!=item.GetProperty("startedTicks").GetInt64()||!string.Equals(held.MainModule?.FileName,image,StringComparison.OrdinalIgnoreCase))throw new IOException("Watcher identity refused.");return held;}
        catch{held.Dispose();throw;}
    }
    using var primary=HoldWatcher("primary");using var backup=HoldWatcher("backup");
    if(primary.Id==backup.Id)throw new IOException("Independent watchers required.");
    void ConfirmWatchers(){if(primary.HasExited||backup.HasExited||DateTimeOffset.UtcNow>=lease.DeadlineUtc.AddSeconds(-5)||!maintenance.ServiceStillMatches())throw new IOException("Watcher admission expired.");}
    string fixture=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RoutePilot","Agent","zapret-live-lab","native","native-guard.exe");
    for(var part=new FileInfo(fixture).Directory;part!=null;part=part.Parent)Check(part.FullName,true);
    using var fixtureLease=Locked(fixture);
    if(lease.GuardSha256.Length!=64||!Convert.ToHexString(SHA256.HashData(fixtureLease)).Equals(lease.GuardSha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Fixture guard digest differs.");
    async Task<bool> Connect()
    {
        using var client=new System.Net.Sockets.TcpClient(System.Net.Sockets.AddressFamily.InterNetwork);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try{await client.ConnectAsync(System.Net.IPAddress.Parse("1.1.1.1"),443,timeout.Token);return true;}
        catch(System.Net.Sockets.SocketException){return false;}catch(OperationCanceledException){return false;}
    }
    ConfirmWatchers();if(!await Connect())throw new IOException("Baseline destination unreachable.");
    await maintenance.ConfirmServiceAsync(p=>Check(p));ConfirmWatchers();
    Process StartGuard()
    {
        var info=new ProcessStartInfo(fixture){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.Environment["CITA_PHYSICAL_GUARD_LAB"]="1";
        return GuardChild.Start(info,job,ConfirmWatchers);
    }
    Task<JsonDocument> Request(Process guard,object request,bool keepOpen)=>GuardChild.Request(guard,request,keepOpen,ConfirmWatchers);
    using var arm=StartGuard();
    var armError=Capture(arm.StandardError);
    using(var reply=await Request(arm,new{Action="Arm",Run=id,Policy=new{Vpn=new[]{"1.1.1.1/32"},Direct=new[]{"10.0.0.0/8","172.16.0.0/12","192.168.0.0/16","26.0.0.0/8","100.64.0.0/10"}}},true))
        if(!reply.RootElement.GetProperty("success").GetBoolean()||reply.RootElement.GetProperty("run").GetString()!=id||reply.RootElement.GetProperty("rules").GetInt32()!=1)throw new IOException("Arm unconfirmed.");
    ConfirmWatchers();bool blocked=!await Connect();
    using var inspect=StartGuard();
    var inspectError=Capture(inspect.StandardError);
    using(var reply=await Request(inspect,new{Action="Inspect",Run=id},false))
        if(!reply.RootElement.GetProperty("success").GetBoolean()||reply.RootElement.GetProperty("run").GetString()!=id||reply.RootElement.GetProperty("rules").GetInt32()!=1||!blocked)throw new IOException("Narrow block unconfirmed.");
    await inspect.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));await inspectError.WaitAsync(TimeSpan.FromSeconds(3));if(inspect.ExitCode!=0)throw new IOException("Inspect exit failed.");
    ConfirmWatchers();Save("network.json",new{run=id,baseline=true,blocked,armedRules=1},true);
    await Task.Delay(Timeout.InfiniteTimeSpan);
}
void Save(string name,object value,bool replace=false)
{
    string path=Path.Combine(root,name),temporary=path+"."+Guid.NewGuid().ToString("N")+".new";
    try
    {
        using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
        {JsonSerializer.Serialize(file,value);file.Flush(true);}
        if(File.Exists(path))Check(path);
        File.Move(temporary,path,replace);
    }
    finally{if(File.Exists(temporary))File.Delete(temporary);}
}
static async Task<string> Capture(StreamReader reader)
{
    var text=new System.Text.StringBuilder();char[] buffer=new char[1024];int count;
    bool overflow=false;
    while((count=await reader.ReadAsync(buffer))>0){if(text.Length+count>8192)overflow=true;else if(!overflow)text.Append(buffer,0,count);}
    if(overflow)throw new IOException("Rescue reply budget exceeded.");return text.ToString();
}
static void Check(string path,bool ancestor=false)
{
    var item=Directory.Exists(path)?(FileSystemInfo)new DirectoryInfo(path):new FileInfo(path);
    if((item.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Rescue reparse path refused.");
    var acl=item is DirectoryInfo directory?(FileSystemSecurity)directory.GetAccessControl():((FileInfo)item).GetAccessControl();
    static bool Trusted(string? sid)=>sid is "S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    if(!Trusted(acl.GetOwner(typeof(SecurityIdentifier))?.Value))throw new UnauthorizedAccessException("Untrusted rescue owner.");
    long mutation=ancestor?0x500c0040:0x500d0156;
    foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
        if(rule.AccessControlType==AccessControlType.Allow&&(rule.PropagationFlags&PropagationFlags.InheritOnly)==0&&
            !Trusted(rule.IdentityReference.Value)&&(((long)rule.FileSystemRights&0xffffffff)&mutation)!=0)throw new UnauthorizedAccessException("Writable rescue path.");
}
internal sealed record Lease(string Run,int ControllerPid,long ControllerStartedTicks,DateTimeOffset CreatedUtc,DateTimeOffset DeadlineUtc,string GuardSha256,long JobHandle,int ChildPid,long ChildStartedTicks,bool EnableArm=false);
internal static class GuardChild
{
    internal static Process Start(ProcessStartInfo info,NativeProcessJob job,Action confirm)
        =>StartWith(info,confirm,job.AssignBlockedOnPrivateInput,RetireBlockedChild);
    // Test seams are internal only; production always uses the real job and
    // bounded retirement, with no caller-selected executables or RPC hooks.
    internal static Process StartWith(ProcessStartInfo info,Action confirm,Action<Process> attach,Func<Process,bool> retire)
    {
        confirm();if(!info.RedirectStandardInput||!info.RedirectStandardOutput||!info.RedirectStandardError||info.UseShellExecute)throw new IOException("Private guard pipes required.");
        var process=Process.Start(info)??throw new IOException("Guard failed to start.");
        try{attach(process);return process;}
        catch(Exception original)
        {
            Exception? cleanupFailure=null;bool stopped=false;
            try{stopped=retire(process);}catch(Exception error){cleanupFailure=error;}
            try{process.Dispose();}catch(Exception error){cleanupFailure??=error;}
            throw new GuardChildStartException(original,stopped&&cleanupFailure==null,cleanupFailure);
        }
    }
    internal static bool RetireBlockedChild(Process process)
    {
        if(!process.HasExited)process.Kill();
        return process.WaitForExit(5000)&&process.HasExited;
    }
    internal static async Task<JsonDocument> Request(Process guard,object request,bool keepOpen,Action confirm)
    {
        confirm();using var writeTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await guard.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(),writeTimeout.Token);
        if(!keepOpen)guard.StandardInput.Close();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var text=new System.Text.StringBuilder();char[] one=new char[1];
        while(true){int count=await guard.StandardOutput.ReadAsync(one.AsMemory(),timeout.Token);if(count==0)throw new IOException("Guard reply missing.");if(one[0]=='\n')break;if(text.Length>=8192)throw new IOException("Guard reply budget exceeded.");text.Append(one[0]);}
        return JsonDocument.Parse(text.ToString());
    }
}
internal sealed class GuardChildStartException:IOException
{
    internal bool CleanupConfirmed{get;}
    internal Exception? CleanupFailure{get;}
    internal GuardChildStartException(Exception original,bool confirmed,Exception? cleanupFailure)
        :base(confirmed?"Guard assignment failed; blocked child retired.":"Guard assignment failed; blocked child retirement is unconfirmed.",original)
    {CleanupConfirmed=confirmed;CleanupFailure=cleanupFailure;}
}
internal sealed class CleanupLease:IDisposable
{
    readonly FileStream file;
    CleanupLease(FileStream file){this.file=file;}
    internal static CleanupLease? Acquire(string root)
    {
        string path=Path.Combine(root,"cleanup.lock");var elapsed=Stopwatch.StartNew();
        do
        {
            if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Cleanup lock link refused.");
            try{return new(new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None));}
            catch(IOException){Thread.Sleep(50);}
        }while(elapsed.Elapsed<TimeSpan.FromSeconds(210));
        return null;
    }
    public void Dispose()=>file.Dispose();
}
