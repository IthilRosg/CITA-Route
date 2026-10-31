using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Cita.NativeUpdater;

internal static class ServiceFence
{
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool result);
    internal static bool Contains(System.Diagnostics.Process process,long handle)
    {if(!IsProcessInJob(process.Handle,new IntPtr(handle),out bool result))throw new Win32Exception(Marshal.GetLastWin32Error());return result;}
    [StructLayout(LayoutKind.Sequential)]struct Status{public uint Type,State,Controls,ExitCode,Specific,Checkpoint,WaitHint,Pid,Flags;}
    [StructLayout(LayoutKind.Sequential)]struct Config{public uint Type,Start,Error;public IntPtr Binary,Group;public uint Tag;public IntPtr Dependencies,Account,Display;}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenSCManagerW(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenServiceW(IntPtr manager,string name,uint access);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool QueryServiceStatusEx(IntPtr service,int level,out Status status,uint size,out uint needed);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryServiceConfigW(IntPtr service,IntPtr config,uint size,out uint needed);
    [DllImport("advapi32.dll")]static extern bool CloseServiceHandle(IntPtr handle);
    internal static bool DisabledStopped(string agentRoot)
    {
        IntPtr manager=OpenSCManagerW(null,null,1);if(manager==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            IntPtr service=OpenServiceW(manager,"RoutePilotRescue",1|4);if(service==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if(QueryServiceConfigW(service,IntPtr.Zero,0,out uint size)||Marshal.GetLastWin32Error()!=122||size<Marshal.SizeOf<Config>()||size>65536)throw new IOException("Upgrade SCM config size refused.");
                IntPtr buffer=Marshal.AllocHGlobal(checked((int)size));
                try
                {
                    if(!QueryServiceConfigW(service,buffer,size,out _))throw new Win32Exception(Marshal.GetLastWin32Error());
                    var config=Marshal.PtrToStructure<Config>(buffer);
                    if(config.Type!=16||Marshal.PtrToStringUni(config.Account)!="LocalSystem"||!string.Equals(Marshal.PtrToStringUni(config.Binary),'"'+Path.Combine(agentRoot,"RoutePilot.Agent.exe")+"\" --service",StringComparison.OrdinalIgnoreCase))throw new IOException("Upgrade SCM identity differs.");
                    if(!QueryServiceStatusEx(service,0,out var status,(uint)Marshal.SizeOf<Status>(),out _))throw new Win32Exception(Marshal.GetLastWin32Error());
                    return config.Start==4&&status.State==1&&status.Pid==0;
                }finally{Marshal.FreeHGlobal(buffer);}
            }finally{CloseServiceHandle(service);}
        }finally{CloseServiceHandle(manager);}
    }
}
