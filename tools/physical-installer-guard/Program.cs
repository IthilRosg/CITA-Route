using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using RoutePilot.Agent;

// Scratch fixture only. No production client paths, privileged setup, network,
// maintenance RPC, or installer support is exposed by this executable.
string workspace=Path.GetFullPath(Environment.CurrentDirectory);
string allowed=Path.GetFullPath(Path.Combine(workspace,"runtime","physical-installer-guard-tests"));
string exe=Environment.ProcessPath??throw new IOException("Missing test executable.");
string tools=Path.Combine(workspace,"tools","physical-installer-guard");
if(args.Length==1&&args[0]=="--self-test"){await SelfTest();return;}
if(args.Length!=2||args[0] is not ("--controller" or "--primary" or "--backup" or "--fixture"))throw new ArgumentException("Scratch mode refused.");
string id=args[1];if(id.Length!=32||id.Any(c=>!char.IsAsciiHexDigit(c)||char.IsUpper(c)))throw new IOException("Scratch ID refused.");
string run=Path.Combine(allowed,id),client=Path.Combine(run,"client");
foreach(string path in new[]{allowed,run,client})if(!Directory.Exists(path)||(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Scratch path refused.");
using var manifestLease=OpenChecked(Path.Combine(run,"payload-manifest.json"));
using var workerLease=OpenChecked(Path.Combine(run,"RestoreWorker.ps1"));
using var transactionLease=OpenChecked(Path.Combine(run,"InstallationTransaction.ps1"));
using var invocationLease=OpenChecked(Path.Combine(run,"Invoke-ScratchRestore.ps1"));
using var planLease=OpenChecked(Path.Combine(run,"plan.json"));
var plan=JsonSerializer.Deserialize<Plan>(planLease)??throw new IOException("Scratch plan missing.");
if(plan.Run!=id||plan.ExeSha!=Hash(exe)||plan.WorkerSha!=HashStream(workerLease)||plan.TransactionSha!=HashStream(transactionLease)||plan.InvokeSha!=HashStream(invocationLease)||plan.ManifestSha!=HashStream(manifestLease)||plan.DeadlineUtc>DateTimeOffset.UtcNow.AddMinutes(2))throw new IOException("Scratch plan identity refused.");
if(args[0]=="--fixture")
{
    string runtime=Path.Combine(client,"runtime"),backup=Path.Combine(runtime,"installation-backup-"+id);Directory.CreateDirectory(backup);
    string old=Path.Combine(backup,"example.txt");File.WriteAllText(old,"old");
    var journal=new{schema=1,id,root=client,userSid="S-1-5-21-1-2-3-1001",backup,phase="Armed",files=new[]{new{name="example.txt",existed=true,sha256=Hash(old)}},version="0.0.0",incomingManifestHash=new string('0',64)};
    Save(Path.Combine(runtime,"installation-transaction.json"),journal);
    File.WriteAllText(Path.Combine(client,"example.txt"),"partially-updated");
    Save(Path.Combine(run,"dirty.json"),new{run=id});await Task.Delay(Timeout.Infinite);return;
}
if(args[0]=="--controller")
{
    using var job=new NativeProcessJob();using var self=Process.GetCurrentProcess();
    Save(Path.Combine(run,"controller.json"),new{pid=self.Id,start=self.StartTime.ToUniversalTime().Ticks,handle=job.DescriptorHandleValue,run=id});
    await Until(()=>File.Exists(Path.Combine(run,"primary.json"))&&File.Exists(Path.Combine(run,"backup.json")),TimeSpan.FromSeconds(10));
    var watchers=new List<Process>();
    try
    {
        foreach(string role in new[]{"primary","backup"})
        {
            using var f=OpenChecked(Path.Combine(run,role+".json"));using var doc=JsonDocument.Parse(f);var r=doc.RootElement;
            if(r.GetProperty("run").GetString()!=id||!r.GetProperty("jobHeld").GetBoolean())throw new IOException("Scratch watcher not ready.");
            var held=Process.GetProcessById(r.GetProperty("pid").GetInt32());watchers.Add(held);_=held.Handle;
            if(held.HasExited||held.StartTime.ToUniversalTime().Ticks!=r.GetProperty("start").GetInt64()||!string.Equals(held.MainModule?.FileName,exe,StringComparison.OrdinalIgnoreCase))throw new IOException("Scratch watcher identity differs.");
        }
        if(watchers[0].Id==watchers[1].Id||watchers.Any(p=>p.HasExited)||DateTimeOffset.UtcNow>=plan.DeadlineUtc)throw new IOException("Scratch watcher admission refused.");
        using var fixture=job.StartOwned(exe,new[]{"--fixture",id},workspace);
        await Task.Delay(Timeout.Infinite);return;
    }
    finally{foreach(var held in watchers)held.Dispose();}
}
using var controllerFile=OpenChecked(Path.Combine(run,"controller.json"));
using var descriptor=JsonDocument.Parse(controllerFile);var data=descriptor.RootElement;
if(data.GetProperty("run").GetString()!=id)throw new IOException("Scratch descriptor differs.");
using var controller=Process.GetProcessById(data.GetProperty("pid").GetInt32());_=controller.Handle;
if(controller.StartTime.ToUniversalTime().Ticks!=data.GetProperty("start").GetInt64()||!string.Equals(controller.MainModule?.FileName,exe,StringComparison.OrdinalIgnoreCase))throw new IOException("Scratch controller differs.");
using var owned=NativeProcessJob.DuplicateFromHeldController(controller,data.GetProperty("handle").GetInt64());
using var watcher=Process.GetCurrentProcess();string roleName=args[0]=="--primary"?"primary":"backup";
Save(Path.Combine(run,roleName+".json"),new{run=id,pid=watcher.Id,start=watcher.StartTime.ToUniversalTime().Ticks,jobHeld=true});
while(!controller.HasExited&&DateTimeOffset.UtcNow<plan.DeadlineUtc)await Task.Delay(100);
if(!controller.HasExited){controller.Kill();await controller.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}
await owned.TerminateAsync(CancellationToken.None);
if(owned.ReadUsage().ActiveProcesses!=0)throw new IOException("Scratch job retirement unconfirmed.");
if(roleName=="backup")await Task.Delay(1000); // Deterministic scratch fault injection only.
string ps=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
var retry=Stopwatch.StartNew();
while(retry.Elapsed<TimeSpan.FromSeconds(20))
{
    var launch=new ProcessStartInfo(ps){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
    foreach(string arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",Path.Combine(run,"Invoke-ScratchRestore.ps1"),"-RunRoot",run,"-Role",roleName})launch.ArgumentList.Add(arg);
    // The helper holds restore.lock; a separate kill-on-close job prevents it
    // outliving a crashed watcher. The other watcher retries the journal.
    using var restoreJob=new NativeProcessJob();
    using var child=restoreJob.StartWithPipes(launch);var restore=child.Process;
    var stdout=child.Output.ReadToEndAsync();var stderr=child.Error.ReadToEndAsync();
    try
    {
        await restore.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));string reply=await stdout.WaitAsync(TimeSpan.FromSeconds(2));await stderr.WaitAsync(TimeSpan.FromSeconds(2));
        await restoreJob.TerminateAsync(CancellationToken.None);
        if(restore.ExitCode==0)
        {
            using var result=JsonDocument.Parse(reply);if(!result.RootElement.GetProperty("success").GetBoolean()||result.RootElement.GetProperty("outcome").GetString()!="RolledBack")throw new IOException("Scratch rollback unconfirmed.");
            Save(Path.Combine(run,"result.json"),new{run=id,role=roleName,jobActiveAfter=owned.ReadUsage().ActiveProcesses,outcome="RolledBack"});return;
        }
    }
    finally{if(!restore.HasExited)await restoreJob.TerminateAsync(CancellationToken.None);}
    await Task.Delay(250);
}
throw new IOException("Scratch rollback remained unconfirmed.");

async Task SelfTest()
{
    await SelfScenario(false);await SelfScenario(true);
}
async Task SelfScenario(bool duringRestore)
{
    Directory.CreateDirectory(allowed);EnsureUnlinked(allowed);string runId=Guid.NewGuid().ToString("N"),root=Path.Combine(allowed,runId),testClient=Path.Combine(root,"client");
    Directory.CreateDirectory(Path.Combine(testClient,"runtime"));File.WriteAllText(Path.Combine(testClient,"example.txt"),"old");
    foreach(string file in new[]{"RestoreWorker.ps1","Invoke-ScratchRestore.ps1"})File.Copy(Path.Combine(tools,file),Path.Combine(root,file));
    // Instrument only this disposable scratch copy, immediately after its
    // exclusive restore lock is acquired. Production worker stays unchanged.
    string workerPath=Path.Combine(root,"RestoreWorker.ps1");
    string workerText=File.ReadAllText(workerPath);
    const string lockLine="$owned.Add([IO.File]::Open((Join-Path $RunRoot 'restore.lock'),'OpenOrCreate','ReadWrite','None'))";
    if(!workerText.Contains(lockLine,StringComparison.Ordinal))throw new IOException("Scratch restore barrier location missing.");
    workerText=workerText.Replace(lockLine,lockLine+"\r\n        [IO.File]::WriteAllText((Join-Path $RunRoot ('helper-'+$env:CITA_SCRATCH_ROLE+'.locked')),'locked'); Start-Sleep -Milliseconds 1000",StringComparison.Ordinal);
    File.WriteAllText(workerPath,workerText);
    File.Copy(Path.Combine(workspace,"installer","InstallationTransaction.ps1"),Path.Combine(root,"InstallationTransaction.ps1"));
    File.WriteAllText(Path.Combine(root,"payload-manifest.json"),"{}");
    var testPlan=new Plan(runId,Hash(exe),Hash(Path.Combine(root,"RestoreWorker.ps1")),Hash(Path.Combine(root,"InstallationTransaction.ps1")),Hash(Path.Combine(root,"Invoke-ScratchRestore.ps1")),Hash(Path.Combine(root,"payload-manifest.json")),DateTimeOffset.UtcNow.AddSeconds(35));
    Save(Path.Combine(root,"plan.json"),testPlan);
    Process Launch(string mode){var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=workspace};start.ArgumentList.Add(mode);start.ArgumentList.Add(runId);return Process.Start(start)??throw new IOException("Scratch process start failed.");}
    Process? c=null,p=null,b=null,primaryHelper=null;
    try
    {
        c=Launch("--controller");await Until(()=>File.Exists(Path.Combine(root,"controller.json")),TimeSpan.FromSeconds(8));
        p=Launch("--primary");b=Launch("--backup");await Until(()=>File.Exists(Path.Combine(root,"primary.json"))&&File.Exists(Path.Combine(root,"backup.json"))&&File.Exists(Path.Combine(root,"dirty.json")),TimeSpan.FromSeconds(12));
        if(!duringRestore){p.Kill();await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}
        c.Kill();await c.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if(duringRestore){await Until(()=>File.Exists(Path.Combine(root,"helper-primary.locked")),TimeSpan.FromSeconds(12));primaryHelper=Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(root,"helper-primary.started"))));_=primaryHelper.Handle;p.Kill();await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}
        await b.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
        if(duringRestore)await primaryHelper!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var result=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"result.json")));
        if(b.ExitCode!=0||result.RootElement.GetProperty("outcome").GetString()!="RolledBack"||result.RootElement.GetProperty("jobActiveAfter").GetUInt32()!=0||File.ReadAllText(Path.Combine(testClient,"example.txt"))!="old")throw new IOException("Scratch crash recovery failed.");
        Console.WriteLine(duringRestore?"PASS scratch: primary died during helper restore; backup verified rollback.":"PASS scratch: both watchers ready, primary and controller killed, job drained, old public file restored.");
    }
    finally
    {
        foreach(var process in new[]{p,b,c,primaryHelper})if(process!=null){try{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}}finally{process.Dispose();}}
        // Only this exact generated scratch directory may be removed.
        if(Path.GetDirectoryName(root)!=allowed||Path.GetFileName(root)!=runId)throw new IOException("Scratch cleanup refused.");
        EnsureUnlinked(root);Directory.Delete(root,true);
    }
}
static FileStream OpenChecked(string path){if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Scratch file link refused.");return new(path,FileMode.Open,FileAccess.Read,FileShare.Read);}
static string Hash(string path){using var f=OpenChecked(path);return HashStream(f);}
static string HashStream(Stream stream){stream.Position=0;string result=Convert.ToHexString(SHA256.HashData(stream));stream.Position=0;return result;}
static void Save(string path,object value){string temp=path+"."+Guid.NewGuid().ToString("N")+".new";try{using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){JsonSerializer.Serialize(f,value);f.Flush(true);}File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}}
static async Task Until(Func<bool> condition,TimeSpan limit){var timer=Stopwatch.StartNew();while(!condition()){if(timer.Elapsed>limit)throw new TimeoutException("Scratch readiness unconfirmed.");await Task.Delay(50);}}
static void EnsureUnlinked(string root)
{
    for(string? p=root;p!=null;p=Path.GetDirectoryName(p))if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Scratch ancestor link refused.");
    var pending=new Stack<string>();pending.Push(root);
    while(pending.Count>0)foreach(string item in Directory.EnumerateFileSystemEntries(pending.Pop())){var attr=File.GetAttributes(item);if((attr&FileAttributes.ReparsePoint)!=0)throw new IOException("Scratch cleanup link refused.");if((attr&FileAttributes.Directory)!=0)pending.Push(item);}
}
internal sealed record Plan(string Run,string ExeSha,string WorkerSha,string TransactionSha,string InvokeSha,string ManifestSha,DateTimeOffset DeadlineUtc);
