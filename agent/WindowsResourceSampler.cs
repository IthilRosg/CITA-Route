using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using RoutePilot.Resources;

namespace RoutePilot.Agent;

internal sealed class ResourceProcessTracker
{
    readonly Dictionary<int,DateTime> _known=new();
    public void Begin(IEnumerable<int> listed)
    {
        var ids=listed.ToHashSet();foreach(int pid in _known.Keys.Where(p=>!ids.Contains(p)).ToArray())_known.Remove(pid);
    }
    public bool Observe(int pid,ResourceProcess? current)
    {
        if(current==null)return !_known.ContainsKey(pid);
        if(_known.TryGetValue(pid,out var started)&&started==current.StartedUtc)current.CitaComponent=true;
        else _known.Remove(pid);
        if(current.CitaComponent)_known[pid]=current.StartedUtc;
        return true;
    }
}

public sealed class WindowsResourceSampler
{
    public readonly record struct UdpRow(int Family,int Port,int Pid);
    public readonly record struct PortRange(int Start,int Count);
    sealed record Layout(PortRange? Range,bool ReservationsKnown,bool[] Reserved);
    readonly Dictionary<int,Layout> _layouts=new();
    readonly HashSet<string> _owned;
    readonly string[] _watchRoots;
    readonly Dictionary<int,WatchResourceIdentity.Identity> _watchers=new();
    readonly ResourceProcessTracker _identities=new();
    List<ResourceProcess> _processes=new();
    readonly List<string> _processUnavailable=new();
    DateTime _layoutAt,_processesAt;
    [DllImport("iphlpapi.dll")]static extern uint GetExtendedUdpTable(IntPtr table,ref uint size,[MarshalAs(UnmanagedType.Bool)]bool order,int family,int tableClass,uint reserved);
    [DllImport("kernel32.dll",SetLastError=true)]static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,int pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool QueryFullProcessImageName(SafeProcessHandle process,uint flags,StringBuilder name,ref int size);
    [StructLayout(LayoutKind.Sequential)]struct PerformanceInfo
    {
        public uint Size;
        public nuint CommitTotal,CommitLimit,CommitPeak,PhysicalTotal,PhysicalAvailable,SystemCache,KernelTotal,KernelPaged,KernelNonpaged,PageSize;
        public uint Handles,Processes,Threads;
    }
    [DllImport("psapi.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool GetPerformanceInfo(ref PerformanceInfo value,uint size);
    public WindowsResourceSampler(string appRoot)
    {
        _watchRoots=new[]{Path.GetFullPath(appRoot),Path.GetFullPath(Cita.Startup.StartupPaths.Client)}.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string protectedRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RoutePilot","Agent");
        _owned=new(StringComparer.OrdinalIgnoreCase){Path.Combine(appRoot,"RoutePilot.exe"),Path.Combine(appRoot,"runtime","app-build","RoutePilot.exe"),
            Path.Combine(appRoot,"runtime","smart-router.exe"),Path.Combine(Cita.Startup.StartupPaths.Client,"runtime","smart-router.exe"),Path.Combine(protectedRoot,"RoutePilot.Agent.exe"),Path.Combine(protectedRoot,"vpn-bridge.exe"),
            Path.Combine(AppContext.BaseDirectory,"RoutePilot.Agent.exe")};
    }
    public static PortRange? ParseRange(string text)
    {
        var matches=Regex.Matches(text,@":\s*(\d+)[ \t]*\r?$",RegexOptions.Multiline);
        if(matches.Count!=2||!int.TryParse(matches[0].Groups[1].Value,out int start)||!int.TryParse(matches[1].Groups[1].Value,out int count)||start<1||count<1||start+(long)count>65536)return null;
        return new(start,count);
    }
    public static bool[] ParseReservations(string text)
    {
        var reserved=new bool[65536];
        foreach(Match row in Regex.Matches(text,@"^[ \t]*(\d+)[ \t]+(\d+)(?:[ \t]+\*)?[ \t]*\r?$",RegexOptions.Multiline))
        {
            if(!int.TryParse(row.Groups[1].Value,out int start)||!int.TryParse(row.Groups[2].Value,out int end)||start<1||end>65535||start>end)throw new InvalidDataException("Invalid excluded range.");
            Array.Fill(reserved,true,start,end-start+1);
        }
        return reserved;
    }
    public static List<UdpRow> ParseUdpTable(byte[] bytes,int family)
    {
        int rowSize=family==2?12:family==23?28:throw new ArgumentOutOfRangeException(nameof(family));
        if(bytes.Length<4)throw new InvalidDataException("Truncated UDP table.");
        uint count=BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if(count>262144||4L+(long)count*rowSize>bytes.Length)throw new InvalidDataException("Oversized or truncated UDP rows.");
        var rows=new List<UdpRow>((int)count);
        for(int index=0;index<count;index++)
        {
            int offset=4+index*rowSize+(family==2?4:20);
            int port=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset,2));
            uint pid=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset+4,4));
            rows.Add(new(family,port,pid<=int.MaxValue?(int)pid:0));
        }
        return rows;
    }
    public static List<UdpRow> ReadUdpTable(int family)
    {
        uint size=0;
        uint result=GetExtendedUdpTable(IntPtr.Zero,ref size,false,family,1,0);
        if(result!=0&&result!=122)throw new IOException("UDP table unavailable.");
        for(int attempt=0;attempt<3;attempt++)
        {
            if(size<4||size>16*1024*1024)throw new InvalidDataException("UDP table size rejected.");
            int capacity=(int)size;IntPtr buffer=Marshal.AllocHGlobal(capacity);
            try
            {
                result=GetExtendedUdpTable(buffer,ref size,false,family,1,0);
                if(result==122)continue;
                if(result!=0||size>capacity)throw new IOException("UDP table read failed.");
                var bytes=new byte[size];Marshal.Copy(buffer,bytes,0,bytes.Length);
                return ParseUdpTable(bytes,family);
            }
            finally{Marshal.FreeHGlobal(buffer);}
        }
        throw new IOException("UDP table changed too often.");
    }
    public static string CheckAllocation(AddressFamily family)
    {
        try
        {
            using var socket=new Socket(family,SocketType.Dgram,ProtocolType.Udp);
            if(family==AddressFamily.InterNetworkV6)socket.DualMode=false;
            socket.Bind(new IPEndPoint(family==AddressFamily.InterNetwork?IPAddress.Any:IPAddress.IPv6Any,0));
            return "Available";
        }
        catch(SocketException ex)
        {
            return ex.SocketErrorCode switch
            {
                SocketError.NoBufferSpaceAvailable or SocketError.TooManyOpenSockets or SocketError.AddressAlreadyInUse=>"ResourceFailure",
                SocketError.AccessDenied=>"AccessDenied",
                SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported=>"NotSupported",
                _=>"Unavailable"
            };
        }
    }
    static async Task<string> Netsh(int family,bool exclusions,CancellationToken stop)
    {
        var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","netsh.exe"))
            {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string value in new[]{"interface",family==2?"ipv4":"ipv6","show",exclusions?"excludedportrange":"dynamicport","protocol=udp"})info.ArgumentList.Add(value);
        using var process=Process.Start(info)??throw new IOException("Port configuration unavailable.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop);timeout.CancelAfter(TimeSpan.FromSeconds(2));
        async Task<byte[]> Read(Stream input)
        {
            using var result=new MemoryStream();byte[] buffer=new byte[2048];int count;
            while((count=await input.ReadAsync(buffer,timeout.Token))>0)
            {if(result.Length+count>32768)throw new InvalidDataException("Port configuration output exceeded limit.");result.Write(buffer,0,count);}
            return result.ToArray();
        }
        var output=Read(process.StandardOutput.BaseStream);var error=Read(process.StandardError.BaseStream);
        try
        {
            await process.WaitForExitAsync(timeout.Token);byte[] bytes=await output;await error;
            if(process.ExitCode!=0)throw new IOException("Port configuration command failed.");
            // Numeric values are ASCII on supported Windows locales. Never log
            // command output or treat a parser failure as the default range.
            return Encoding.Latin1.GetString(bytes);
        }
        catch
        {
            try{if(!process.HasExited)process.Kill(true);}catch{}
            try{await Task.WhenAll(output,error);}catch{}
            throw;
        }
    }
    public static ResourcePool Summarize(int family,IEnumerable<UdpRow> rows,PortRange? range,bool reservationsKnown,bool[] reserved)
    {
        var entries=rows.ToList();
        var pool=new ResourcePool{Family=family==2?"IPv4":"IPv6",TableAvailable=true,RangeAvailable=range!=null,ReservationsAvailable=reservationsKnown,Endpoints=entries.Count};
        if(range is {} r)
        {
            pool.Start=r.Start;pool.Count=r.Count;
            pool.Reserved=reservationsKnown?Enumerable.Range(r.Start,r.Count).Count(p=>reserved[p]):0;
            pool.Used=entries.Where(x=>x.Port>=r.Start&&x.Port<r.Start+r.Count&&(!reservationsKnown||!reserved[x.Port])).Select(x=>x.Port).Distinct().Count();
        }
        return pool;
    }
    ResourceProcess? ReadProcess(int pid,DateTime snapshot,int udp=0)
    {
        try
        {
            using var process=Process.GetProcessById(pid);
            DateTime started=process.StartTime.ToUniversalTime();
            if(started>snapshot)return null; // PID reuse/new process after the table snapshot.
            string name=process.ProcessName;
            name=new string(name.Where(c=>!char.IsControl(c)).Take(55).ToArray());
            bool owned=false;
            if(name is "RoutePilot" or "RoutePilot.Agent" or "smart-router" or "vpn-bridge"||(name=="powershell"&&_watchers.ContainsKey(pid)))
            {
                using var handle=OpenProcess(0x1000,false,pid);
                var image=new StringBuilder(32768);int size=image.Capacity;
                if(!handle.IsInvalid&&QueryFullProcessImageName(handle,0,image,ref size))
                    owned=_owned.Contains(image.ToString())||(_watchers.TryGetValue(pid,out var watcher)&&WatchResourceIdentity.Matches(watcher,pid,started,image.ToString()));
            }
            long? cpu=null,working=null;
            if(owned){try{cpu=Math.Max(0,(long)process.TotalProcessorTime.TotalMilliseconds);working=Math.Max(0,process.WorkingSet64);}catch(System.ComponentModel.Win32Exception){}}
            return new(){Pid=pid,StartedUtc=started,Name=name,PrivateBytes=Math.Max(0,process.PrivateMemorySize64),Handles=Math.Max(0,process.HandleCount),CitaComponent=owned,UdpPorts=udp,
                ObservedUtc=snapshot,WorkingSetBytes=working,CpuTotalMilliseconds=cpu};
        }
        catch(Exception ex)when(ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException){return null;}
    }
    public async Task<ResourceSample> Capture(CancellationToken stop)
    {
        var timer=Stopwatch.StartNew();var now=DateTime.UtcNow;
        var sample=new ResourceSample{ObservedUtc=now};
        _watchers.Clear();
        foreach(string root in _watchRoots)
        {
            var watcher=WatchResourceIdentity.Load(root,out bool limited);
            if(watcher!=null)_watchers[watcher.Pid]=watcher;
            if(limited&&!sample.Unavailable.Contains("CitaProcessCoverage"))sample.Unavailable.Add("CitaProcessCoverage");
        }
        if(now-_layoutAt>TimeSpan.FromMinutes(15))
        {
            foreach(int family in new[]{2,23})
            {
                PortRange? range=null;bool known=false;bool[] reserved=new bool[65536];
                try{range=ParseRange(await Netsh(family,false,stop));}catch(OperationCanceledException)when(stop.IsCancellationRequested){throw;}catch{}
                try{reserved=ParseReservations(await Netsh(family,true,stop));known=true;}catch(OperationCanceledException)when(stop.IsCancellationRequested){throw;}catch{}
                _layouts[family]=new(range,known,reserved);
            }
            _layoutAt=_layouts.Values.All(x=>x.Range!=null&&x.ReservationsKnown)?now:now.AddMinutes(-14);
        }
        var rows=new List<UdpRow>();
        foreach(int family in new[]{2,23})
        {
            stop.ThrowIfCancellationRequested();ResourcePool pool;
            try
            {
                var entries=ReadUdpTable(family);rows.AddRange(entries);var layout=_layouts[family];
                pool=Summarize(family,entries,layout.Range,layout.ReservationsKnown,layout.Reserved);
            }
            catch{pool=new(){Family=family==2?"IPv4":"IPv6"};}
            pool.Allocation=CheckAllocation(family==2?AddressFamily.InterNetwork:AddressFamily.InterNetworkV6);
            if(!pool.TableAvailable||!pool.RangeAvailable||!pool.ReservationsAvailable)sample.Unavailable.Add(pool.Family+"PortCoverage");
            sample.Pools.Add(pool);
        }
        try
        {
            var value=new PerformanceInfo{Size=(uint)Marshal.SizeOf<PerformanceInfo>()};
            if(!GetPerformanceInfo(ref value,value.Size))throw new IOException();
            long Bytes(nuint pages)=>checked((long)pages*(long)value.PageSize);
            sample.PhysicalTotal=Bytes(value.PhysicalTotal);sample.PhysicalAvailable=Bytes(value.PhysicalAvailable);
            sample.CommitTotal=Bytes(value.CommitTotal);sample.CommitLimit=Bytes(value.CommitLimit);sample.SystemHandles=value.Handles;sample.MemoryAvailable=true;
        }
        catch{sample.Unavailable.Add("SystemMemory");}
        if(now-_processesAt>=TimeSpan.FromSeconds(30))
        {
            _processUnavailable.Clear();
            var all=new List<ResourceProcess>();
            var listed=Process.GetProcesses();
            _identities.Begin(listed.Select(p=>p.Id));
            try
            {
                foreach(var process in listed.Take(2048))
                {
                    stop.ThrowIfCancellationRequested();var item=ReadProcess(process.Id,now);
                    bool known=_identities.Observe(process.Id,item);
                    if(item!=null)all.Add(item);
                    else if(!known&&!_processUnavailable.Contains("CitaProcessCoverage"))_processUnavailable.Add("CitaProcessCoverage");
                }
                if(listed.Length>2048)_processUnavailable.Add("ProcessCoverageLimited");
                if(all.Count(p=>p.CitaComponent)>16&&!_processUnavailable.Contains("CitaProcessCoverage"))_processUnavailable.Add("CitaProcessCoverage");
            }
            finally{foreach(var process in listed)process.Dispose();}
            _processes=all.Where(p=>p.CitaComponent).Concat(all.OrderByDescending(p=>p.PrivateBytes).Take(5)).Concat(all.OrderByDescending(p=>p.Handles).Take(5)).DistinctBy(p=>p.Pid).Take(16).ToList();
            _processesAt=now;
        }
        foreach(string unavailable in _processUnavailable)if(!sample.Unavailable.Contains(unavailable))sample.Unavailable.Add(unavailable);
        var selected=_processes.ToDictionary(p=>p.Pid,p=>p);
        var owners=rows.Where(r=>r.Pid>0).GroupBy(r=>r.Pid).Select(g=>(Pid:g.Key,Count:g.Select(x=>(x.Family,x.Port)).Distinct().Count())).OrderByDescending(x=>x.Count).Take(8);
        foreach(var owner in owners)
        {
            var current=ReadProcess(owner.Pid,now,owner.Count);
            if(!_identities.Observe(owner.Pid,current)&&!sample.Unavailable.Contains("CitaProcessCoverage"))sample.Unavailable.Add("CitaProcessCoverage");
            if(current!=null)selected[owner.Pid]=current;
            else{selected.Remove(owner.Pid);sample.UnresolvedOwners++;}
        }
        sample.Processes=selected.Values.OrderByDescending(p=>p.UdpPorts).ThenByDescending(p=>p.CitaComponent).Take(24).ToList();
        sample.ProcessesObservedUtc=_processesAt;
        sample.DurationMs=timer.ElapsedMilliseconds;
        return sample;
    }
}
