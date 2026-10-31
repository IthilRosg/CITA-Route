using System.Text.Json;

namespace RoutePilot.Agent;

internal static class NativeNetworkTelemetryTests
{
    internal static void Run()
    {
        var telemetry=new NativeNetworkTelemetry();
        Parallel.For(0,1000,i=>{telemetry.Event(i%2==0);telemetry.Stamp(i%3==0?null:i%3==1);});
        telemetry.Reset(true);telemetry.Reset(false);
        using var doc=JsonDocument.Parse(JsonSerializer.Serialize(telemetry.Snapshot()));var root=doc.RootElement;
        void Equal(string name,long value){if(root.GetProperty(name).GetInt64()!=value)throw new Exception("Network diagnostic counter mismatch: "+name);}
        Equal("address",500);Equal("availability",500);Equal("stampUnreadable",334);Equal("stampChanged",333);Equal("stampUnchanged",333);Equal("coreResets",1);Equal("networkResets",1);
        if(root.GetProperty("lastEventTick").GetInt64()<=0||root.GetProperty("lastResetTick").GetInt64()<=0)throw new Exception("Missing monotonic diagnostic time.");
        if(NativeZapretSession.FailureReason(new NativeStaleEvidenceException())!="stale"||NativeZapretSession.FailureReason(new OperationCanceledException())!="cancelled"||NativeZapretSession.FailureReason(new IOException("private output"))!="io"||NativeZapretSession.FailureReason(new Exception("private output"))!="other")throw new Exception("Zapret diagnostic classification mismatch.");
        var failures=new NativeZapretSession.FailureTracker();
        var first=failures.Begin();failures.Finish(first,new("prepare","io"));
        if(failures.Current?.Reason!="io")throw new Exception("Failed attempt missing.");
        var second=failures.Begin();if(failures.Current!=null)throw new Exception("Previous failure survived next attempt.");
        failures.Finish(second,new("probe","stale"));failures.Finish(first,null);
        if(failures.Current?.Reason!="stale")throw new Exception("Old attempt cleared current failure.");
        var third=failures.Begin();failures.Finish(third,null);failures.Finish(second,new("verify","io"));
        if(failures.Current!=null)throw new Exception("Old attempt restored failure after success.");
    }
}
