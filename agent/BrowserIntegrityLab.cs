using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace RoutePilot.Agent;

// Acceptance-only launcher: same-user interactive task, explicitly limited rights.
// Production browser/native-host launches never use this helper.
internal static class BrowserIntegrityLab
{
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool OpenProcessToken(SafeProcessHandle process,uint access,out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool GetTokenInformation(SafeAccessTokenHandle token,int kind,IntPtr buffer,int length,out int needed);
    static IntPtr Info(SafeAccessTokenHandle token,int kind)
    {
        GetTokenInformation(token,kind,IntPtr.Zero,0,out int size);
        if(size<=0||size>65536)throw new Win32Exception(Marshal.GetLastWin32Error());
        var memory=Marshal.AllocHGlobal(size);
        if(GetTokenInformation(token,kind,memory,size,out _))return memory;
        int error=Marshal.GetLastWin32Error();Marshal.FreeHGlobal(memory);throw new Win32Exception(error);
    }
    static int Level(SafeAccessTokenHandle token)
    {
        var memory=Info(token,25);
        try{string sid=new SecurityIdentifier(Marshal.ReadIntPtr(memory)).Value;return int.Parse(sid[(sid.LastIndexOf('-')+1)..],System.Globalization.CultureInfo.InvariantCulture);}
        finally{Marshal.FreeHGlobal(memory);}
    }
    internal static int Level(Process process)
    {
        if(!OpenProcessToken(process.SafeHandle,8,out var token))throw new Win32Exception(Marshal.GetLastWin32Error());
        using(token)return Level(token);
    }
    static string Quote(string value)
    {
        var text=new StringBuilder("\"");int slashes=0;
        foreach(char c in value){if(c=='\\'){slashes++;continue;}text.Append('\\',c=='"'?slashes*2+1:slashes).Append(c);slashes=0;}
        return text.Append('\\',slashes*2).Append('"').ToString();
    }
    internal static async Task Probe(string root,string profile)
    {
        using var bios=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        if((bios?.GetValue("SystemManufacturer") as string)?.Contains("VMware")!=true||!root.StartsWith(@"C:\CitaLab\BrowserTun\",StringComparison.OrdinalIgnoreCase)||!Guid.TryParseExact(Path.GetFileName(root),"N",out _)||!profile.StartsWith(root+@"\profiles\",StringComparison.OrdinalIgnoreCase))throw new IOException("Owned VMware lab required.");
        string stage="discovery";bool connected=false,server=false;string? error=null;object? identity=null;bool sameOwner=false;
        try
        {
            var state=ComponentLiveness.Read<BrowserObserverState>(Path.Combine(root,"runtime/browser-observer.json"));stage="pipe";
            using var pipe=new System.IO.Pipes.NamedPipeClientStream(".",state.Pipe,System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);
            using var stop=new CancellationTokenSource(4000);await pipe.ConnectAsync(stop.Token);connected=true;stage="identity";
            using var caller=WindowsIdentity.GetCurrent();
            sameOwner=System.IO.Pipes.PipesAclExtensions.GetAccessControl(pipe).GetOwner(typeof(SecurityIdentifier))?.Equals(caller.User)==true;
            if(BrowserNativeIdentity.GetNamedPipeServerProcessId(pipe.SafePipeHandle,out uint pid)){identity=BrowserNativeIdentity.ServerProbe((int)pid,state.Owner);server=sameOwner&&BrowserNativeIdentity.Server((int)pid,state.Owner);}stage="finished";
        }
        catch(Exception ex){error=ex.GetType().Name+":"+ex.HResult;}
        Storage.Write(Path.Combine(profile,"peer-probe.json"),new{stage,connected,server,error,sameOwner,identity});
    }
    internal static Process Start(ProcessStartInfo info)
    {
        using var owner=Process.GetCurrentProcess();
        if(Level(owner)!=12288)throw new IOException("Medium-browser acceptance requires a high-integrity user agent.");
        string profile=info.ArgumentList.Single(a=>a.StartsWith("--user-data-dir=",StringComparison.Ordinal))[16..];
        if(!profile.StartsWith(@"C:\CitaLab\BrowserTun\",StringComparison.OrdinalIgnoreCase))throw new IOException("Only an owned browser profile may be launched.");
        using var identity=WindowsIdentity.GetCurrent();var user=identity.User??throw new IOException("Missing user SID.");
        var directory=new DirectoryInfo(profile);var acl=directory.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(user,FileSystemRights.Modify,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));directory.SetAccessControl(acl);
        string script=Path.Combine(profile,"launch-medium.ps1"),pidFile=Path.Combine(profile,"chrome.pid");
        static string Literal(string text)=>"'"+text.Replace("'","''")+"'";
        string root=Directory.GetParent(Directory.GetParent(profile)!.FullName)!.FullName;
        string probe="Start-Process -WindowStyle Hidden -FilePath "+Literal(BrowserNativeIdentity.AgentImage)+" -ArgumentList "+Literal("--browser-native-peer-lab "+Quote(root)+" "+Quote(profile))+" -Wait -RedirectStandardOutput "+Literal(Path.Combine(profile,"probe-out.log"))+" -RedirectStandardError "+Literal(Path.Combine(profile,"probe-error.log"))+";";
        File.WriteAllText(script,@"$ErrorActionPreference='Stop';$env:DOTNET_ROOT='C:\CitaLab\Dotnet';$env:DOTNET_ROOT_X64='C:\CitaLab\Dotnet';"+probe+"$p=Start-Process -WindowStyle Hidden -FilePath "+Literal(info.FileName)+" -ArgumentList "+Literal(string.Join(" ",info.ArgumentList.Select(Quote)))+" -PassThru -RedirectStandardOutput "+Literal(Path.Combine(profile,"stdout.log"))+" -RedirectStandardError "+Literal(Path.Combine(profile,"stderr.log"))+";[IO.File]::WriteAllText("+Literal(pidFile+".tmp")+",$p.Id.ToString(),[Text.Encoding]::ASCII);[IO.File]::Move("+Literal(pidFile+".tmp")+","+Literal(pidFile)+");$p.WaitForExit()",new UTF8Encoding(true));
        dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;service.Connect();dynamic folder=service.GetFolder("\\");
        string name="Cita-BrowserLab-"+Guid.NewGuid().ToString("N");dynamic definition=service.NewTask(0);
        definition.Principal.UserId=user.Value;definition.Principal.LogonType=3;definition.Principal.RunLevel=0;
        definition.Settings.Hidden=true;definition.Settings.ExecutionTimeLimit="PT4M";definition.Settings.DisallowStartIfOnBatteries=false;definition.Settings.StopIfGoingOnBatteries=false;
        dynamic action=definition.Actions.Create(0);action.Path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");action.Arguments="-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "+Quote(script);
        dynamic task=folder.RegisterTaskDefinition(name,definition,2,user.Value,null,3,null);bool started=false;
        try
        {
            DateTime launchUtc=DateTime.UtcNow;
            task.Run(null);
            for(int i=0;i<150;i++)
            {
                if(File.Exists(pidFile)&&int.TryParse(File.ReadAllText(pidFile).Trim(),out int pid))
                {
                    var process=Process.GetProcessById(pid);
                    var actual=BrowserNativeIdentity.Read(pid);
                    if(actual==null){process.Dispose();Thread.Sleep(100);continue;}
                    if(process.HasExited||actual.StartedUtc<launchUtc.AddSeconds(-1)||!string.Equals(actual.Path,info.FileName,StringComparison.OrdinalIgnoreCase)){process.Dispose();throw new IOException("Scheduled browser identity differs.");}
                    if(Level(process)!=8192){process.Kill(true);process.Dispose();throw new IOException("Scheduled Chrome is not medium integrity.");}started=true;return process;
                }
                Thread.Sleep(100);
            }
            throw new IOException("Interactive medium-browser startup deadline.");
        }
        finally{if(!started)task.Stop(0);folder.DeleteTask(name,0);}
    }
}
