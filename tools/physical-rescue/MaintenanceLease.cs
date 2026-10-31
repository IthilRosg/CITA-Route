using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

// This lease is only for the fixed physical fixture. It prevents replacement
// of the exact native intent while its service confirms a quiescent state.
internal sealed class PhysicalNativeNotReady:IOException
{
    internal PhysicalNativeNotReady():base("Native service is starting."){}
}
internal sealed class PhysicalNativeStatusTimeout:IOException
{
    internal PhysicalNativeStatusTimeout():base("Native service status timed out."){}
}
internal sealed class MaintenanceLease:IDisposable
{
    readonly FileStream input;
    readonly string operation;
    readonly ulong generation;
    Process? server;
    MaintenanceLease(FileStream stream,string operation,ulong generation){input=stream;this.operation=operation;this.generation=generation;}
    internal static MaintenanceLease Open(string path)
    {
        var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        try
        {
            if(file.Length is <1 or >4096)throw new IOException("Intent length refused.");
            using var data=JsonDocument.Parse(file);var root=data.RootElement;
            string run=root.GetProperty("Run").GetString()??"",operation=root.GetProperty("Operation").GetString()??"";
            if(root.GetProperty("Desired").ValueKind!=JsonValueKind.False||run.Length!=32||run.Any(c=>!char.IsAsciiHexDigit(c)||char.IsUpper(c))||operation.Length!=32||operation.Any(c=>!char.IsAsciiHexDigit(c)||char.IsUpper(c)))throw new IOException("Native intent is not quiescent.");
            return new(file,operation,root.GetProperty("Generation").GetUInt64());
        }
        catch{file.Dispose();throw;}
    }
    internal bool ServiceStillMatches()
    {
        try{return server is {HasExited:false}&&QueryServicePid()==server.Id;}
        catch{return false;}
    }
    internal async Task ConfirmServiceAsync(Action<string> check)
    {
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try{
        using var pipe=new NamedPipeClientStream(".","RoutePilot.Rescue.v1",PipeDirection.InOut,PipeOptions.Asynchronous,System.Security.Principal.TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(budget.Token);
        if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out uint pipePid)||pipePid==0||pipePid!=QueryServicePid())throw new IOException("Status pipe server is not the service.");
        var current=Process.GetProcessById(checked((int)pipePid));
        try
        {
            _=current.Handle;
            string expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RoutePilot","Agent","RoutePilot.Agent.exe");
            check(expected);
            using var image=new FileStream(expected,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(current.HasExited||!string.Equals(current.MainModule?.FileName,expected,StringComparison.OrdinalIgnoreCase))throw new IOException("Status service image differs.");
            byte[] command=Encoding.UTF8.GetBytes("{\"Command\":\"native-status\"}\n");await pipe.WriteAsync(command,budget.Token);await pipe.FlushAsync(budget.Token);
            var data=new List<byte>();byte[] one=new byte[1];
            while(true){int n=await pipe.ReadAsync(one,budget.Token);if(n==0)throw new IOException("Status reply missing.");if(one[0]==10)break;if(data.Count>=65536)throw new IOException("Status reply too large.");data.Add(one[0]);}
            using var reply=JsonDocument.Parse(data.ToArray());var root=reply.RootElement;
            if(current.HasExited||QueryServicePid()!=pipePid)throw new IOException("Native service identity changed.");
            if(!ClassifyNativeStatus(root,operation,generation))throw new PhysicalNativeNotReady();
            if(server!=null&&(server.Id!=current.Id||server.StartTime.ToUniversalTime().Ticks!=current.StartTime.ToUniversalTime().Ticks))throw new IOException("Native service restarted during maintenance.");
            if(server==null){server=current;current=null!;}
        }
        finally{current?.Dispose();}
        }catch(OperationCanceledException)when(budget.IsCancellationRequested){throw new PhysicalNativeStatusTimeout();}
    }
    internal static bool ClassifyNativeStatus(JsonElement root,string operation,ulong generation)
    {
        if(!root.TryGetProperty("Success",out var success)||success.ValueKind is not (JsonValueKind.True or JsonValueKind.False)||!root.TryGetProperty("Native",out var native))throw new IOException("Malformed native service status.");
        if(native.ValueKind==JsonValueKind.Null)return false;
        if(native.ValueKind!=JsonValueKind.Object||!native.TryGetProperty("phase",out var phase)||!native.TryGetProperty("desired",out var desired)||!native.TryGetProperty("operation",out var op)||!native.TryGetProperty("generation",out var gen)||desired.ValueKind!=JsonValueKind.False||op.GetString()!=operation||gen.GetUInt64()!=generation)throw new IOException("Native service identity or desired state differs.");
        if(phase.GetString()=="Stopping"&&success.ValueKind==JsonValueKind.True)return false;
        if(phase.GetString()=="Disconnected"&&success.ValueKind==JsonValueKind.True)return true;
        throw new IOException("Native service state requires attention.");
    }
    public void Dispose(){server?.Dispose();input.Dispose();}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    internal static int TestDeleteOpen(string path)
    {
        using var handle=CreateFileW(path,0x10000,7,IntPtr.Zero,3,0x80,IntPtr.Zero);
        return handle.IsInvalid?Marshal.GetLastWin32Error():0;
    }
    [StructLayout(LayoutKind.Sequential)]struct ServiceStatus{public uint Type,State,Controls,ExitCode,SpecificExitCode,Checkpoint,WaitHint,Pid,Flags;}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenSCManagerW(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenServiceW(IntPtr manager,string service,uint access);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool QueryServiceStatusEx(IntPtr service,int level,out ServiceStatus status,uint size,out uint needed);
    [DllImport("advapi32.dll")]static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetNamedPipeServerProcessId(IntPtr pipe,out uint pid);
    static uint QueryServicePid()
    {
        IntPtr manager=OpenSCManagerW(null,null,1);if(manager==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            IntPtr service=OpenServiceW(manager,"RoutePilotRescue",4);if(service==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            try{if(!QueryServiceStatusEx(service,0,out var status,(uint)Marshal.SizeOf<ServiceStatus>(),out _)||status.State!=4||status.Pid==0)throw new IOException("Native service unavailable.");return status.Pid;}
            finally{CloseServiceHandle(service);}
        }
        finally{CloseServiceHandle(manager);}
    }
}
