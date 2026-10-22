using System.Diagnostics;
using System.Text.Json;

namespace RoutePilot.Agent;

internal static class PolicyTimingTests
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    internal static async Task Run()
    {
        var ports=new PolicyPorts();
        var vpnReady=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDirect=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int vpnCalls=0;
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        async Task<CandidateResult> Probe(int port,CancellationToken token)
        {
            if(port==ports.Direct){await releaseDirect.Task.WaitAsync(token);return new(false,false,"held direct",1);}
            if(port==ports.Vpn&&Interlocked.Increment(ref vpnCalls)==2)vpnReady.TrySetResult();
            return new(port==ports.Vpn,false,"controlled candidate",1);
        }
        var wall=Stopwatch.StartNew();
        var pending=PolicyTransport.Select(Probe,ports,stop.Token);
        try
        {
            await vpnReady.Task.WaitAsync(stop.Token);
            Assert(!pending.IsCompleted,"The fixture must exercise an already-ready VPN waiting on a preferred path.");
            await Task.Delay(80,stop.Token);releaseDirect.TrySetResult();
            var result=await pending;
            Assert(result.Route=="vpn"&&result.DecisionMilliseconds>=result.Milliseconds+50,
                "Decision timing must expose preference waiting instead of reporting only the fast candidate.");
            Assert(result.CompletionMilliseconds>=result.DecisionMilliseconds&&result.CompletionMilliseconds<=wall.ElapsedMilliseconds+2,
                "Selection milestones must be ordered and bounded by an independent wall stopwatch.");
        }
        finally{releaseDirect.TrySetResult();stop.Cancel();try{await pending;}catch(OperationCanceledException){}}

        var checkedUtc=DateTime.UtcNow;
        var measurement=new PolicyMeasurement{DecisionCheckedUtc=checkedUtc,FinishedUtc=checkedUtc};
        await Task.Delay(30);
        measurement.ConfirmApplied(checkedUtc.AddHours(-1),7);
        double? delay=measurement.ApplyWaitMilliseconds;
        Assert(delay>=15&&measurement.AppliedVersion==7,"Application delay must use monotonic time despite a changed UTC clock.");
        measurement.ConfirmApplied(checkedUtc.AddHours(1),8);
        Assert(measurement.ApplyWaitMilliseconds==delay&&measurement.AppliedVersion==7,"A later publication must not relabel an already confirmed measurement.");
        var deferred=new PolicyMeasurement();deferred.ConfirmApplied(checkedUtc,9);
        Assert(deferred.AppliedUtc==null&&deferred.ApplyWaitMilliseconds==null,"An unaccepted decision cannot acquire an application proof.");
        var legacy=new CalibrationResult(ProbeOutcome.Unavailable,null,0,0,Array.Empty<CandidateTiming>());
        Assert(legacy.CandidateReadyMilliseconds==null&&legacy.SelectionDecisionMilliseconds==null,
            "Missing measurements must remain unknown rather than fabricated zero latency.");
        Assert(!JsonSerializer.Serialize(measurement,Storage.Json).Contains("completedTimestamp",StringComparison.OrdinalIgnoreCase),
            "Process-local stopwatch timestamps must never be persisted as portable timestamps.");
        Console.WriteLine("PASS: actual selector decision/completion timing, independent wait evidence, monotonic application delay and legacy unknown timing.");
    }
}
