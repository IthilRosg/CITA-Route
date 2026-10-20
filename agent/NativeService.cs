using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace RoutePilot.Agent;

public static class NativeService
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate void ServiceMain(uint count,IntPtr args);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate uint Handler(uint control,uint eventType,IntPtr eventData,IntPtr context);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct TableEntry { [MarshalAs(UnmanagedType.LPWStr)]public string? Name;public ServiceMain? Main; }
    [StructLayout(LayoutKind.Sequential)]struct Status { public uint Type,State,Accepted,Win32Exit,SpecificExit,Checkpoint,WaitHint; }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool StartServiceCtrlDispatcher(TableEntry[] entries);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr RegisterServiceCtrlHandlerEx(string name,Handler handler,IntPtr context);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool SetServiceStatus(IntPtr handle,ref Status status);
    static readonly CancellationTokenSource Stop=new();
    static readonly ServiceMain MainDelegate=ServiceEntry;
    static readonly Handler HandlerDelegate=Control;
    static IntPtr _handle;
    static AgentHost? _host;
    static Timer? _stoppingTimer;
    static uint _checkpoint;
    public static void Run()
    {
        if(!StartServiceCtrlDispatcher(new[]{new TableEntry{Name=Program.ServiceName,Main=MainDelegate},new TableEntry()}))throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    static void Report(uint state,uint accepted=0,uint hint=0,bool failed=false)
    {var status=new Status{Type=0x10,State=state,Accepted=accepted,WaitHint=hint,Checkpoint=state==3?++_checkpoint:0,Win32Exit=failed?1066u:0,SpecificExit=failed?1u:0};SetServiceStatus(_handle,ref status);}
    static uint Control(uint control,uint eventType,IntPtr _,IntPtr __)
    {
        if(control is 1 or 5){if(control==5)_host?.PrepareForShutdown();Report(3,0,90000);_stoppingTimer??=new Timer(_=>Report(3,0,90000),null,2000,2000);Stop.Cancel();}
        if(control==13 && eventType is 4 or 7 or 18)_host?.RequestRecovery("System suspend/resume");
        return 0;
    }
    static void ServiceEntry(uint _,IntPtr __)
    {
        _handle=RegisterServiceCtrlHandlerEx(Program.ServiceName,HandlerDelegate,IntPtr.Zero);
        Report(2,0,10000);
        try
        {
            var config=Storage.Read<AgentConfig>(Path.Combine(AppContext.BaseDirectory,"agent-config.json"));
            _host=new AgentHost(config);
            Report(4,1|4|0x40);
            _host.Run(Stop.Token).GetAwaiter().GetResult();Report(1);
        }
        catch(Exception ex)
        {
            try{File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RoutePilot","service-error.log"),DateTime.UtcNow.ToString("O")+" "+ex+Environment.NewLine);}catch{}
            Report(1,failed:true);Environment.ExitCode=1;
        }
        finally{_stoppingTimer?.Dispose();}
    }
}

public static class ServiceRecovery
{
    [StructLayout(LayoutKind.Sequential)]struct ActionEntry {public int Kind;public uint Delay;}
    [StructLayout(LayoutKind.Sequential)]struct Actions {public uint Reset;public IntPtr Reboot,Command;public uint Count;public IntPtr Entries;}
    [StructLayout(LayoutKind.Sequential)]struct Flag {public int Enabled;}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenSCManager(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenService(IntPtr manager,string name,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool ChangeServiceConfig2(IntPtr service,uint level,ref Actions actions);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool ChangeServiceConfig2(IntPtr service,uint level,ref Flag flag);
    [DllImport("advapi32.dll")]static extern bool CloseServiceHandle(IntPtr handle);
    public static void Configure()
    {
        IntPtr manager=OpenSCManager(null,null,1);if(manager==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr service=IntPtr.Zero,entries=IntPtr.Zero;
        try
        {
            service=OpenService(manager,Program.ServiceName,2|0x10);if(service==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            int size=Marshal.SizeOf<ActionEntry>();entries=Marshal.AllocHGlobal(size*3);
            Marshal.StructureToPtr(new ActionEntry{Kind=1,Delay=5000},entries,false);
            Marshal.StructureToPtr(new ActionEntry{Kind=1,Delay=15000},entries+size,false);
            // SCM repeats the last action for every subsequent failure. A NONE
            // tail silently disabled the rescue watchdog after its third crash.
            Marshal.StructureToPtr(new ActionEntry{Kind=1,Delay=15000},entries+size*2,false);
            var actions=new Actions{Reset=86400,Count=3,Entries=entries};var flag=new Flag{Enabled=1};
            if(!ChangeServiceConfig2(service,2,ref actions)||!ChangeServiceConfig2(service,4,ref flag))throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally{if(entries!=IntPtr.Zero)Marshal.FreeHGlobal(entries);if(service!=IntPtr.Zero)CloseServiceHandle(service);CloseServiceHandle(manager);}
    }
}

public static class UserLaunch
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct Startup
    {public int Size;public string? Reserved,Desktop,Title;public uint X,Y,XSize,YSize,XCount,YCount,Fill,Flags;public short Show,ReservedBytes;public IntPtr ReservedPtr,Input,Output,Error;}
    [StructLayout(LayoutKind.Sequential)]struct ProcessInfo {public IntPtr Process,Thread;public int Pid,Tid;}
    [DllImport("wtsapi32.dll",SetLastError=true)]static extern bool WTSQueryUserToken(uint sessionId,out IntPtr token);
    [DllImport("userenv.dll",SetLastError=true)]static extern bool CreateEnvironmentBlock(out IntPtr environment,IntPtr token,bool inherit);
    [DllImport("userenv.dll")]static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateProcessAsUser(IntPtr token,string app,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string directory,ref Startup startup,out ProcessInfo info);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateProcess(string app,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string directory,ref Startup startup,out ProcessInfo info);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",SetLastError=true)]static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")]static extern bool TerminateProcess(IntPtr process,uint code);
    public static ProcessIdentity StartSystemRouter(string config,Action<ProcessIdentity> beforeResume)
    {
        string image=Tools.Router;
        var startup=new Startup{Size=Marshal.SizeOf<Startup>(),Flags=1,Show=0};
        var command=new StringBuilder(Quote(image)+" run -c "+Quote(config)+" --disable-color");
        if(!CreateProcess(image,command,IntPtr.Zero,IntPtr.Zero,false,0x08000000|4,IntPtr.Zero,AppContext.BaseDirectory,ref startup,out var info))throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            using var process=System.Diagnostics.Process.GetProcessById(info.Pid);
            var identity=new ProcessIdentity(info.Pid,process.StartTime.ToUniversalTime(),image);
            beforeResume(identity);
            if(ResumeThread(info.Thread)==uint.MaxValue)throw new Win32Exception(Marshal.GetLastWin32Error());
            return identity;
        }
        catch{TerminateProcess(info.Process,1);throw;}
        finally{CloseHandle(info.Thread);CloseHandle(info.Process);}
    }
    public static ProcessIdentity Start(string image,IEnumerable<string> args,int sessionId,string expectedSid,string directory,bool visible=false,Action<ProcessIdentity>? beforeResume=null)
    {
        if(!WTSQueryUserToken((uint)sessionId,out var token))throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr environment=IntPtr.Zero;
        try
        {
            using var identity=new WindowsIdentity(token);
            if(identity.User?.Value!=expectedSid)throw new UnauthorizedAccessException("Interactive session belongs to another user.");
            if(!CreateEnvironmentBlock(out environment,token,false))throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup=new Startup{Size=Marshal.SizeOf<Startup>(),Desktop="winsta0\\default",Flags=1,Show=(short)(visible?1:0)};
            string command=Quote(image)+" "+string.Join(" ",args.Select(Quote));
            if(!CreateProcessAsUser(token,image,new StringBuilder(command),IntPtr.Zero,IntPtr.Zero,false,0x400|0x08000000|4,environment,directory,ref startup,out var info))throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                using var process=System.Diagnostics.Process.GetProcessById(info.Pid);
                var recorded=new ProcessIdentity(info.Pid,process.StartTime.ToUniversalTime(),image);
                beforeResume?.Invoke(recorded);
                if(ResumeThread(info.Thread)==uint.MaxValue)throw new Win32Exception(Marshal.GetLastWin32Error());
                return recorded;
            }
            catch{TerminateProcess(info.Process,1);throw;}
            finally{CloseHandle(info.Thread);CloseHandle(info.Process);}
        }
        finally{if(environment!=IntPtr.Zero)DestroyEnvironmentBlock(environment);CloseHandle(token);}
    }
    public static string Quote(string value)
    {
        var result=new StringBuilder("\"");int slashes=0;
        foreach(char c in value)
        {
            if(c=='\\'){slashes++;continue;}
            if(c=='\"'){result.Append('\\',slashes*2+1).Append(c);slashes=0;continue;}
            result.Append('\\',slashes).Append(c);slashes=0;
        }
        return result.Append('\\',slashes*2).Append('"').ToString();
    }
}

public static class PipeIdentity
{
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool ImpersonateNamedPipeClient(IntPtr pipe);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool RevertToSelf();
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool OpenThreadToken(IntPtr thread,uint access,bool asSelf,out IntPtr token);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool GetTokenInformation(IntPtr token,int type,IntPtr data,int length,out int required);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool ConvertSidToStringSid(IntPtr sid,out IntPtr text);
    [DllImport("kernel32.dll")]static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]static extern IntPtr LocalFree(IntPtr handle);
    public static string Read(IntPtr pipe)
    {
        // Do not load managed identity/claims assemblies while holding an identification-only token.
        IntPtr token=IntPtr.Zero,buffer=IntPtr.Zero,text=IntPtr.Zero;
        if(!ImpersonateNamedPipeClient(pipe))throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if(!OpenThreadToken(GetCurrentThread(),8,true,out token))throw new Win32Exception(Marshal.GetLastWin32Error());
            GetTokenInformation(token,1,IntPtr.Zero,0,out int size);buffer=Marshal.AllocHGlobal(size);
            if(!GetTokenInformation(token,1,buffer,size,out _) || !ConvertSidToStringSid(Marshal.ReadIntPtr(buffer),out text))throw new Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.PtrToStringUni(text)??throw new UnauthorizedAccessException("Missing client SID.");
        }
        finally
        {
            RevertToSelf();if(token!=IntPtr.Zero)CloseHandle(token);if(buffer!=IntPtr.Zero)Marshal.FreeHGlobal(buffer);if(text!=IntPtr.Zero)LocalFree(text);
        }
    }
}

