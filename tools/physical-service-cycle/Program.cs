using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Pipes;
using System.Text;
using System.Runtime.InteropServices;
using RoutePilot.Agent;

if(args.Length==1&&args[0]=="--job-fixture"){await Task.Delay(Timeout.Infinite);return;}
if(args.Length==1&&args[0]=="--self-test"){
    if(RefusalEvidence.Classify("The process cannot access the file because it is being used by another process.",@"C:\x\intent.json")!="KnownSharingViolationNoFileName"||
       RefusalEvidence.Classify("Процесс не может получить доступ к файлу, так как этот файл используется другим процессом.",@"C:\x\intent.json")!="KnownSharingViolationNoFileName"||
       RefusalEvidence.Classify("Cannot open C:\\x\\intent.json now.",@"C:\x\intent.json")!="IntentPath"||
       RefusalEvidence.Classify("some other failure",@"C:\x\intent.json")!="Other")throw new IOException("Refusal classification test failed.");
    string op=new string('a',32);
    bool Status(string json){using var doc=JsonDocument.Parse(json);return MaintenanceLease.ClassifyNativeStatus(doc.RootElement,op,7);}
    string state(string phase,string desired="false",string operation="",int generation=7)=>JsonSerializer.Serialize(new{Success=true,Native=new{phase,desired=desired=="true",operation=operation==""?op:operation,generation}});
    if(Status("{\"Success\":false,\"Native\":null}")||Status(state("Stopping"))||!Status(state("Disconnected")))throw new IOException("Readiness transition classification differs.");
    foreach(string bad in new[]{state("Disconnected","true"),state("Disconnected","false",new string('b',32)),state("Disconnected","false","",8),state("Unavailable")}){
        bool rejected=false;try{Status(bad);}catch(IOException){rejected=true;}if(!rejected)throw new IOException("Invalid readiness was treated as transient.");
    }
    string self=Environment.ProcessPath??throw new IOException("Test image unavailable.");
    using var job=new NativeProcessJob();using var process=job.StartOwned(self,new[]{"--job-fixture"},Path.GetDirectoryName(self)!);
    for(int i=0;i<30&&job.ReadUsage().ActiveProcesses==0&&!process.HasExited;i++)await Task.Delay(100);
    if(job.ReadUsage().ActiveProcesses<1||process.HasExited||!Service.IsInJob(process,job.DescriptorHandleValue))throw new IOException($"Test child did not enter the owned job: active={job.ReadUsage().ActiveProcesses}, exited={process.HasExited}.");
    await job.TerminateAsync(CancellationToken.None);await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    if(job.ReadUsage().ActiveProcesses!=0)throw new IOException("Test child survived job retirement.");
    Console.WriteLine("Process-only owned job retirement PASS; no service or network test.");return;
}
if(args.Length!=2||args[0] is not ("--controller" or "--primary" or "--backup" or "--actor"))throw new ArgumentException("Service cycle mode refused.");
string parent=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RoutePilot","Agent");
string root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));string name=Path.GetFileName(root);
if(!string.Equals(Path.GetDirectoryName(root),parent,StringComparison.OrdinalIgnoreCase)||!name.StartsWith("cita-cycle-",StringComparison.Ordinal)||name.Length!=43||name[11..].Any(c=>!char.IsAsciiHexDigit(c)||char.IsUpper(c)))throw new IOException("Cycle root refused.");
string id=name[11..];Check(root);for(var p=Directory.GetParent(root);p!=null;p=p.Parent)Check(p.FullName,true);
string image=Path.Combine(root,"Cita.PhysicalServiceCycle.exe");if(!string.Equals(Environment.ProcessPath,image,StringComparison.OrdinalIgnoreCase))throw new IOException("Cycle image differs.");
using var imageLease=Locked(image);using var planLease=Locked(Path.Combine(root,"plan.json"));
if(planLease.Length>8192)throw new IOException("Plan too large.");
var plan=JsonSerializer.Deserialize<Plan>(planLease,new JsonSerializerOptions{UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new IOException("Plan missing.");
if(plan.Schema!=1||plan.Run!=id||plan.UserSid is null||!System.Text.RegularExpressions.Regex.IsMatch(plan.UserSid,@"\AS-1-5-21-(\d+-){3}\d+\z")||plan.DeadlineUtc<=DateTimeOffset.UtcNow||plan.DeadlineUtc>DateTimeOffset.UtcNow.AddSeconds(120)||plan.AgentSha256 is not {Length:64}||plan.ManifestSha256 is not {Length:64}||plan.HelperSha256 is not {Length:64})throw new IOException("Cycle plan refused.");
string tokenSid=WindowsIdentity.GetCurrent().User?.Value??throw new UnauthorizedAccessException("Identity unavailable.");
if(args[0]=="--controller"){if(tokenSid!="S-1-5-18"&&tokenSid!=plan.UserSid)throw new UnauthorizedAccessException("Controller identity refused.");}else if(args[0]=="--actor"){if(tokenSid!="S-1-5-18"&&tokenSid!=plan.UserSid)throw new UnauthorizedAccessException("Actor identity refused.");}else if(tokenSid!="S-1-5-18")throw new UnauthorizedAccessException("Watcher identity refused.");
CheckMachine();if(!string.Equals(Hash(imageLease),plan.HelperSha256,StringComparison.Ordinal))throw new IOException("Helper hash differs.");
string agent=Path.Combine(parent,"RoutePilot.Agent.exe");using var agentLease=Verified(agent,plan.AgentSha256);
string appRoot=@"C:\Users\CITASTATION\AppData\Local\Programs\CITA Route\client";
using(var config=Locked(Path.Combine(parent,"agent-config.json"))){using var doc=JsonDocument.Parse(config);if(doc.RootElement.GetProperty("UserSid").GetString()!=plan.UserSid||!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(doc.RootElement.GetProperty("AppRoot").GetString()!)),appRoot,StringComparison.OrdinalIgnoreCase))throw new IOException("Owner or application root differs.");}
string clientManifest=Path.Combine(appRoot,"manifest.json");for(string? p=clientManifest;p!=null;p=Path.GetDirectoryName(p)){if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Client manifest path link refused.");}
using var manifestLease=new FileStream(clientManifest,FileMode.Open,FileAccess.Read,FileShare.Read);
if(Hash(manifestLease)!=plan.ManifestSha256)throw new IOException("Initial client manifest differs.");
string failureRole=args[0][2..];
string diagnosticStage="InitialReadiness";
AppDomain.CurrentDomain.UnhandledException+=(_,eventArgs)=>{
    try{
        var error=eventArgs.ExceptionObject as Exception;
        Save(Path.Combine(root,"failure-"+failureRole+".json"),new{run=id,role=failureRole,stage=diagnosticStage,errorType=error?.GetType().Name??"Unknown",hresult=error?.HResult??0,win32NativeCode=error is System.ComponentModel.Win32Exception w?w.NativeErrorCode:(int?)null});
    }catch{}
};
string intent=Path.Combine(parent,"native","intent.json");Check(intent);for(var p=new FileInfo(intent).Directory;p!=null;p=p.Parent)Check(p.FullName,true);
using var maintenance=MaintenanceLease.Open(intent);await maintenance.ConfirmServiceAsync(p=>Check(p));
if(args[0]=="--actor")
{
    diagnosticStage="StartRefusal";
    await TestStartRefusal(intent,root,id);await maintenance.ConfirmServiceAsync(p=>Check(p));
    using var descFile=Locked(Path.Combine(root,"controller.json"));using var desc=JsonDocument.Parse(descFile);int oldPid=desc.RootElement.GetProperty("servicePid").GetInt32();using var old=Process.GetProcessById(oldPid);_=old.Handle;
    if(old.HasExited||Service.QueryRunningPid()!=oldPid||old.StartTime.ToUniversalTime().Ticks!=desc.RootElement.GetProperty("serviceStart").GetInt64())throw new IOException("Service identity changed before stop.");
    // Durable fence precedes any SCM STOP. After actor death a watcher must
    // observe STOPPED, not mistake an in-flight stop for healthy RUNNING.
    Save(Path.Combine(root,"stop-request.json"),new{run=id,oldPid});
    diagnosticStage="SCMStop";Service.Stop();await old.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35));
    if(Service.State()!=1||!old.HasExited)throw new IOException("Service stop not confirmed.");
    Save(Path.Combine(root,"stopped.json"),new{run=id,oldPid,oldStopped=true,nativeStartRefusedWhileIntentWriteDenied=true});
    await Task.Delay(Timeout.Infinite);return;
}
if(args[0]=="--controller")
{
    diagnosticStage="Admission";
    using var job=new NativeProcessJob();using var self=Process.GetCurrentProcess();using var initialService=Process.GetProcessById(Service.QueryRunningPid());_=initialService.Handle;
    Save(Path.Combine(root,"controller.json"),new{run=id,pid=self.Id,start=self.StartTime.ToUniversalTime().Ticks,handle=job.DescriptorHandleValue,tokenSid,servicePid=initialService.Id,serviceStart=initialService.StartTime.ToUniversalTime().Ticks});
    await Until(()=>File.Exists(Path.Combine(root,"ready-primary.json"))&&File.Exists(Path.Combine(root,"ready-backup.json")),TimeSpan.FromSeconds(20));
    var held=new List<Process>();
    try{
        foreach(string role in new[]{"primary","backup"}){
            using var file=Locked(Path.Combine(root,"ready-"+role+".json"));using var doc=JsonDocument.Parse(file);var r=doc.RootElement;
            if(r.GetProperty("run").GetString()!=id||r.GetProperty("role").GetString()!=role||!r.GetProperty("jobHeld").GetBoolean()||!r.GetProperty("maintenanceHeld").GetBoolean()||r.GetProperty("tokenSid").GetString()!="S-1-5-18")throw new IOException("Watcher admission refused.");
            var process=Process.GetProcessById(r.GetProperty("pid").GetInt32());held.Add(process);_=process.Handle;
            if(process.HasExited||process.StartTime.ToUniversalTime().Ticks!=r.GetProperty("start").GetInt64()||!string.Equals(process.MainModule?.FileName,image,StringComparison.OrdinalIgnoreCase))throw new IOException("Watcher identity differs.");
        }
        await maintenance.ConfirmServiceAsync(p=>Check(p));
        if(held[0].Id==held[1].Id||held.Any(p=>p.HasExited)||!maintenance.ServiceStillMatches()||DateTimeOffset.UtcNow>=plan.DeadlineUtc.AddSeconds(-45))throw new IOException("Watcher admission expired.");
        diagnosticStage="ActorStart";using var actor=job.StartOwned(image,new[]{"--actor",root},root);
        while(!actor.HasExited&&DateTimeOffset.UtcNow<plan.DeadlineUtc&&held.All(p=>!p.HasExited))await Task.Delay(100);
        return;
    }finally{foreach(var p in held)p.Dispose();}
}
string roleName=args[0]=="--primary"?"primary":"backup";
using var descriptorFile=Locked(Path.Combine(root,"controller.json"));using var descriptor=JsonDocument.Parse(descriptorFile);var d=descriptor.RootElement;
string controllerSid=d.GetProperty("tokenSid").GetString()??throw new IOException("Controller token missing.");
if(d.GetProperty("run").GetString()!=id||(controllerSid!="S-1-5-18"&&controllerSid!=plan.UserSid))throw new IOException("Controller descriptor differs.");
using var controller=Process.GetProcessById(d.GetProperty("pid").GetInt32());_=controller.Handle;
if(controller.StartTime.ToUniversalTime().Ticks!=d.GetProperty("start").GetInt64()||!string.Equals(controller.MainModule?.FileName,image,StringComparison.OrdinalIgnoreCase))throw new IOException("Controller identity differs.");
int originalPid=d.GetProperty("servicePid").GetInt32();using var originalService=Process.GetProcessById(originalPid);_=originalService.Handle;
if(originalService.HasExited||originalService.StartTime.ToUniversalTime().Ticks!=d.GetProperty("serviceStart").GetInt64()||Service.QueryRunningPid()!=originalPid||!string.Equals(originalService.MainModule?.FileName,agent,StringComparison.OrdinalIgnoreCase))throw new IOException("Initial service identity differs.");
using var owned=NativeProcessJob.DuplicateFromHeldController(controller,d.GetProperty("handle").GetInt64());
using var watcher=Process.GetCurrentProcess();Save(Path.Combine(root,"ready-"+roleName+".json"),new{run=id,role=roleName,pid=watcher.Id,start=watcher.StartTime.ToUniversalTime().Ticks,jobHeld=true,maintenanceHeld=true,tokenSid});
// The actor is expected to stop SCM; its former service PID is not a liveness signal here.
while(!controller.HasExited&&DateTimeOffset.UtcNow<plan.DeadlineUtc)await Task.Delay(100);
if(!controller.HasExited){controller.Kill();await controller.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}
diagnosticStage="RetireJob";await owned.TerminateAsync(CancellationToken.None);uint active=owned.ReadUsage().ActiveProcesses;if(active!=0)throw new IOException("Cycle actor retirement unconfirmed.");
Save(Path.Combine(root,"retired-"+roleName+".json"),new{run=id,role=roleName,controllerStopped=controller.HasExited,jobActiveAfter=active});
diagnosticStage="RecoveryLock";
FileStream? recoveryLock=null;var recoveryBudget=Stopwatch.StartNew();
while(recoveryLock==null){try{recoveryLock=new FileStream(Path.Combine(root,"recovery.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException){if(recoveryBudget.Elapsed>=TimeSpan.FromSeconds(45))throw new IOException("Cycle recovery lock unavailable.");await Task.Delay(100);}}
using(recoveryLock)
{
string otherResult=Path.Combine(root,"result-"+(roleName=="primary"?"backup":"primary")+".json");bool alreadyRecovered=false;
if(File.Exists(otherResult)){using var f=Locked(otherResult);using var d2=JsonDocument.Parse(f);alreadyRecovered=d2.RootElement.GetProperty("run").GetString()==id&&d2.RootElement.GetProperty("success").GetBoolean()&&d2.RootElement.GetProperty("jobActiveAfter").GetUInt32()==0;if(!alreadyRecovered)throw new IOException("Prior recovery proof refused.");}
string requested=Path.Combine(root,"stop-request.json");
if(!alreadyRecovered&&File.Exists(requested)){
    diagnosticStage="SCMStop";
    using var file=Locked(requested);using var request=JsonDocument.Parse(file);if(request.RootElement.GetProperty("run").GetString()!=id||request.RootElement.GetProperty("oldPid").GetInt32()!=originalPid)throw new IOException("Stop request identity differs.");
    var budget=Stopwatch.StartNew();while(Service.State()!=1){
        if(Service.State()==4){if(originalService.HasExited||Service.QueryRunningPid()!=originalPid)throw new IOException("A different service is running; recovery refused.");Service.StopIfRunning(originalPid);}
        if(budget.Elapsed>=TimeSpan.FromSeconds(35)||recoveryBudget.Elapsed>=TimeSpan.FromSeconds(40))throw new IOException("Pending SCM stop remains unconfirmed; no recovery success can be claimed.");await Task.Delay(100);
    }
    if(!originalService.HasExited)await originalService.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
}
if(!alreadyRecovered&&!File.Exists(requested)&&(originalService.HasExited||Service.QueryRunningPid()!=originalPid))throw new IOException("Unplanned service change; recovery unconfirmed.");
diagnosticStage="SCMStart";var remaining=TimeSpan.FromSeconds(45)-recoveryBudget.Elapsed;if(remaining<=TimeSpan.Zero)throw new IOException("Cycle recovery deadline exceeded.");Service.StartAndWait(remaining);
diagnosticStage="StartupReady";int readyPid=Service.QueryRunningPid();using var readyProcess=Process.GetProcessById(readyPid);_=readyProcess.Handle;
long readyStart=readyProcess.StartTime.ToUniversalTime().Ticks;bool confirmed=false;
while(recoveryBudget.Elapsed<TimeSpan.FromSeconds(37)){
    if(readyProcess.HasExited||Service.QueryRunningPid()!=readyPid||readyProcess.StartTime.ToUniversalTime().Ticks!=readyStart||!string.Equals(readyProcess.MainModule?.FileName,agent,StringComparison.OrdinalIgnoreCase))throw new IOException("Restarted service identity changed.");
    try{using var fresh=MaintenanceLease.Open(intent);await fresh.ConfirmServiceAsync(p=>Check(p));confirmed=true;break;}
    catch(PhysicalNativeNotReady){await Task.Delay(200);}
    catch(PhysicalNativeStatusTimeout){await Task.Delay(200);}
}
if(!confirmed||readyProcess.HasExited||Service.QueryRunningPid()!=readyPid)throw new IOException("Restarted native service readiness unconfirmed.");
Save(Path.Combine(root,"result-"+roleName+".json"),new{run=id,role=roleName,success=true,jobActiveAfter=active,servicePid=readyPid,scope="Service restart only; no upgrade, TUN or power-loss proof"});
}

static async Task TestStartRefusal(string intentPath,string root,string run)
{
    int writeCode=0;try{using var blocked=new FileStream(intentPath,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);}catch(IOException e){writeCode=e.HResult&0xffff;}
    int deleteCode=MaintenanceLease.TestDeleteOpen(intentPath);
    using var before=await ReadRpc("native-status",null);var baseline=before.RootElement;
    var state=baseline.GetProperty("Native");
    bool beforePhaseValid=baseline.GetProperty("Success").GetBoolean()&&state.GetProperty("phase").GetString()=="Disconnected"&&state.GetProperty("desired").ValueKind==JsonValueKind.False;
    if(writeCode is not (32 or 33)||deleteCode is not (32 or 33)||!beforePhaseValid){
        Save(Path.Combine(root,"refusal-evidence.json"),new{run,writeCode,deleteCode,beforePhaseValid,replySuccess=(bool?)null,phaseMatches=false,operationMatches=false,generationMatches=false,desiredMatches=false,messageClassificationOnly="NotAttempted"});
        throw new IOException("Protected intent preconditions unconfirmed.");
    }
    string operation=state.GetProperty("operation").GetString()??throw new IOException("Native operation missing.");ulong generation=state.GetProperty("generation").GetUInt64();
    using var reply=await ReadRpc("native-start",operation);var result=reply.RootElement;var native=result.GetProperty("Native");
    bool success=result.GetProperty("Success").GetBoolean(),phase=native.GetProperty("phase").GetString()=="Disconnected",desired=native.GetProperty("desired").ValueKind==JsonValueKind.False,op=native.GetProperty("operation").GetString()==operation,gen=native.GetProperty("generation").GetUInt64()==generation;
    string classification=RefusalEvidence.Classify(result.GetProperty("Message").GetString(),intentPath);
    Save(Path.Combine(root,"refusal-evidence.json"),new{run,writeCode,deleteCode,beforePhaseValid,replySuccess=success,phaseMatches=phase,operationMatches=op,generationMatches=gen,desiredMatches=desired,messageClassificationOnly=classification,causalFileUnproven=classification!="IntentPath"});
    if(writeCode is not (32 or 33)||deleteCode is not (32 or 33)||!beforePhaseValid||success||!phase||!desired||!op||!gen)throw new IOException("Protected intent refusal unconfirmed.");
}
static async Task<JsonDocument> ReadRpc(string name,string? operation)
{
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));using var pipe=new NamedPipeClientStream(".","RoutePilot.Rescue.v1",PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);
    await pipe.ConnectAsync(timeout.Token);Service.VerifyPipeServer(pipe);byte[] command=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{Command=name,RunId=operation})+"\n");await pipe.WriteAsync(command,timeout.Token);await pipe.FlushAsync(timeout.Token);
    var bytes=new List<byte>();byte[] one=new byte[1];while(true){int count=await pipe.ReadAsync(one,timeout.Token);if(count==0)throw new IOException("Start response missing.");if(one[0]==10)break;if(bytes.Count>=65536)throw new IOException("Start response too large.");bytes.Add(one[0]);}
    return JsonDocument.Parse(bytes.ToArray());
}
static void CheckMachine()
{
    var type=Type.GetTypeFromProgID("WbemScripting.SWbemLocator")??throw new IOException("WMI unavailable.");
    dynamic locator=Activator.CreateInstance(type)!;dynamic service=locator.ConnectServer(".","root\\cimv2");dynamic matches=service.ExecQuery("SELECT UUID FROM Win32_ComputerSystemProduct");
    if((int)matches.Count!=1||!string.Equals(((string)matches.ItemIndex(0).UUID).Trim(),"F8798200-12FB-11F1-8F47-A104BEE59100",StringComparison.OrdinalIgnoreCase))throw new IOException("Physical station differs.");
}
static FileStream Locked(string path){Check(path);return new(path,FileMode.Open,FileAccess.Read,FileShare.Read);}
static FileStream Verified(string path,string sha){if(sha is not {Length:64}||sha.Any(c=>!char.IsAsciiHexDigit(c)))throw new IOException("Digest invalid.");var file=Locked(path);try{if(!string.Equals(Hash(file),sha,StringComparison.OrdinalIgnoreCase))throw new IOException("Protected payload differs.");return file;}catch{file.Dispose();throw;}}
static string Hash(FileStream f){f.Position=0;string hash=Convert.ToHexString(SHA256.HashData(f));f.Position=0;return hash;}
static string HashFile(string path){using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);return Hash(f);}
static void Check(string path,bool ancestor=false)
{
    var item=Directory.Exists(path)?(FileSystemInfo)new DirectoryInfo(path):new FileInfo(path);
    if((item.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Protected path link refused.");
    var acl=item is DirectoryInfo d?(FileSystemSecurity)d.GetAccessControl():((FileInfo)item).GetAccessControl();
    static bool Trusted(string? sid)=>sid is "S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    if(!Trusted(acl.GetOwner(typeof(SecurityIdentifier))?.Value))throw new UnauthorizedAccessException("Protected owner refused.");
    long mutation=ancestor?0x500c0040:0x500d0156;
    foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))if(rule.AccessControlType==AccessControlType.Allow&&(rule.PropagationFlags&PropagationFlags.InheritOnly)==0&&!Trusted(rule.IdentityReference.Value)&&(((long)rule.FileSystemRights&0xffffffff)&mutation)!=0)throw new UnauthorizedAccessException("Protected path writable.");
}
static void Save(string path,object value){string temp=path+"."+Guid.NewGuid().ToString("N")+".new";try{using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){JsonSerializer.Serialize(f,value);f.Flush(true);}File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}}
static async Task Until(Func<bool> condition,TimeSpan limit){var timer=Stopwatch.StartNew();while(!condition()){if(timer.Elapsed>limit)throw new TimeoutException("Watcher readiness unconfirmed.");await Task.Delay(100);}}

