using System.Runtime.InteropServices;

namespace RoutePilot.Agent;

// Equality evidence only; never expose this value in diagnostics.
internal sealed record NativeNetworkEvidence(string Nic,string Routes);

internal static class NativeIpv4Routes
{
    // MIB_IPFORWARDROW.dwForwardMetric1 includes the interface metric on
    // Vista and later; ROW2.Metric has different semantics. See:
    // https://learn.microsoft.com/en-us/windows/win32/api/ipmib/ns-ipmib-mib_ipforwardrow
    [StructLayout(LayoutKind.Sequential,Size=56)]
    internal struct Row
    {
        internal uint Destination,Mask,Policy,NextHop,InterfaceIndex,Type,Protocol,Age,NextHopAs,Metric1,Metric2,Metric3,Metric4,Metric5;
    }
    [DllImport("iphlpapi.dll",ExactSpelling=true)]
    static extern uint GetIpForwardTable(IntPtr table,ref uint size,[MarshalAs(UnmanagedType.Bool)] bool order);
    internal static string Canonical(IEnumerable<Row> rows)=>string.Join(";",rows.Select(r=>string.Join(",",r.Destination,r.Mask,r.Policy,r.NextHop,r.InterfaceIndex,r.Type,r.Protocol,r.NextHopAs,r.Metric1,r.Metric2,r.Metric3,r.Metric4,r.Metric5)).OrderBy(x=>x,StringComparer.Ordinal));
    static string ReadOnce()
    {
        uint size=0;uint result=GetIpForwardTable(IntPtr.Zero,ref size,false);
        if(result!=122||size<4||size>8*1024*1024)throw new IOException("IPv4 route table unavailable.");
        for(int retry=0;retry<3;retry++)
        {
            IntPtr buffer=Marshal.AllocHGlobal(checked((int)size));
            try
            {
                uint allocated=size;result=GetIpForwardTable(buffer,ref size,false);
                if(result==122&&size>allocated&&size<=8*1024*1024)continue;
                if(result!=0||size<4||size>allocated)throw new IOException("IPv4 route table unavailable.");
                uint count=unchecked((uint)Marshal.ReadInt32(buffer));int rowSize=Marshal.SizeOf<Row>();
                if(rowSize!=56||count>(size-4)/(uint)rowSize)throw new IOException("IPv4 route table malformed.");
                var rows=new Row[checked((int)count)];for(int i=0;i<rows.Length;i++)rows[i]=Marshal.PtrToStructure<Row>(IntPtr.Add(buffer,checked(4+i*rowSize)));
                return Canonical(rows);
            }
            finally{Marshal.FreeHGlobal(buffer);}
        }
        throw new IOException("IPv4 route table changed during read.");
    }
    internal static string ReadStable(){string first=ReadOnce(),second=ReadOnce();if(first!=second)throw new IOException("IPv4 route table changed during read.");return second;}
}

// Lock covers capture and admission, so competing notifications cannot reorder
// lastObserved and incorrectly admit a duplicate after a change and revert.
internal sealed class NativeNetworkEvidenceGate
{
    readonly object sync=new();
    NativeNetworkEvidence? lastObserved;
    internal NativeNetworkEvidenceGate(NativeNetworkEvidence? initial)=>lastObserved=initial;
    internal bool Observe(bool availability,Func<NativeNetworkEvidence?> read,Action invalidate)
    {
        lock(sync)
        {
            if(availability)invalidate();
            NativeNetworkEvidence? current;try{current=read();}catch{current=null;}
            bool changed=availability||current==null||lastObserved==null||current!=lastObserved;
            lastObserved=current;
            if(changed&&!availability)invalidate();
            return changed;
        }
    }
    internal bool Matches(NativeNetworkEvidence? expected,Func<NativeNetworkEvidence?> read)
    {
        lock(sync){try{return expected!=null&&read()==expected;}catch{return false;}}
    }
}
