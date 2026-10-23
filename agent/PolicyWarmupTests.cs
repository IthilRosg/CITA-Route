using System.Net.NetworkInformation;
using System.Text.Json.Nodes;

namespace RoutePilot.Agent;

internal static class PolicyWarmupTests
{
    internal static async Task Run()
    {
        static void Check(bool condition,string message){if(!condition)throw new IOException(message);}
        string root=Path.Combine(Path.GetTempPath(),"CitaWarm-"+Guid.NewGuid().ToString("N"));
        foreach(var part in new[]{"runtime/rules","data"})Directory.CreateDirectory(Path.Combine(root,part));
        try
        {
            File.WriteAllText(Path.Combine(root,"force-direct.txt"),"");File.WriteAllText(Path.Combine(root,"force-vpn.txt"),"");
            Storage.Write(Path.Combine(root,"runtime/bridge.json"),new{outbounds=new[]{new{bind_interface=NetworkInterface.GetAllNetworkInterfaces().First().Name}}});
            var session=new PolicySession{Id="warm",Running=true};Storage.Write(Path.Combine(root,"runtime/session.json"),session);
            string fingerprint=PolicyNetwork.Signature(root),epoch=session.Id+":"+fingerprint;
            DateTime now=DateTime.UtcNow;var key=new PolicyKey(epoch,"warm.fixture.invalid",Path.Combine(Environment.SystemDirectory,"cmd.exe"),443);
            var expired=new PolicyDecision(key,"vpn","expired fixture",now.AddHours(-2),now.AddHours(-2).AddMinutes(15),1){Evidence="BodySample"};
            PolicyCache Cache(string signature)=>new(signature,"Auto",false,"",new[]{expired}){BodyProbes=true,WarmHints=new[]{new PolicyWarmHint(key,now.AddHours(-1))}};
            Storage.Write(Path.Combine(root,"data/policy-cache.json"),Cache(fingerprint));
            var worker=new PolicyWorker(root,session,new(){Mode="Auto"});worker.LoadCache();worker.Publish(false);
            Check(worker.WarmQueued==1,"Expired evidence should become a hint, never disappear before warmup.");
            Check(JsonNode.Parse(File.ReadAllText(Path.Combine(root,"runtime/rules/auto-vpn.json")))!["rules"]!.AsArray().Count==0,"A hint must not authorize an expired VPN route.");
            worker.Clear();Check(worker.WarmQueued==0,"Explicit Clear removes queued warmup.");
            Storage.Write(Path.Combine(root,"data/policy-cache.json"),Cache("different-network"));
            worker=new PolicyWorker(root,session,new(){Mode="Auto"});worker.LoadCache();Check(worker.WarmQueued==0,"A different network/profile must reject hints.");
            Storage.Write(Path.Combine(root,"data/policy-cache.json"),Cache(fingerprint));
            worker=new PolicyWorker(root,session,new(){Mode="Auto",Warmup=false});worker.LoadCache();Check(worker.WarmQueued==0,"Warmup can be disabled independently.");
            Check(!PolicyWorker.WarmHintValid(new(key,now.AddSeconds(1)),now)&&!PolicyWorker.WarmHintValid(new(key,now.AddHours(-25)),now),"Future and old hints must expire.");
            Check(!PolicyWorker.WarmHintValid(new(key with{Strategy="old"},now),now),"Algorithm changes must invalidate hints.");
            // Saturate input to check bounded history and reload, without network probes.
            worker=new PolicyWorker(root,session,new(){Mode="Auto"});
            for(int i=0;i<1000;i++)worker.Touch(key with{Domain=$"host{i}.fixture.invalid"},now);
            worker.Publish();
            var cache=Storage.Read<PolicyCache>(Path.Combine(root,"data/policy-cache.json"));
            Check(cache.WarmHints.Length==256,"History must remain bounded under many destinations.");
            worker=new PolicyWorker(root,session,new(){Mode="Auto"});worker.LoadCache();Check(worker.WarmQueued==256,"Bounded history survives worker restart without creating routes.");
            long tick=1000;int launched=0;
            worker=new PolicyWorker(root,session,new(){Mode="Auto"},async (k,ct)=>
            {launched++;await Task.Delay(Timeout.Infinite,ct);throw new IOException("unreachable");},()=>tick);
            worker.LoadCache();using var stop=new CancellationTokenSource();
            worker.DrainWarmup(stop.Token);worker.DrainWarmup(stop.Token);
            Check(launched==1,"One warm job must leave the other controller slot for foreground work.");
            worker.Clear();await worker.CompletePending();
            Check(worker.WarmQueued==0,"Clear cancels in-flight work and queued warmup.");
            tick=1000;worker=new PolicyWorker(root,session,new(){Mode="Auto"},(k,ct)=>throw new IOException("Must not launch after deadline"),()=>tick);
            worker.LoadCache();tick+=30001;worker.DrainWarmup(stop.Token);Check(worker.WarmQueued==0,"Startup window is bounded by monotonic time.");
            File.WriteAllText(Path.Combine(root,"force-vpn.txt"),"fixture.invalid");
            worker=new PolicyWorker(root,session,new(){Mode="Auto"});worker.LoadCache();Check(worker.WarmQueued==0,"Manual suffix rules exclude warmup.");
            await Task.CompletedTask;
        }
        finally{Directory.Delete(root,true);}
        Console.WriteLine("PASS: startup warmup hints never authorize expired routes; network/strategy isolation, Clear, opt-out and bounded history.");
    }
}