internal sealed record Plan(int Schema,string Run,string UserSid,string AgentSha256,string ManifestSha256,string HelperSha256,DateTimeOffset DeadlineUtc);
internal static class RefusalEvidence
{
    internal static string Classify(string? message,string intent)
    {
        if(message?.Contains(intent,StringComparison.OrdinalIgnoreCase)==true)return "IntentPath";
        return message?.Trim() is "The process cannot access the file because it is being used by another process." or "Процесс не может получить доступ к файлу, так как этот файл используется другим процессом."
            ?"KnownSharingViolationNoFileName":"Other";
    }
}
internal static class Service
{
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool result);
    internal static bool IsInJob(Process process,long job){if(!IsProcessInJob(process.Handle,new IntPtr(job),out bool result))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());return result;}
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetNamedPipeServerProcessId(IntPtr pipe,out uint pid);
    internal static void VerifyPipeServer(NamedPipeClientStream pipe){if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out uint pid)||pid!=(uint)QueryRunningPid())throw new IOException("Unexpected native pipe server.");}
    [StructLayout(LayoutKind.Sequential)]struct Status{public uint Type,State,Controls,ExitCode,SpecificExitCode,Checkpoint,WaitHint,Pid,Flags;}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenSCManagerW(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenServiceW(IntPtr manager,string service,uint access);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool QueryServiceStatusEx(IntPtr service,int level,out Status status,uint size,out uint needed);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool ControlService(IntPtr service,uint control,out Status status);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool StartServiceW(IntPtr service,uint count,IntPtr args);
    [DllImport("advapi32.dll")]static extern bool CloseServiceHandle(IntPtr handle);
    static T With<T>(uint access,Func<IntPtr,T> body){IntPtr m=OpenSCManagerW(null,null,1);if(m==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());try{IntPtr s=OpenServiceW(m,"RoutePilotRescue",access);if(s==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());try{return body(s);}finally{CloseServiceHandle(s);}}finally{CloseServiceHandle(m);}}
    static Status Query(IntPtr s){if(!QueryServiceStatusEx(s,0,out var x,(uint)Marshal.SizeOf<Status>(),out _))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());return x;}
    internal static uint State()=>With(4,s=>Query(s).State);
    internal static int QueryRunningPid()=>With(4,s=>{var x=Query(s);if(x.State!=4||x.Pid==0)throw new IOException("Service not running.");return checked((int)x.Pid);});
    internal static void Stop()=>With(0x24,s=>{var x=Query(s);if(x.State!=4)throw new IOException("Service not running before planned stop.");if(!ControlService(s,1,out _))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());return 0;});
    internal static void StopIfRunning(int expectedPid)=>With(0x24,s=>{var x=Query(s);if(x.State!=4)return 0;if(x.Pid!=(uint)expectedPid)throw new IOException("Service PID changed before recovery stop.");if(!ControlService(s,1,out _)){int e=Marshal.GetLastWin32Error();if(e!=1061&&e!=1062)throw new System.ComponentModel.Win32Exception(e);}return 0;});
    internal static void StartAndWait(TimeSpan budget)=>With(0x14,s=>{var timer=Stopwatch.StartNew();while(timer.Elapsed<budget){var state=Query(s).State;if(state==4)return 0;if(state==1&&!StartServiceW(s,0,IntPtr.Zero)){int error=Marshal.GetLastWin32Error();if(error!=1056&&error!=1053)throw new System.ComponentModel.Win32Exception(error);}Thread.Sleep(200);}throw new TimeoutException("Service restart unconfirmed.");});
}
