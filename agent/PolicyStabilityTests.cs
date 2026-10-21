using System.Diagnostics;
using System.Text.Json;

namespace RoutePilot.Agent;

internal static class PolicyStabilityTests
{
    static void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
    internal static async Task Run(string temporary)
    {
        var key=new PolicyKey("epoch","site.example.com",Environment.ProcessPath!,443);
        var now=DateTime.UtcNow;
        PolicyDecision Decision(string route,DateTime at)=>new(key,route,"fixture",at,at.AddMinutes(15),1);
        long stabilityTick=0;var gate=new PolicyStability(()=>stabilityTick);var old=Decision("vpn",now.AddMinutes(-1));
        Assert(gate.Accept(null,Decision("vpn",now),Array.Empty<CandidateTiming>(),now),"First confirmed route must not wait for return hysteresis.");
        Assert(!gate.Accept(old,Decision("direct",now),Array.Empty<CandidateTiming>(),now),"A single recovery burst must keep the working VPN binding.");
        stabilityTick=1000;
        Assert(!gate.Accept(old,Decision("direct",now.AddSeconds(1)),Array.Empty<CandidateTiming>(),now.AddSeconds(1)),"Two adjacent calibrations are not a stable return.");
        stabilityTick=15000;
        Assert(gate.Accept(old,Decision("direct",now.AddSeconds(15)),Array.Empty<CandidateTiming>(),now.AddSeconds(15)),"Independent delayed calibration confirms return.");
        Assert(!gate.Accept(old,Decision("direct",now),Array.Empty<CandidateTiming>(),now),"A new return starts new evidence.");
        Assert(!gate.Accept(old,Decision("dpi",now.AddSeconds(16)),Array.Empty<CandidateTiming>(),now.AddSeconds(16)),"A different candidate cannot inherit a direct confirmation.");
        Assert(gate.Accept(old,Decision("direct",now),new[]{new CandidateTiming("vpn",1,ProbeOutcome.Unavailable)},now),"Two failed probes of the old path allow prompt replacement.");
        Assert(gate.Accept(Decision("direct",now),Decision("vpn",now),Array.Empty<CandidateTiming>(),now),"Confirmed fallback is not delayed by preferred-return hysteresis.");
        gate.Clear();stabilityTick=0;
        Assert(!gate.Accept(old,Decision("direct",now),Array.Empty<CandidateTiming>(),now),"Begin a fresh return before a forward wall-clock step.");
        stabilityTick=1000;
        Assert(!gate.Accept(old,Decision("direct",now.AddSeconds(16)),Array.Empty<CandidateTiming>(),now.AddSeconds(16)),"Moving Windows time forward cannot shorten the real confirmation interval.");
        stabilityTick=16000;
        Assert(gate.Accept(old,Decision("direct",now.AddSeconds(31)),Array.Empty<CandidateTiming>(),now.AddSeconds(31)),"A real monotonic interval confirms the preferred return.");
        gate.Clear();Assert(!gate.Pending(key),"Clear retires unfinished return evidence.");
        Assert(!gate.Accept(old,Decision("direct",now),Array.Empty<CandidateTiming>(),now),"Start a return before clock rollback.");
        Assert(!gate.Accept(old,Decision("direct",now.AddSeconds(-1)),Array.Empty<CandidateTiming>(),now.AddSeconds(-1)),"Clock rollback invalidates prior confirmation.");
        for(int i=0;i<300;i++)gate.Accept(old with{Key=key with{Domain="site"+i+".example.com"}},Decision("direct",now) with{Key=key with{Domain="site"+i+".example.com"}},Array.Empty<CandidateTiming>(),now);
        Assert(gate.Count<=PolicyWorker.DecisionLimit,"Return evidence has a fixed capacity.");
        int reads=0;long clock=0;string network="first";
        using(var monitor=new PolicyNetworkMonitor(()=>{reads++;return network;},()=>clock,false))
        {
            for(int i=0;i<100;i++){clock=i*10;Assert(monitor.Read()=="first","Cached poll changed unexpectedly.");}
            Assert(reads==1,"Idle polling must not enumerate network profiles each time.");
            network="changed";Assert(monitor.Read(true)=="changed" && reads==2,"Publication always forces fresh network evidence.");
            network="event";monitor.Invalidate();Assert(monitor.Read()=="event" && reads==3,"Network event bypasses the polling cache.");
            clock+=2000;monitor.Read();Assert(reads==4,"A bounded fallback poll covers missed events.");
        }
        var completion=new TaskCompletionSource<CalibrationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer=Stopwatch.StartNew();var wait=PolicyWorker.WaitForWork(new[]{completion.Task},CancellationToken.None,TimeSpan.FromSeconds(30));
        completion.SetResult(new(ProbeOutcome.Unavailable,null,0,0,Array.Empty<CandidateTiming>()));await wait.WaitAsync(TimeSpan.FromSeconds(3));
        long wakeMs=timer.ElapsedMilliseconds;
        var ports=new PolicyPorts();int oldChecks=0;
        var selected=await PolicyTransport.Select(async(port,stop)=>{if(port==ports.Vpn){Interlocked.Increment(ref oldChecks);await Task.Delay(15,stop);}return new(port!=ports.Vpn,false,"fixture",1);},ports,CancellationToken.None,observeRoute:"vpn");
        Assert(selected.Route=="direct" && oldChecks==2 && selected.Candidates.Single(c=>c.Route=="vpn").Outcome==ProbeOutcome.Unavailable,"Preferred return includes the current route's complete independent evidence.");
        Storage.Write(Path.Combine(temporary,"policy-060-scheduling.json"),new{idlePolls=100,fullNetworkReads=1,publicationAlwaysFresh=true,returnConfirmationSeconds=15,probeCompletionWakeMilliseconds=wakeMs});
        Console.WriteLine("PASS: stable preferred return, failed-path fast replacement, bounded epoch evidence, event invalidation and immediate probe completion wake.");
    }
}