public static class VpnusServiceControl
{
    [StructLayout(LayoutKind.Sequential)]struct Status {public uint Type,State,Accepted,Win32Exit,SpecificExit,Checkpoint,WaitHint;}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenSCManager(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenService(IntPtr manager,string name,uint access);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool ControlService(IntPtr service,uint control,out Status status);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool QueryServiceStatus(IntPtr service,out Status status);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool StartService(IntPtr service,uint count,IntPtr args);
    [DllImport("advapi32.dll")]static extern bool CloseServiceHandle(IntPtr handle);
    static void WithService(Action<IntPtr> action)
    {
        IntPtr manager=OpenSCManager(null,null,1);if(manager==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr service=IntPtr.Zero;
        try{service=OpenService(manager,"VpnusService",0x34);if(service==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());action(service);}
        finally{if(service!=IntPtr.Zero)CloseServiceHandle(service);CloseServiceHandle(manager);}
    }
    static void Wait(IntPtr service,uint expected,int seconds)
    {
        var timer=System.Diagnostics.Stopwatch.StartNew();
        while(timer.Elapsed.TotalSeconds<seconds)
        {if(!QueryServiceStatus(service,out var state))throw new Win32Exception(Marshal.GetLastWin32Error());if(state.State==expected)return;Thread.Sleep(100);}
        throw new TimeoutException("VPNUS service did not reach the requested state.");
    }
    static void Start(IntPtr service,int seconds=8)
    {
        var budget=System.Diagnostics.Stopwatch.StartNew();
        if(!QueryServiceStatus(service,out var state))throw new Win32Exception(Marshal.GetLastWin32Error());
        if(state.State==4)return;
        if(state.State==3)Wait(service,1,Math.Min(6,seconds));
        if(!StartService(service,0,IntPtr.Zero) && Marshal.GetLastWin32Error()!=1056)throw new Win32Exception(Marshal.GetLastWin32Error());
        Wait(service,4,Math.Max(1,seconds-(int)budget.Elapsed.TotalSeconds));
    }
    public static void EnsureRunning(int seconds=8)=>WithService(service=>Start(service,seconds));
    public static bool StopWithDownProof()
    {
        bool down=false;
        WithService(service=>
        {
            if(!QueryServiceStatus(service,out var state))throw new Win32Exception(Marshal.GetLastWin32Error());
            if(state.State!=1)
            {if(!ControlService(service,1,out _) && Marshal.GetLastWin32Error()!=1062)throw new Win32Exception(Marshal.GetLastWin32Error());Wait(service,1,8);}
            var timer=System.Diagnostics.Stopwatch.StartNew();
            while(timer.Elapsed.TotalSeconds<3 && NetworkProbe.Vpnus!=null)Thread.Sleep(100);
            down=NetworkProbe.Vpnus==null;
        });
        return down;
    }
    public static bool RestartWithDownProof()
    {
        bool down=false;
        WithService(service=>
        {
            if(!QueryServiceStatus(service,out var state))throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if(state.State!=1)
                {if(!ControlService(service,1,out _) && Marshal.GetLastWin32Error()!=1062)throw new Win32Exception(Marshal.GetLastWin32Error());Wait(service,1,8);}
                var timer=System.Diagnostics.Stopwatch.StartNew();
                while(timer.Elapsed.TotalSeconds<3 && NetworkProbe.Vpnus!=null)Thread.Sleep(100);
                down=NetworkProbe.Vpnus==null;
            }
            finally{Start(service);}
        });
        return down;
    }
}
