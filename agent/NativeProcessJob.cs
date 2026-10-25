using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RoutePilot.Agent;

// A native session's children inherit this job. Unexpected controller exit
// closes its last handle, so an orphan core cannot keep the previous TUN alive.
internal sealed class NativeProcessJob:IDisposable
{
    sealed class Handle:SafeHandleZeroOrMinusOneIsInvalid
    {public Handle():base(true){}protected override bool ReleaseHandle()=>CloseHandle(handle);}
    [StructLayout(LayoutKind.Sequential)]struct BasicLimits{public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinWorkingSet,MaxWorkingSet;public uint ActiveProcesses;public UIntPtr Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)]struct Io{public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]struct Limits{public BasicLimits Basic;public Io Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
    [StructLayout(LayoutKind.Sequential)]struct Accounting{public long User,Kernel,PeriodUser,PeriodKernel;public uint Faults,Total,Active,Terminated;}
    [DllImport("kernel32.dll",SetLastError=true)]static extern Handle CreateJobObjectW(IntPtr security,string? name);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetInformationJobObject(Handle job,int kind,ref Limits data,uint length);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool QueryInformationJobObject(Handle job,int kind,out Accounting data,uint length,IntPtr returned);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool AssignProcessToJobObject(Handle job,IntPtr process);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateJobObject(Handle job,uint code);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
    readonly Handle job;
    internal NativeProcessJob()
    {
        job=CreateJobObjectW(IntPtr.Zero,null);if(job.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits=new Limits{Basic=new BasicLimits{Flags=0x2000}};
        if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf<Limits>())){int code=Marshal.GetLastWin32Error();job.Dispose();throw new Win32Exception(code);}
    }
    internal void Attach(Process process)
    {
        if(AssignProcessToJobObject(job,process.Handle))return;
        int code=Marshal.GetLastWin32Error();try{process.Kill(true);}catch{}throw new Win32Exception(code);
    }
    internal async Task TerminateAsync(CancellationToken stop)
    {
        if(!TerminateJobObject(job,1))throw new Win32Exception(Marshal.GetLastWin32Error());
        for(int i=0;i<100;i++)
        {if(!QueryInformationJobObject(job,1,out var state,(uint)Marshal.SizeOf<Accounting>(),IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());if(state.Active==0)return;await Task.Delay(100,stop);}
        throw new IOException("Native process retirement unconfirmed.");
    }
    public void Dispose()=>job.Dispose();
}
