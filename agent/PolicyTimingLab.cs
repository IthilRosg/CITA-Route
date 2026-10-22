using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;

namespace RoutePilot.Agent;

// Bounded scheduling fixtures only. Does not resolve names, open external
// sockets, change routing or contact the installed service.
internal static class PolicyTimingLab
{
    sealed record Scenario(string Name,int DirectDelay,int DpiDelay,int VpnDelay,string? Expected,ProbeOutcome Outcome,bool Temporary=false);
    internal static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var scenarios=new[]{
            new Scenario("reachable-direct-retains-priority",60,8,3,"direct",ProbeOutcome.Reachable),
            new Scenario("ready-vpn-waits-for-direct",160,30,5,"vpn",ProbeOutcome.Reachable),
            new Scenario("dpi-retains-priority-over-vpn",25,50,3,"dpi",ProbeOutcome.Reachable),
            new Scenario("preferred-certificate-refusal",15,5,3,null,ProbeOutcome.TlsAuthenticationError),
            new Scenario("partial-direct-keeps-conservative-policy",20,5,3,null,ProbeOutcome.Indeterminate),
            new Scenario("failed-flow-allows-confirmed-reserve",20,5,3,"vpn",ProbeOutcome.Reachable,true),
            new Scenario("all-paths-unavailable",20,10,5,null,ProbeOutcome.Unavailable)
        };
        var samples=new List<object>();
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach(var scenario in scenarios)
        for(int iteration=0;iteration<3;iteration++)
        {
            var ports=new PolicyPorts();var calls=new int[3];int active=0,maximum=0;
            async Task<CandidateResult> Probe(int port,CancellationToken stop)
            {
                int index=port==ports.Direct?0:port==ports.Dpi?1:2;
                int count=Interlocked.Increment(ref calls[index]);
                int concurrency=Interlocked.Increment(ref active);
                int before;
                do{before=Volatile.Read(ref maximum);if(before>=concurrency)break;}while(Interlocked.CompareExchange(ref maximum,concurrency,before)!=before);
                try
                {
                    await Task.Delay(index==0?scenario.DirectDelay:index==1?scenario.DpiDelay:scenario.VpnDelay,stop);
                    bool reachable=scenario.Name switch
                    {
                        "reachable-direct-retains-priority"=>true,
                        "ready-vpn-waits-for-direct"=>index==2,
                        "dpi-retains-priority-over-vpn"=>index!=0,
                        "preferred-certificate-refusal"=>index!=0,
                        "partial-direct-keeps-conservative-policy"=>index!=0||count==1,
                        "failed-flow-allows-confirmed-reserve"=>index==2||(index==0&&count==1),
                        _=>false
                    };
                    bool certificateRefused=scenario.Name=="preferred-certificate-refusal"&&index==0;
                    return new(reachable,certificateRefused,"controlled fixture",1);
                }
                finally{Interlocked.Decrement(ref active);}
            }
            var timer=Stopwatch.StartNew();
            var selection=await PolicyTransport.Select(Probe,ports,budget.Token,scenario.Temporary);
            double wall=timer.Elapsed.TotalMilliseconds;
            bool correct=selection.Route==scenario.Expected&&selection.Outcome==scenario.Outcome&&active==0&&maximum<=3;
            samples.Add(new{scenario=scenario.Name,iteration,correct,route=selection.Route,outcome=selection.Outcome.ToString(),
                wallMilliseconds=wall,candidateReadyMilliseconds=selection.Route==null?(long?)null:selection.Milliseconds,
                decisionMilliseconds=Timing(selection,"DecisionMilliseconds"),completionMilliseconds=Timing(selection,"CompletionMilliseconds"),
                maximumConcurrentProbes=maximum,activeAfterReturn=active,candidates=selection.Candidates});
            if(!correct)throw new IOException("Controlled routing fixture failed: "+scenario.Name);
        }
        var assembly=Assembly.GetExecutingAssembly();
        string image=assembly.Location.Length>0?assembly.Location:Environment.ProcessPath!;
        Storage.Write(Path.Combine(directory,"policy-timing-lab.json"),new{schema=1,success=true,checkedUtc=DateTime.UtcNow,
            scope="controlled scheduling fixtures; no DNS, internet, service or route changes",imageSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(image))),
            scenarioCount=scenarios.Length,sampleCount=samples.Count,samples});
        Console.WriteLine("PASS: "+samples.Count+" controlled scheduling samples; report saved. No external connections or routing changes.");
    }
    // Enables the identical fixture to compare the pre-instrumentation build:
    // unavailable timing fields stay null rather than becoming false zeroes.
    static long? Timing(RouteSelection selection,string name)=>typeof(RouteSelection).GetProperty(name)?.GetValue(selection) as long?;
}
