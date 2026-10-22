using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoutePilot.Agent;

internal static class PolicyWebTests
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    internal static async Task Run(string temporary)
    {
        var now=DateTime.UtcNow;long tick=0;
        foreach(string invalid in new[]{"http://example.com/image.jpg","https://127.0.0.1/image.jpg","https://printer.local/image.jpg","https://user:password@example.com/image.jpg","https://example.com:8443/image.jpg","https://example.com/image.jpg?token=secret"})
        {bool refused=false;try{PolicyResourceCheck.Target(invalid);}catch(ArgumentException){refused=true;}Assert(refused,"Resource diagnostics must reject local, credential-bearing and unsupported URLs.");}
        Assert(PolicyResourceCheck.Target("https://cdn.example.com/image.jpg").IdnHost=="cdn.example.com","An explicit public credential-free resource is allowed.");
        var signals=new PolicyWebSignals(()=>tick);var app=@"C:\Browser\msedge.exe";
        PolicyKey Key(string name)=>new("epoch",name,app,443);
        for(int i=0;i<100;i++)signals.Discover(Key($"cdn{i}.example.com"),now);
        Assert(signals.Queued==64,"Discovery queue must be bounded independently of failure hints.");
        int taken=0;for(int i=0;i<10;i++)if(signals.Take(_=>true,now)!=null)taken++;
        Assert(taken==4,"First-use discovery is limited to a four-host burst.");
        tick=3000;Assert(signals.Take(_=>true,now)!=null&&signals.Take(_=>true,now)==null,"Discovery refills at most 20 hosts/minute after its bounded burst.");
        tick=9000;Assert(signals.Take(_=>true,now.AddMinutes(3))==null&&signals.Queued==0,"Old queued destinations must expire without probes.");
        var key=Key("asset.example.com");
        signals.Fault(key,"direct","one",now);signals.Fault(key,"direct","one",now.AddSeconds(1));
        Assert(signals.Avoided(key,now.AddSeconds(1)).Count==0,"Duplicate observations of one flow must never quarantine its route.");
        signals.Fault(key,"direct","two",now.AddSeconds(2));
        Assert(signals.Avoided(key,now.AddSeconds(3)).SetEquals(new[]{"direct"})&&signals.Avoided(Key("other.example.com"),now.AddSeconds(3)).Count==0,"Two distinct faults only affect the exact app/domain/route.");
        Assert(signals.Avoided(key,now.AddMinutes(4)).Count==0&&signals.Avoided(key,now.AddMinutes(-1)).Count==0,"Fault evidence expires and cannot survive a backwards clock.");
        var ports=new PolicyPorts();
        var chosen=await PolicyTransport.Select((_,_)=>Task.FromResult(PolicyTransport.HttpResponse(200,1)),ports,CancellationToken.None,avoidRoutes:new HashSet<string>{"direct","dpi"});
        Assert(chosen.Route=="vpn"&&chosen.Candidates.Take(2).All(c=>c.Outcome==ProbeOutcome.ApplicationFailure),"A healthy root cannot erase repeated stream failures; the fallback still needs two own successful HTTPS checks.");
        chosen=await PolicyTransport.Select((port,_)=>Task.FromResult(port==ports.Direct?new CandidateResult(false,true,"bad certificate",1):PolicyTransport.HttpResponse(200,1)),ports,CancellationToken.None,avoidRoutes:new HashSet<string>{"direct"});
        Assert(chosen.Route==null&&chosen.Outcome==ProbeOutcome.TlsAuthenticationError,"Application faults must not hide an actual certificate error.");
        chosen=await PolicyTransport.Select((_,_)=>Task.FromResult(PolicyTransport.HttpResponse(200,1)),ports,CancellationToken.None,avoidRoutes:new HashSet<string>{"direct","dpi","vpn"});
        Assert(chosen.Route==null&&chosen.Outcome==ProbeOutcome.ApplicationFailure,"No route may be invented if every path has current fault evidence.");

        string root=Path.Combine(temporary,"web-policy");
        foreach(string part in new[]{"runtime/rules","data","private","logs"})Directory.CreateDirectory(Path.Combine(root,part));
        File.WriteAllText(Path.Combine(root,"force-direct.txt"),"");File.WriteAllText(Path.Combine(root,"force-vpn.txt"),"");
        Storage.Write(Path.Combine(root,"runtime/bridge.json"),new{outbounds=new[]{new{bind_interface=NetworkInterface.GetAllNetworkInterfaces().First().Name}}});
        var session=new PolicySession{Id=Guid.NewGuid().ToString("N"),Running=true,RouterPid=Environment.ProcessId};Storage.Write(Path.Combine(root,"runtime/session.json"),session);
        var config=JsonNode.Parse("""{"route":{"rules":[{"type":"logical","mode":"and","rules":[{"rule_set":["auto-blocked"]},{"ip_version":6,"invert":true}],"action":"reject"}]}}""")!;
        Assert(!PolicyWebRouting.Supported(root),"An old/unknown engine topology must not receive QUIC fallback rules.");
        PolicyWebRouting.Configure(config);PolicyWebRouting.Configure(config);
        Assert(config["route"]!["rules"]![0]!["rules"]!.AsArray().Count==3&&PolicyWebRouting.Guarded(config["route"]!["rules"]![0]),"The engine protocol guard must be idempotent and keep non-QUIC UDP outside rejection.");
        Storage.Write(Path.Combine(root,"runtime/router.json"),config);
        Assert(PolicyWebRouting.Supported(root),"A guarded generated topology can accept scoped browser fallback.");
        var options=new PolicyOptions{Mode="Auto",AllowListedDpi=true};
        var worker=new PolicyWorker(root,session,options);worker.Status("Auto");
        string epoch=Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json")).GetProperty("epoch").GetString()!;
        key=new(epoch,"asset.example.com",app,443);
        var decision=new PolicyDecision(key,"vpn","fixture",now,now.AddMinutes(10),1);
        worker.StoreDecision(decision);worker.ObserveDestination(key,"tcp","tls",now);worker.Publish(false);
        int Blocked()=>Storage.Read<JsonElement>(Path.Combine(root,"runtime/rules/auto-blocked.json")).GetProperty("rules").GetArrayLength();
        Assert(Blocked()==0,"A TCP decision alone cannot reject QUIC that was never observed.");
        worker.ObserveDestination(key,"udp","",now);worker.Publish(false);
        var fallback=Storage.Read<JsonElement>(Path.Combine(root,"runtime/rules/auto-blocked.json")).GetProperty("rules");
        Assert(fallback.GetArrayLength()==1&&fallback[0].GetProperty("network").GetString()=="udp"&&fallback[0].GetProperty("domain")[0].GetString()==key.Domain&&fallback[0].GetProperty("process_path")[0].GetString()==app,"QUIC-to-TCP compatibility must be scoped to the exact observed browser, domain and port.");
        worker.StoreDecision(decision with{Route="direct"});worker.Publish(false);Assert(Blocked()==0,"Returning to a confirmed direct path removes compatibility rejection.");
        worker.StoreDecision(decision with{ExpiresUtc=now.AddSeconds(-1)});worker.Publish(false);Assert(Blocked()==0,"An expired TCP proof must not retain a QUIC rejection.");
        var game=key with{Application=@"C:\Game\game.exe"};worker.StoreDecision(decision with{Key=game});worker.ObserveDestination(game,"udp","quic",now);worker.Publish(false);Assert(Blocked()==0,"A QUIC-only game must not be forced to TCP by browser compatibility.");
        worker.StoreDecision(decision);File.WriteAllText(Path.Combine(root,"force-direct.txt"),key.Domain);worker.Publish(false);Assert(Blocked()==0,"Explicit manual rules override browser compatibility.");File.WriteAllText(Path.Combine(root,"force-direct.txt"),"");
        // Explicit response-stream errors used to be ignored because they do
        // not say 'open connection to'. Normal user cancellation is excluded.
        string log=Path.Combine(root,"logs/router.log");
        string Flow(int id,string tail)=>$"INFO [{id} 0ms] router: found process path: {app}\nINFO [{id} 0ms] outbound/direct[direct]: outbound connection to asset.example.com:443\nERROR [{id} 10ms] {tail}\n";
        File.WriteAllText(log,Flow(11,"connection download closed: connection reset by peer"));worker.Failures(log,CancellationToken.None);
        Assert(worker.AvoidedRoutes(key,DateTime.UtcNow).Count==0,"One stream error remains only a probe hint.");
        File.AppendAllText(log,Flow(11,"connection download closed: connection reset by peer"));worker.Failures(log,CancellationToken.None);
        Assert(worker.AvoidedRoutes(key,DateTime.UtcNow).Count==0,"Duplicate log lines are one failure, not two.");
        File.AppendAllText(log,Flow(12,"connection download closed: connection reset by peer"));worker.Failures(log,CancellationToken.None);
        Assert(worker.AvoidedRoutes(key,DateTime.UtcNow).SetEquals(new[]{"direct"}),"Two explicit stream errors must quarantine the correct route despite response bytes.");
        worker.Clear();File.AppendAllText(log,Flow(13,"connection download closed: context canceled")+Flow(14,"connection upload closed: use of closed network connection"));worker.Failures(log,CancellationToken.None);
        Assert(worker.AvoidedRoutes(key,DateTime.UtcNow).Count==0,"Normal cancellations must not create a route quarantine.");
        File.WriteAllText(Path.Combine(root,"runtime/router-proxy.json"),"{}");Assert(!PolicyWebRouting.Supported(root),"Mixed old/new prepared topologies must disable compatibility.");
        var pending=new TaskCompletionSource<CalibrationResult>();int started=0;
        worker=new(root,session,options,(_,token)=>{started++;return pending.Task.WaitAsync(token);});
        worker.ObserveDestination(new(epoch,"server.tailnet.ts.net",app,443),"tcp","tls",now);
        worker.ObserveDestination(new(epoch,"control.example.com",@"C:\Tailscale\tailscaled.exe",443),"tcp","tls",now);
        worker.ObserveDestination(new(epoch,"resolver.example.com",@"C:\Cita\smart-router.exe",443),"tcp","tls",now);
        worker.Status("Auto");Assert(Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json")).GetProperty("discoveryQueued").GetInt32()==0,"Tailnet names and managed transports must never enter discovery or recursive routing probes.");
        for(int i=0;i<5;i++)worker.ObserveDestination(new(epoch,$"new{i}.example.com",app,443),"tcp","tls",now);
        worker.ObserveDestination(new(epoch,"ignored.example.com",@"C:\Agent\RoutePilot.Agent.exe",443),"tcp","tls",now);
        worker.DrainFailures(CancellationToken.None);Assert(started==2,"First-use discovery must share the existing two-calibration concurrency limit and exclude the diagnostic agent.");
        worker.Clear();pending.SetResult(new(ProbeOutcome.Unavailable,null,0,0,Array.Empty<CandidateTiming>()));await worker.CompletePending();
        await PolicyBrowserFeedbackTests.Run();
        Console.WriteLine("PASS: bounded first-use CDN discovery, repeated stream-fault quarantine, root-vs-body evidence, certificate guards and scoped browser QUIC/TCP rules.");
    }
}
