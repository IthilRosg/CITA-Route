using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RoutePilot.Agent;

internal static class BrowserNativeIdentity
{
    [DllImport("kernel32.dll",SetLastError=true)]internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)]internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool Process32FirstW(IntPtr snapshot,ref Entry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool Process32NextW(IntPtr snapshot,ref Entry entry);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr value);
    [DllImport("kernel32.dll",SetLastError=true)]static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetProcessTimes(SafeProcessHandle process,out long created,out long exit,out long kernel,out long user);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryFullProcessImageName(SafeProcessHandle process,uint flags,StringBuilder name,ref int size);
    internal static ProcessIdentity? Read(int pid)=>Read(pid,out _);
    static ProcessIdentity? Read(int pid,out string status)
    {
        using var handle=OpenProcess(0x1000,false,pid);status="Open:"+Marshal.GetLastWin32Error();if(handle.IsInvalid)return null;
        if(!GetProcessTimes(handle,out long created,out _,out _,out _)){status="Times:"+Marshal.GetLastWin32Error();return null;}
        var name=new StringBuilder(32768);int size=name.Capacity;if(!QueryFullProcessImageName(handle,0,name,ref size)){status="Image:"+Marshal.GetLastWin32Error();return null;}status="Read";return new(pid,DateTime.FromFileTimeUtc(created),name.ToString());
    }
    internal static object ServerProbe(int pid,ProcessIdentity expected)
    {var actual=Read(pid,out var status);return new{status,pidMatches=pid==expected.Pid,imageMatches=actual!=null&&string.Equals(actual.Path,AgentImage,StringComparison.OrdinalIgnoreCase),expectedImageMatches=actual!=null&&string.Equals(actual.Path,expected.Path,StringComparison.OrdinalIgnoreCase),startDeltaMs=actual==null?(double?)null:(actual.StartedUtc-expected.StartedUtc).TotalMilliseconds};}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct Entry
    {public uint Size,Usage,Pid;public UIntPtr Heap;public uint Module,Threads,Parent;public int Priority;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Exe;}
    static int Parent(int pid)
    {
        var snapshot=CreateToolhelp32Snapshot(2,0);if(snapshot==new IntPtr(-1))return 0;
        try{var item=new Entry{Size=(uint)Marshal.SizeOf<Entry>(),Exe=""};if(Process32FirstW(snapshot,ref item))do{if(item.Pid==pid)return (int)item.Parent;}while(Process32NextW(snapshot,ref item));return 0;}finally{CloseHandle(snapshot);}
    }
    internal static string AgentImage=>Path.Combine(AppContext.BaseDirectory,"RoutePilot.Agent.exe");
    internal static string? Browser(int nativePid)
    {
        try
        {
            var host=Read(nativePid);
            if(host==null||!string.Equals(host.Path,AgentImage,StringComparison.OrdinalIgnoreCase))return null;
            DateTime childStart=host.StartedUtc;int parent=Parent(nativePid);
            for(int depth=0;depth<3&&parent>0;depth++)
            {
                var process=Read(parent);if(process==null)return null;string path=process.Path;
                if(process.StartedUtc>childStart)return null;
                string file=Path.GetFileName(path).ToLowerInvariant();
                if(file is "chrome.exe" or "msedge.exe")return path;
                if(!string.Equals(path,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe"),StringComparison.OrdinalIgnoreCase))return null;
                childStart=process.StartedUtc;parent=Parent(parent);
            }
        }
        catch(Exception ex)when(ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception){}
        return null;
    }
    internal static bool Server(int pid,ProcessIdentity expected)
    {
        try{var process=Read(pid);return process!=null&&pid==expected.Pid&&string.Equals(process.Path,AgentImage,StringComparison.OrdinalIgnoreCase)&&Math.Abs((process.StartedUtc-expected.StartedUtc).TotalMilliseconds)<1;}
        catch(Exception ex)when(ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception){return false;}
    }
}
