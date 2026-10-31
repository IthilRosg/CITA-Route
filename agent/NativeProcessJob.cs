using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RoutePilot.Agent;

internal sealed record NativeJobUsage(long CpuTicks,uint ProcessesStarted,uint ActiveProcesses,ulong PeakCommittedBytes);
internal sealed class NativeChildProcess:IDisposable
{
    internal Process Process{get;}
    internal StreamWriter Input{get;}
    internal StreamReader Output{get;}
    internal StreamReader Error{get;}
    internal NativeChildProcess(Process process,SafeFileHandle input,SafeFileHandle output,SafeFileHandle error)
    {
        Process=process;
        Input=new StreamWriter(new FileStream(input,FileAccess.Write,4096,false),new UTF8Encoding(false)){AutoFlush=true};
        Output=new StreamReader(new FileStream(output,FileAccess.Read,4096,false),Encoding.UTF8);
        Error=new StreamReader(new FileStream(error,FileAccess.Read,4096,false),Encoding.UTF8);
    }
    public void Dispose(){try{Input.Dispose();}finally{try{Output.Dispose();}finally{try{Error.Dispose();}finally{Process.Dispose();}}}}
}

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
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool QueryInformationJobObject(Handle job,int kind,out Limits data,uint length,IntPtr returned);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool AssignProcessToJobObject(Handle job,IntPtr process);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateJobObject(Handle job,uint code);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool DuplicateHandle(IntPtr sourceProcess,SafeHandle source,IntPtr targetProcess,out SafeFileHandle target,uint access,bool inherit,uint options);
    [DllImport("kernel32.dll",EntryPoint="DuplicateHandle",SetLastError=true)]static extern bool DuplicateRemoteHandle(IntPtr sourceProcess,IntPtr source,IntPtr targetProcess,out Handle target,uint access,bool inherit,uint options);
    [StructLayout(LayoutKind.Sequential)]struct Security{public int Size;public IntPtr Descriptor;public int Inherit;}
    [StructLayout(LayoutKind.Sequential)]struct Startup{public int Size;public IntPtr Reserved,Desktop,Title;public uint X,Y,XSize,YSize,XChars,YChars,Fill,Flags;public ushort Show,ReservedSize;public IntPtr ReservedBytes,Input,Output,Error;}
    [StructLayout(LayoutKind.Sequential)]struct StartupEx{public Startup Startup;public IntPtr Attributes;}
    [StructLayout(LayoutKind.Sequential)]struct ProcessInfo{public IntPtr Process,Thread;public uint Pid,Tid;}
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,uint flags,ref IntPtr size);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr attribute,IntPtr value,IntPtr size,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")]static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFileW(string name,uint access,uint sharing,ref Security security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateProcessW(string application,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string directory,ref StartupEx startup,out ProcessInfo info);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool CreatePipe(out SafeFileHandle read,out SafeFileHandle write,ref Security security,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetHandleInformation(SafeFileHandle handle,uint mask,uint flags);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateProcess(IntPtr process,uint exitCode);
    [DllImport("kernel32.dll",SetLastError=true)]static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    readonly Handle job;
    NativeProcessJob(Handle duplicated){job=duplicated;try{_=ReadUsage();}catch{job.Dispose();throw;}}
    // Only pass a handle value obtained from a protected descriptor and the
    // already-held, identity-verified controller process handle. The returned
    // object owns its independent duplicate, not the controller's handle.
    internal static NativeProcessJob DuplicateFromHeldController(Process controller,long value)
    {
        if(value<=0 || (IntPtr.Size==4 && value>int.MaxValue))throw new ArgumentOutOfRangeException(nameof(value));
        if(!DuplicateRemoteHandle(controller.Handle,new IntPtr(value),new IntPtr(-1),out var duplicated,0,false,2))throw new Win32Exception(Marshal.GetLastWin32Error());
        return new NativeProcessJob(duplicated);
    }
    internal long DescriptorHandleValue => job.DangerousGetHandle().ToInt64();
    internal NativeProcessJob(ulong? processMemoryLimit=null)
    {
        if(processMemoryLimit.HasValue&&(processMemoryLimit.Value<16777216||processMemoryLimit.Value>268435456))throw new ArgumentOutOfRangeException(nameof(processMemoryLimit));
        job=CreateJobObjectW(IntPtr.Zero,null);if(job.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits=new Limits{Basic=new BasicLimits{Flags=0x2000|(processMemoryLimit.HasValue?0x100u:0)},ProcessMemory=new UIntPtr(processMemoryLimit??0)};
        if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf<Limits>())){int code=Marshal.GetLastWin32Error();job.Dispose();throw new Win32Exception(code);}
    }
    internal void Attach(Process process)
    {
        if(AssignProcessToJobObject(job,process.Handle))return;
        int code=Marshal.GetLastWin32Error();try{process.Kill(true);}catch{}throw new Win32Exception(code);
    }
    // Set job membership atomically at process creation. Starting first and
    // attaching later leaves an orphan window if the controller dies, and can
    // race a program (winws2) which creates its own restricted nested job.
    // Only NUL and an optional explicitly supplied diagnostic writer are
    // inherited: never leak the agent's control/config pipe handles.
    // Only for a trusted helper blocked on its private control input. Attach
    // before the first request; parent death before attachment gives empty EOF.
    internal void AssignBlockedOnPrivateInput(Process process)
    {
        if(process.HasExited||!AssignProcessToJobObject(job,process.Handle))throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    // The kernel assigns the job as part of CreateProcess: the child cannot
    // execute even its first instruction outside the session's job.
    internal NativeChildProcess StartWithPipes(ProcessStartInfo info)
    {
        if(info.UseShellExecute||!info.CreateNoWindow||!info.RedirectStandardInput||!info.RedirectStandardOutput||!info.RedirectStandardError||!Path.IsPathFullyQualified(info.FileName)||!string.IsNullOrEmpty(info.Arguments)||!string.IsNullOrEmpty(info.UserName)||!string.IsNullOrEmpty(info.Domain)||info.LoadUserProfile||!string.IsNullOrEmpty(info.Verb)||info.ErrorDialog||info.WindowStyle!=ProcessWindowStyle.Normal||info.StandardInputEncoding!=null||info.StandardOutputEncoding!=null||info.StandardErrorEncoding!=null)throw new ArgumentException("Unsupported owned child process options.");
        string directory=string.IsNullOrEmpty(info.WorkingDirectory)?Environment.CurrentDirectory:Path.GetFullPath(info.WorkingDirectory);
        var security=new Security{Size=Marshal.SizeOf<Security>(),Inherit=1};
        if(!CreatePipe(out var childIn,out var parentIn,ref security,0))throw new Win32Exception(Marshal.GetLastWin32Error());
        using(childIn)using(parentIn)
        {
            if(!CreatePipe(out var parentOut,out var childOut,ref security,0))throw new Win32Exception(Marshal.GetLastWin32Error());
            using(parentOut)using(childOut)
            {
                if(!CreatePipe(out var parentErr,out var childErr,ref security,0))throw new Win32Exception(Marshal.GetLastWin32Error());
                using(parentErr)using(childErr)
                {
                    if(!SetHandleInformation(parentIn,1,0)||!SetHandleInformation(parentOut,1,0)||!SetHandleInformation(parentErr,1,0))throw new Win32Exception(Marshal.GetLastWin32Error());
                    IntPtr size=IntPtr.Zero;InitializeProcThreadAttributeList(IntPtr.Zero,2,0,ref size);if(size==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
                    IntPtr list=Marshal.AllocHGlobal(size),jobValue=Marshal.AllocHGlobal(IntPtr.Size),handles=Marshal.AllocHGlobal(IntPtr.Size*3),environment=IntPtr.Zero;bool initialized=false;
                    try
                    {
                        if(!InitializeProcThreadAttributeList(list,2,0,ref size))throw new Win32Exception(Marshal.GetLastWin32Error());initialized=true;
                        Marshal.WriteIntPtr(jobValue,job.DangerousGetHandle());Marshal.WriteIntPtr(handles,0,childIn.DangerousGetHandle());Marshal.WriteIntPtr(handles,IntPtr.Size,childOut.DangerousGetHandle());Marshal.WriteIntPtr(handles,2*IntPtr.Size,childErr.DangerousGetHandle());
                        if(!UpdateProcThreadAttribute(list,0,(IntPtr)0x2000d,jobValue,(IntPtr)IntPtr.Size,IntPtr.Zero,IntPtr.Zero)||!UpdateProcThreadAttribute(list,0,(IntPtr)0x20002,handles,(IntPtr)(IntPtr.Size*3),IntPtr.Zero,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());
                        var env=string.Join('\0',info.Environment.OrderBy(pair=>pair.Key,StringComparer.OrdinalIgnoreCase).Select(pair=>pair.Key+"="+pair.Value))+"\0\0";
                        byte[] envBytes=Encoding.Unicode.GetBytes(env);environment=Marshal.AllocHGlobal(envBytes.Length);Marshal.Copy(envBytes,0,environment,envBytes.Length);
                        var startup=new StartupEx{Startup=new Startup{Size=Marshal.SizeOf<StartupEx>(),Flags=0x100,Input=childIn.DangerousGetHandle(),Output=childOut.DangerousGetHandle(),Error=childErr.DangerousGetHandle()},Attributes=list};
                        var command=new StringBuilder(string.Join(" ",new[]{info.FileName}.Concat(info.ArgumentList).Select(QuoteArgument)));
                        if(!CreateProcessW(info.FileName,command,IntPtr.Zero,IntPtr.Zero,true,0x08080400,environment,directory,ref startup,out var created))throw new Win32Exception(Marshal.GetLastWin32Error());
                        try
                        {
                            Process? process=null;
                            try
                            {
                                process=Process.GetProcessById(checked((int)created.Pid));_=process.Handle;
                                // Transfer ownership using duplicated, non-inheritable parent ends.
                                if(!DuplicateHandle(new(-1),parentIn,new(-1),out var inCopy,0,false,2))throw new Win32Exception(Marshal.GetLastWin32Error());
                                SafeFileHandle? outCopy=null,errCopy=null;
                                try
                                {
                                    if(!DuplicateHandle(new(-1),parentOut,new(-1),out outCopy,0,false,2)||!DuplicateHandle(new(-1),parentErr,new(-1),out errCopy,0,false,2))throw new Win32Exception(Marshal.GetLastWin32Error());
                                    return new NativeChildProcess(process,inCopy,outCopy,errCopy);
                                }
                                catch{inCopy.Dispose();outCopy?.Dispose();errCopy?.Dispose();throw;}
                            }
                            catch(Exception original)
                            {
                                process?.Dispose();
                                if(WaitForSingleObject(created.Process,0)!=0&&(!TerminateProcess(created.Process,1)||WaitForSingleObject(created.Process,5000)!=0))throw new AggregateException("Owned child retirement unconfirmed after launch failure.",original,new IOException("Child termination unconfirmed."));
                                throw;
                            }
                        }
                        finally{CloseHandle(created.Thread);CloseHandle(created.Process);}
                    }
                    finally{if(initialized)DeleteProcThreadAttributeList(list);Marshal.FreeHGlobal(list);Marshal.FreeHGlobal(jobValue);Marshal.FreeHGlobal(handles);if(environment!=IntPtr.Zero)Marshal.FreeHGlobal(environment);GC.KeepAlive(job);}
                }
            }
        }
    }
    internal Process StartOwned(string image,IReadOnlyList<string> arguments,string directory,SafeHandle? diagnosticWriter=null)
    {
        if(!Path.IsPathFullyQualified(image)||!Path.IsPathFullyQualified(directory))throw new ArgumentException("Owned process paths must be absolute.");
        var security=new Security{Size=Marshal.SizeOf<Security>(),Inherit=1};
        using var nul=CreateFileW("NUL",0xc0000000,3,ref security,3,0,IntPtr.Zero);
        if(nul.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        using var output=DuplicateDiagnostic(diagnosticWriter);
        int handleBytes=IntPtr.Size*(output==null?1:2);
        IntPtr size=IntPtr.Zero;InitializeProcThreadAttributeList(IntPtr.Zero,2,0,ref size);
        if(size==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr list=Marshal.AllocHGlobal(size),jobValue=Marshal.AllocHGlobal(IntPtr.Size),handleValue=Marshal.AllocHGlobal(handleBytes);
        bool initialized=false;
        try
        {
            if(!InitializeProcThreadAttributeList(list,2,0,ref size))throw new Win32Exception(Marshal.GetLastWin32Error());initialized=true;
            Marshal.WriteIntPtr(jobValue,job.DangerousGetHandle());Marshal.WriteIntPtr(handleValue,nul.DangerousGetHandle());
            if(output!=null)Marshal.WriteIntPtr(handleValue,IntPtr.Size,output.DangerousGetHandle());
            if(!UpdateProcThreadAttribute(list,0,(IntPtr)0x2000d,jobValue,(IntPtr)IntPtr.Size,IntPtr.Zero,IntPtr.Zero)||
               !UpdateProcThreadAttribute(list,0,(IntPtr)0x20002,handleValue,(IntPtr)handleBytes,IntPtr.Zero,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup=new StartupEx{Startup=new Startup{Size=Marshal.SizeOf<StartupEx>(),Flags=0x100,Input=nul.DangerousGetHandle(),Output=output?.DangerousGetHandle()??nul.DangerousGetHandle(),Error=output?.DangerousGetHandle()??nul.DangerousGetHandle()},Attributes=list};
            var command=new StringBuilder(string.Join(" ",new[]{image}.Concat(arguments).Select(QuoteArgument)));
            if(!CreateProcessW(image,command,IntPtr.Zero,IntPtr.Zero,true,0x08080000,IntPtr.Zero,directory,ref startup,out var created))throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var process=Process.GetProcessById(checked((int)created.Pid));
                try{_=process.Handle;return process;}
                catch{process.Dispose();throw;}
            }
            finally{CloseHandle(created.Thread);CloseHandle(created.Process);}
        }
        finally{if(initialized)DeleteProcThreadAttributeList(list);Marshal.FreeHGlobal(list);Marshal.FreeHGlobal(jobValue);Marshal.FreeHGlobal(handleValue);GC.KeepAlive(job);GC.KeepAlive(nul);}
    }
    static SafeFileHandle? DuplicateDiagnostic(SafeHandle? source)
    {
        if(source==null)return null;
        if(!DuplicateHandle(new(-1),source,new(-1),out var target,0,true,2))throw new Win32Exception(Marshal.GetLastWin32Error());
        return target;
    }
    internal static string QuoteArgument(string value)
    {
        if(value.Length>0&&!value.Any(c=>char.IsWhiteSpace(c)||c=='"'))return value;
        var result=new StringBuilder("\"");int slashes=0;
        foreach(char c in value)
        {
            if(c=='\\'){slashes++;continue;}
            result.Append('\\',c=='"'?slashes*2+1:slashes);result.Append(c);slashes=0;
        }
        return result.Append('\\',slashes*2).Append('"').ToString();
    }
    internal NativeJobUsage ReadUsage()
    {
        if(!QueryInformationJobObject(job,1,out Accounting accounting,(uint)Marshal.SizeOf<Accounting>(),IntPtr.Zero)||!QueryInformationJobObject(job,9,out Limits limits,(uint)Marshal.SizeOf<Limits>(),IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(checked(accounting.User+accounting.Kernel),accounting.Total,accounting.Active,limits.PeakJobMemory.ToUInt64());
    }
    internal async Task TerminateAsync(CancellationToken stop)
    {
        if(!TerminateJobObject(job,1))throw new Win32Exception(Marshal.GetLastWin32Error());
        for(int i=0;i<100;i++)
        {if(!QueryInformationJobObject(job,1,out Accounting state,(uint)Marshal.SizeOf<Accounting>(),IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());if(state.Active==0)return;await Task.Delay(100,stop);}
        throw new IOException("Native process retirement unconfirmed.");
    }
    public void Dispose()=>job.Dispose();
}
