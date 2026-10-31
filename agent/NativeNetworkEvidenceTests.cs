namespace RoutePilot.Agent;

internal static class NativeNetworkEvidenceTests
{
    internal static void Run()
    {
        var r=new NativeIpv4Routes.Row{Destination=1,Mask=2,NextHop=3,InterfaceIndex=4,Type=5,Protocol=6,Metric1=7,Age=8};
        var other=new NativeIpv4Routes.Row{Destination=22,Metric2=4};
        string canonical=NativeIpv4Routes.Canonical([r,other]);
        var aged=r;aged.Age++;if(canonical!=NativeIpv4Routes.Canonical([other,aged]))throw new Exception("Route order or age changed evidence.");
        var metric=r;metric.Metric1++;var extraMetric=r;extraMetric.Metric5++;var hop=r;hop.NextHop++;
        if(canonical==NativeIpv4Routes.Canonical([metric,other])||canonical==NativeIpv4Routes.Canonical([extraMetric,other])||canonical==NativeIpv4Routes.Canonical([hop,other]))throw new Exception("Material route change was ignored.");
        var a=new NativeNetworkEvidence("nic-a",canonical);var b=new NativeNetworkEvidence("nic-b",canonical);var c=new NativeNetworkEvidence("nic-a",NativeIpv4Routes.Canonical([hop,other]));
        var gate=new NativeNetworkEvidenceGate(a);int invalidations=0;void Invalidate()=>invalidations++;
        if(gate.Observe(false,()=>a,Invalidate)||invalidations!=0)throw new Exception("Identical event invalidated.");
        if(!gate.Observe(false,()=>b,Invalidate)||!gate.Observe(false,()=>a,Invalidate)||!gate.Observe(false,()=>c,Invalidate)||invalidations!=3)throw new Exception("Change or revert was ignored.");
        if(!gate.Observe(false,()=>throw new IOException(),Invalidate)||!gate.Observe(false,()=>c,Invalidate)||!gate.Observe(true,()=>c,Invalidate)||invalidations!=6)throw new Exception("Unreadable or availability must invalidate.");
        if(!gate.Matches(c,()=>c)||gate.Matches(c,()=>a)||gate.Matches(c,()=>throw new IOException()))throw new Exception("Current evidence validation failed.");
    }
}
