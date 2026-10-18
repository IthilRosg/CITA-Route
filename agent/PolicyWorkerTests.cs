using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace RoutePilot.Agent;

internal static class PolicyWorkerTests
{
    internal static async Task Run(string temporary)
    {
        void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
        string root=Path.Combine(temporary,"policy");
        foreach(string folder in new[]{"runtime/rules","private","data","logs"})Directory.CreateDirectory(Path.Combine(root,folder));
        var session=new PolicySession{Id=Guid.NewGuid().ToString("N"),Running=true,RouterPid=Environment.ProcessId};
        Storage.Write(Path.Combine(root,"runtime/session.json"),session);
        Storage.Write(Path.Combine(root,"data/routing-policy.json"),new PolicyOptions());
        string bridge=Path.Combine(root,"runtime/bridge.json");
        string profile=Path.Combine(root,"private/SmartRouting-AWG2.conf");File.WriteAllText(profile,"synthetic-profile-generation-1");
        Storage.Write(bridge,new{outbounds=new[]{new{bind_interface=NetworkInterface.GetAllNetworkInterfaces().First().Name}}});
        File.WriteAllText(Path.Combine(root,"force-direct.txt"),"");File.WriteAllText(Path.Combine(root,"force-vpn.txt"),"");
        File.WriteAllText(Path.Combine(root,"private/controller-token.txt"),"isolated-test-token");

        // Hash changes simulate a network generation change without touching Windows routes.
        var identityWorker=new PolicyWorker(root,session,new PolicyOptions());
        Assert(identityWorker.Current(),"The current fixture must own its generation.");
        File.AppendAllText(profile,"-changed");
        Assert(!identityWorker.Current(),"A changed profile/network must reject a late publication.");

        var worker=new PolicyWorker(root,session,new PolicyOptions());
        worker.Status("Auto");
        string epoch;
        using(var state=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"runtime/policy-state.json"))))epoch=state.RootElement.GetProperty("epoch").GetString()!;
        var now=DateTime.UtcNow;
        for(int i=0;i<300;i++)worker.StoreDecision(new(new(epoch,$"site{i}.example.com",Environment.ProcessPath!,443),"vpn","fixture",now.AddTicks(i),now.AddMinutes(15),1));
        worker.Publish();worker.Status("Auto");
        using(var state=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"runtime/policy-state.json"))))
        using(var rules=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"runtime/rules/auto-vpn.json"))))
        {
            var decisions=state.RootElement.GetProperty("decisions");var published=rules.RootElement.GetProperty("rules");
            Assert(decisions.GetArrayLength()==PolicyWorker.DecisionLimit && published.GetArrayLength()==decisions.GetArrayLength(),"Every cached decision must have a published rule at capacity.");
            Assert(published.EnumerateArray().Any(r=>r.GetProperty("domain")[0].GetString()=="site299.example.com") && !published.EnumerateArray().Any(r=>r.GetProperty("domain")[0].GetString()=="site0.example.com"),"Cache capacity must evict old decisions and publish new ones.");
        }
        await Task.WhenAll(
            Task.Run(()=>{for(int i=0;i<10000;i++)worker.Event("probe A "+i);}),
            Task.Run(()=>{for(int i=0;i<10000;i++)worker.Event("probe B "+i);}),
            Task.Run(()=>{for(int i=0;i<100;i++)worker.Status("Auto");}));
        worker.Status("Auto");
        using(var state=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"runtime/policy-state.json"))))
            Assert(state.RootElement.GetProperty("events").GetArrayLength()==20,"Concurrent diagnostics must retain a bounded, valid snapshot.");
        Console.WriteLine("PASS: network/profile generation changes reject stale policy ownership.");
        Console.WriteLine("PASS: 300 adaptive decisions stay consistent with published rules; 20,000 concurrent probe events survive status snapshots.");
        var automatic=new PolicyOptions{Mode="Auto",AllowListedDpi=true};
        var blocked=new TaskCompletionSource<CalibrationResult>();
        worker=new PolicyWorker(root,session,automatic,(_,token)=>blocked.Task.WaitAsync(token));worker.Status("Auto");
        using(var state=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"runtime/policy-state.json"))))epoch=state.RootElement.GetProperty("epoch").GetString()!;
        var cachedKey=new PolicyKey(epoch,"cached.example.com",Environment.ProcessPath!,443);
        worker.StoreDecision(new(cachedKey,"vpn","confirmed",now.AddSeconds(-25),now.AddMinutes(10),1));worker.Publish();
        session=new(){Id=Guid.NewGuid().ToString("N"),Running=true,RouterPid=Environment.ProcessId};Storage.Write(Path.Combine(root,"runtime/session.json"),session);
        var restarted=new PolicyWorker(root,session,automatic);restarted.LoadCache();restarted.Publish();restarted.Status("Auto");
        using(var state=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"runtime/policy-state.json"))))
        {
            var decisions=state.RootElement.GetProperty("decisions");
            Assert(decisions.GetArrayLength()==1 && decisions[0].GetProperty("Key").GetProperty("Epoch").GetString()!.StartsWith(session.Id+":"),"A controller restart must reuse a fresh decision on the same network and retag its session.");
        }
        File.WriteAllText(Path.Combine(root,"runtime/clear-policy.request"),Guid.NewGuid().ToString("N"));
        var cleared=new PolicyWorker(root,session,automatic);cleared.LoadCache();cleared.Publish(false);cleared.Status("Auto");
        using(var state=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"runtime/policy-state.json"))))Assert(state.RootElement.GetProperty("decisions").GetArrayLength()==0,"An offline clear request must invalidate persistent decisions.");
        File.Delete(Path.Combine(root,"runtime/clear-policy.request"));File.AppendAllText(profile,"-changed-again");
        var otherNetwork=new PolicyWorker(root,session,automatic);otherNetwork.LoadCache();otherNetwork.Publish(false);otherNetwork.Status("Auto");
        using(var state=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"runtime/policy-state.json"))))Assert(state.RootElement.GetProperty("decisions").GetArrayLength()==0,"A cached route must never cross network fingerprints.");
        worker=new PolicyWorker(root,session,automatic,(_,token)=>blocked.Task.WaitAsync(token));worker.Status("Auto");
        using(var state=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"runtime/policy-state.json"))))epoch=state.RootElement.GetProperty("epoch").GetString()!;
        cachedKey=cachedKey with{Epoch=epoch};worker.StoreDecision(new(cachedKey,"vpn","confirmed",now.AddSeconds(-25),now.AddMinutes(10),1));
        foreach(var keyToFail in new[]{cachedKey,cachedKey with{Domain="busy1.example.com"},cachedKey with{Domain="busy2.example.com"}})
        {worker.RegisterFailure(keyToFail,"one",CancellationToken.None);worker.RegisterFailure(keyToFail,"two",CancellationToken.None);}
        worker.DrainFailures(CancellationToken.None);worker.Status("Auto");
        using(var state=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"runtime/policy-state.json"))))
        {
            Assert(state.RootElement.GetProperty("pending").GetInt32()==2 && state.RootElement.GetProperty("queued").GetInt32()==1,"Failures arriving during two in-flight probes must be queued instead of discarded.");
            Assert(state.RootElement.GetProperty("decisions").GetArrayLength()==1,"A failed hint must keep the old binding until a replacement is confirmed.");
        }
        blocked.SetResult(new(ProbeOutcome.Unavailable,null,0,1,Array.Empty<CandidateTiming>()));await worker.CompletePending();worker.Publish();worker.Status("Auto");
        using(var state=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"runtime/policy-state.json"))))
            Assert(state.RootElement.GetProperty("decisions").GetArrayLength()==1,"Unavailable candidates must not invent a successful direct decision.");
        Console.WriteLine("PASS: persistent cache survives controller restart, clear/network changes invalidate it, failed cached routes are reconsidered and busy probes retain queued failures.");
    }
}
