using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using RoutePilot.Agent;

if(args.Length!=2||args[0] is not ("--controller" or "--primary" or "--backup" or "--fixture"))throw new ArgumentException("Physical supervisor mode refused.");
string parent=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RoutePilot","Agent");
string root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));string name=Path.GetFileName(root);
if(!string.Equals(Path.GetDirectoryName(root),parent,StringComparison.OrdinalIgnoreCase)||!name.StartsWith("cita-installer-",StringComparison.Ordinal)||name.Length!=47||name[15..].Any(c=>!char.IsAsciiHexDigit(c)||char.IsUpper(c)))throw new IOException("Physical supervisor root refused.");
string id=name[15..];Check(root);for(var p=Directory.GetParent(root);p!=null;p=p.Parent)Check(p.FullName,true);
string image=Path.Combine(root,"Cita.InstallerPhysical.exe");if(!string.Equals(Environment.ProcessPath,image,StringComparison.OrdinalIgnoreCase))throw new IOException("Physical supervisor image differs.");
using var imageLease=Locked(image);using var planLease=Locked(Path.Combine(root,"plan.json"));
if(planLease.Length>8192)throw new IOException("Plan budget exceeded.");
var plan=JsonSerializer.Deserialize<PrivatePlan>(planLease,new JsonSerializerOptions{UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new IOException("Plan missing.");
string actual=@"C:\Users\CITASTATION\AppData\Local\Programs\CITA Route\client";
if(plan.Schema!=1||plan.Run!=id||plan.Mode is not ("Scratch" or "Physical")||!string.Equals(plan.AppRoot,actual,StringComparison.OrdinalIgnoreCase)||plan.UserSid is null||!System.Text.RegularExpressions.Regex.IsMatch(plan.UserSid,@"\AS-1-5-21-(\d+-){3}\d+\z")||plan.DeadlineUtc>DateTimeOffset.UtcNow.AddMinutes(5)||plan.PreviousJournalId is null||plan.PreviousJournalId.Length!=32||plan.PreviousJournalId.Any(c=>!char.IsAsciiHexDigit(c)||char.IsUpper(c)))throw new IOException("Physical plan refused.");
string tokenSid=WindowsIdentity.GetCurrent().User?.Value??throw new UnauthorizedAccessException("Process identity unavailable.");
if(args[0] is "--controller" or "--fixture")
{
    if(tokenSid!=plan.UserSid)throw new UnauthorizedAccessException("Installer controller must run as the installation owner.");
}
else if(tokenSid!="S-1-5-18")throw new UnauthorizedAccessException("Installer watchers must run as LocalSystem.");
CheckMachine();
using var configLease=Locked(Path.Combine(parent,"agent-config.json"));if(configLease.Length>16384)throw new IOException("Protected config budget exceeded.");
using(var config=JsonDocument.Parse(configLease)){if(config.RootElement.GetProperty("UserSid").GetString()!=plan.UserSid||!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.RootElement.GetProperty("AppRoot").GetString()!)),actual,StringComparison.OrdinalIgnoreCase))throw new IOException("Protected owner differs.");}
using var worker=Verified(Path.Combine(root,"RestoreWorker.ps1"),plan.WorkerSha256);
using var transaction=Verified(Path.Combine(root,"InstallationTransaction.ps1"),plan.TransactionSha256);
using var invoker=Verified(Path.Combine(root,"Invoke-PhysicalRestore.ps1"),plan.InvokeSha256);
using var manifest=Verified(Path.Combine(root,"payload-manifest.json"),plan.PayloadManifestSha256);
using var previousState=Verified(Path.Combine(root,"previous-state.json"),plan.PreviousStateSha256);
if(!string.Equals(Hash(imageLease),plan.HelperSha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Helper digest differs.");
using var installer=plan.Mode=="Physical"?Verified(Path.Combine(root,"setup.exe"),plan.InstallerSha256):null;
string target=plan.Mode=="Scratch"?Path.Combine(root,"scratch","client"):actual;
if(args[0]=="--fixture")
{
    if(plan.Mode!="Scratch")throw new IOException("Fixture requires scratch mode.");
    string runtime=Path.Combine(target,"runtime"),backup=Path.Combine(runtime,"installation-backup-"+id);Directory.CreateDirectory(backup);
    string old=Path.Combine(backup,"example.txt");File.WriteAllText(old,"old");
    Save(Path.Combine(runtime,"installation-transaction.json"),new{schema=1,id,root=target,userSid=plan.UserSid,backup,phase="Armed",files=new[]{new{name="example.txt",existed=true,sha256=HashFile(old)}},version="0.0.0",incomingManifestHash=plan.PayloadManifestSha256});
    File.WriteAllText(Path.Combine(target,"example.txt"),"partial");Save(Path.Combine(root,"dirty.json"),new{run=id});await Task.Delay(Timeout.Infinite);return;
}
using var maintenance=await OpenMaintenance(parent);
if(args[0]=="--controller")
{
    CheckPrevious(target,plan,previousState);
    using var job=new NativeProcessJob();using var self=Process.GetCurrentProcess();
    Save(Path.Combine(root,"controller.json"),new{run=id,pid=self.Id,start=self.StartTime.ToUniversalTime().Ticks,handle=job.DescriptorHandleValue,tokenSid});
    await Until(()=>File.Exists(Path.Combine(root,"ready-primary.json"))&&File.Exists(Path.Combine(root,"ready-backup.json")),TimeSpan.FromSeconds(20));
    var held=new List<Process>();
    try
    {
        foreach(string role in new[]{"primary","backup"})
        {
            using var file=Locked(Path.Combine(root,"ready-"+role+".json"));using var doc=JsonDocument.Parse(file);var r=doc.RootElement;
            if(r.GetProperty("run").GetString()!=id||r.GetProperty("role").GetString()!=role||!r.GetProperty("jobHeld").GetBoolean()||!r.GetProperty("maintenanceHeld").GetBoolean())throw new IOException("Watcher admission refused.");
            var process=Process.GetProcessById(r.GetProperty("pid").GetInt32());held.Add(process);_=process.Handle;
            if(process.HasExited||process.StartTime.ToUniversalTime().Ticks!=r.GetProperty("start").GetInt64()||!string.Equals(process.MainModule?.FileName,image,StringComparison.OrdinalIgnoreCase))throw new IOException("Watcher identity differs.");
        }
        await maintenance.ConfirmServiceAsync(p=>Check(p));
        if(held[0].Id==held[1].Id||held.Any(p=>p.HasExited)||!maintenance.ServiceStillMatches()||DateTimeOffset.UtcNow>=plan.DeadlineUtc.AddSeconds(-10))throw new IOException("Watcher admission expired.");
        string app=plan.Mode=="Scratch"?image:Path.Combine(root,"setup.exe");
        var command=plan.Mode=="Scratch"?new[]{"--fixture",root}:new[]{"/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/CONFIRMVERSION=0.6.9.0","/TRANSACTIONID="+id,"/DIR="+Path.GetDirectoryName(actual),"/LOG="+Path.Combine(root,"setup.log")};
        using var child=job.StartOwned(app,command,root);
        while(!child.HasExited&&DateTimeOffset.UtcNow<plan.DeadlineUtc&&held.All(p=>!p.HasExited)&&maintenance.ServiceStillMatches())await Task.Delay(100);
        // Exit makes watchers retire the entire job before touching files.
        return;
    }
    finally{foreach(var p in held)p.Dispose();}
}
string roleName=args[0]=="--primary"?"primary":"backup";
using var descriptorFile=Locked(Path.Combine(root,"controller.json"));using var descriptor=JsonDocument.Parse(descriptorFile);var d=descriptor.RootElement;
if(d.GetProperty("run").GetString()!=id)throw new IOException("Controller descriptor differs.");
using var controller=Process.GetProcessById(d.GetProperty("pid").GetInt32());_=controller.Handle;
if(controller.StartTime.ToUniversalTime().Ticks!=d.GetProperty("start").GetInt64()||!string.Equals(controller.MainModule?.FileName,image,StringComparison.OrdinalIgnoreCase))throw new IOException("Controller identity differs.");
using var owned=NativeProcessJob.DuplicateFromHeldController(controller,d.GetProperty("handle").GetInt64());
using var watcher=Process.GetCurrentProcess();Save(Path.Combine(root,"ready-"+roleName+".json"),new{run=id,role=roleName,pid=watcher.Id,start=watcher.StartTime.ToUniversalTime().Ticks,jobHeld=true,maintenanceHeld=true,tokenSid});
while(!controller.HasExited&&DateTimeOffset.UtcNow<plan.DeadlineUtc&&maintenance.ServiceStillMatches())await Task.Delay(100);
if(!controller.HasExited){controller.Kill();await controller.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}
await owned.TerminateAsync(CancellationToken.None);uint active=owned.ReadUsage().ActiveProcesses;if(active!=0)throw new IOException("Installer job retirement unconfirmed.");
Save(Path.Combine(root,"retired-"+roleName+".json"),new{run=id,controllerStopped=controller.HasExited,jobActiveAfter=active});
string ps=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
var timer=Stopwatch.StartNew();
while(timer.Elapsed<TimeSpan.FromSeconds(35))
{
    var info=new ProcessStartInfo(ps){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
    foreach(string arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",Path.Combine(root,"Invoke-PhysicalRestore.ps1"),"-RunRoot",root,"-Role",roleName})info.ArgumentList.Add(arg);
    using var restoreJob=new NativeProcessJob();using var child=restoreJob.StartWithPipes(info);
    var stdout=child.Output.ReadToEndAsync();var stderr=child.Error.ReadToEndAsync();
    try
    {
        await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));string text=await stdout.WaitAsync(TimeSpan.FromSeconds(2));await stderr.WaitAsync(TimeSpan.FromSeconds(2));
        await restoreJob.TerminateAsync(CancellationToken.None);
        if(child.Process.ExitCode==0)
        {
            using var outcome=JsonDocument.Parse(text);string? kind=outcome.RootElement.GetProperty("outcome").GetString();
            if(!outcome.RootElement.GetProperty("success").GetBoolean()||kind is not ("Committed" or "RolledBack" or "PreviousCommitVerified"))throw new IOException("Recovery response refused.");
            Save(Path.Combine(root,"result-"+roleName+".json"),new{run=id,role=roleName,success=true,outcome=kind,jobActiveAfter=active,scope="public client files only"});return;
        }
    }
    catch(TimeoutException){await restoreJob.TerminateAsync(CancellationToken.None);if(restoreJob.ReadUsage().ActiveProcesses!=0)throw new IOException("Timed-out restore job retirement unconfirmed.");}
    finally{if(!child.Process.HasExited)await restoreJob.TerminateAsync(CancellationToken.None);}
    await Task.Delay(250);
}
throw new IOException("Public-file recovery remains unconfirmed.");

static async Task<MaintenanceLease> OpenMaintenance(string parent)
{
    string path=Path.Combine(parent,"native","intent.json");Check(path);for(var p=new FileInfo(path).Directory;p!=null;p=p.Parent)Check(p.FullName,true);
    var lease=MaintenanceLease.Open(path);try{await lease.ConfirmServiceAsync(p=>Check(p));return lease;}catch{lease.Dispose();throw;}
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
static void CheckPrevious(string root,PrivatePlan plan,FileStream previousState)
{
    // Refuse rather than guess if the previous public installation cannot be
    // fully tied to the protected baseline before an installer can start.
    var manifest=Path.Combine(root,"manifest.json");var journal=Path.Combine(root,"runtime","installation-transaction.json");
    CheckUnlinked(manifest,root);CheckUnlinked(journal,root);
    if(!string.Equals(HashFile(manifest),plan.PreviousManifestSha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Previous manifest differs.");
    using var state=JsonDocument.Parse(File.ReadAllText(journal));var j=state.RootElement;
    if(j.GetProperty("id").GetString()!=plan.PreviousJournalId||j.GetProperty("phase").GetString()!="Committed"||j.GetProperty("userSid").GetString()!=plan.UserSid||!string.Equals(j.GetProperty("root").GetString(),root,StringComparison.OrdinalIgnoreCase)||!string.Equals(j.GetProperty("incomingManifestHash").GetString(),plan.PreviousManifestSha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Previous journal differs.");
    using var data=JsonDocument.Parse(File.ReadAllText(manifest));var files=data.RootElement.GetProperty("files");if(files.GetArrayLength() is <20 or >4096)throw new IOException("Previous inventory refused.");
    var expected=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"manifest.json"};
    foreach(var file in files.EnumerateArray())
    {
        string path=file.GetProperty("path").GetString()!;if(path.Equals("force-direct.txt",StringComparison.OrdinalIgnoreCase)||path.Equals("force-vpn.txt",StringComparison.OrdinalIgnoreCase))continue;
        _=PublicPath(root,path);expected.Add(path.Replace('/','\\'));
    }
    if(previousState.Length>4*1024*1024)throw new IOException("Previous state budget exceeded.");previousState.Position=0;
    using var snapshot=JsonDocument.Parse(previousState);var s=snapshot.RootElement;
    if(s.GetProperty("schema").GetInt32()!=1||!string.Equals(s.GetProperty("manifestSha256").GetString(),plan.PreviousManifestSha256,StringComparison.OrdinalIgnoreCase)||s.GetProperty("journalId").GetString()!=plan.PreviousJournalId)throw new IOException("Previous state identity differs.");
    var entries=s.GetProperty("files");if(entries.GetArrayLength()>4097)throw new IOException("Previous state count exceeded.");
    foreach(var entry in entries.EnumerateArray())
    {
        string path=entry.GetProperty("name").GetString()!;if(!expected.Remove(path.Replace('/','\\')))throw new IOException("Previous state inventory differs.");
        string full=PublicPath(root,path);CheckUnlinked(full,root);bool exists=File.Exists(full);
        if(entry.GetProperty("existed").GetBoolean()){if(!exists||new FileInfo(full).Length!=entry.GetProperty("bytes").GetInt64()||!string.Equals(HashFile(full),entry.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase))throw new IOException("Previous public file differs.");}
        else if(exists||Directory.Exists(full))throw new IOException("Unexpected previous public file.");
    }
    if(expected.Count!=0)throw new IOException("Previous state inventory incomplete.");
}
static string PublicPath(string root,string path)
{
    if(string.IsNullOrEmpty(path)||Path.IsPathRooted(path)||path.Contains(':')||path.Split('\\','/').Any(x=>x is "" or "." or "..")||path.EndsWith(".conf",StringComparison.OrdinalIgnoreCase)||System.Text.RegularExpressions.Regex.IsMatch(path.Replace('/','\\'),@"\A(?:data|private|logs|artifacts)(?:\\|\z)|\Aruntime\\(?:updates|installation-backup-[^\\]+)(?:\\|\z)|\Aruntime\\(?:installation-transaction\.json|installation-maintenance\.lock)\z",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new IOException("Previous file path refused.");
    string full=Path.GetFullPath(Path.Combine(root,path));if(!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Previous file escaped root.");return full;
}
static void CheckUnlinked(string path,string root)
{for(string? p=path;p!=null&&p.Length>=root.Length;p=Path.GetDirectoryName(p))if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Previous path link refused.");}
internal sealed record PrivatePlan(int Schema,string Run,string Mode,DateTimeOffset DeadlineUtc,string UserSid,string AppRoot,string HelperSha256,string WorkerSha256,string TransactionSha256,string InvokeSha256,string PayloadManifestSha256,string InstallerSha256,string PreviousManifestSha256,string PreviousJournalId,string PreviousStateSha256);
