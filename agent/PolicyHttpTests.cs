using System.Net.NetworkInformation;
using System.Text.Json;

namespace RoutePilot.Agent;

internal static class PolicyHttpTests
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    internal static async Task Run(string temporary)
    {
        var ports=new PolicyPorts();var calls=new List<int>();
        var chosen=await PolicyTransport.Select((port,_)=>
        {calls.Add(port);return Task.FromResult(PolicyTransport.HttpResponse(port==ports.Vpn?200:451,1));},ports,CancellationToken.None);
        Assert(chosen.Route=="vpn"&&chosen.Candidates.Take(2).All(c=>c.Outcome==ProbeOutcome.HttpRestricted)&&calls.Count(p=>p==ports.Vpn)==2,
            "Two authenticated HTTP 451 refusals must permit a twice-confirmed VPN instead of certifying direct.");
        chosen=await PolicyTransport.Select((_,_)=>Task.FromResult(PolicyTransport.HttpResponse(451,1)),ports,CancellationToken.None);
        Assert(chosen.Route==null&&chosen.Outcome==ProbeOutcome.HttpRestricted,"A 451 on every path cannot produce a usable route.");
        chosen=await PolicyTransport.Select((port,_)=>Task.FromResult(PolicyTransport.HttpResponse(port==ports.Direct?403:200,1)),ports,CancellationToken.None);
        Assert(chosen.Route=="direct","A generic 403/browser challenge is not sufficient evidence to route around a site's authentication policy.");
        chosen=await PolicyTransport.Select((port,_)=>Task.FromResult(port==ports.Direct?new CandidateResult(false,true,"untrusted certificate",1):PolicyTransport.HttpResponse(200,1)),ports,CancellationToken.None);
        Assert(chosen.Route==null&&chosen.Outcome==ProbeOutcome.TlsAuthenticationError,"HTTP classification must not bypass TLS authentication failures.");
        int directCalls=0;
        chosen=await PolicyTransport.Select((port,_)=>Task.FromResult(PolicyTransport.HttpResponse(port==ports.Direct?(++directCalls==1?200:451):200,1)),ports,CancellationToken.None);
        Assert(chosen.Route==null&&chosen.Outcome==ProbeOutcome.Indeterminate,"Mixed direct responses must remain uncertain until independently confirmed.");
        var key=new PolicyKey("epoch","fixture.example.com",Environment.ProcessPath!,443);var now=DateTime.UtcNow;
        var old=new PolicyDecision(key,"vpn","old",now.AddMinutes(-2),now.AddMinutes(2),1);
        Assert(new PolicyStability().Accept(old,old with{Route="direct",CheckedUtc=now},new[]{new CandidateTiming("vpn",1,ProbeOutcome.HttpRestricted)},now),
            "Two verified HTTP restrictions on the previous path permit immediate recovery to an independently proved preferred route.");

        string root=Path.Combine(temporary,"http-policy");
        foreach(string folder in new[]{"runtime/rules","private","data","logs"})Directory.CreateDirectory(Path.Combine(root,folder));
        Storage.Write(Path.Combine(root,"runtime/bridge.json"),new{outbounds=new[]{new{bind_interface=NetworkInterface.GetAllNetworkInterfaces().First().Name}}});
        var session=new PolicySession{Id=Guid.NewGuid().ToString("N"),Running=true,RouterPid=Environment.ProcessId};
        Storage.Write(Path.Combine(root,"runtime/session.json"),session);
        File.WriteAllText(Path.Combine(root,"force-direct.txt"),"");File.WriteAllText(Path.Combine(root,"force-vpn.txt"),"");
        var options=new PolicyOptions{Mode="Auto",AllowListedDpi=true};var scheduled=new List<PolicyKey>();
        var worker=new PolicyWorker(root,session,options,(candidate,_)=>{scheduled.Add(candidate);return Task.FromResult(new CalibrationResult(ProbeOutcome.Unavailable,null,0,0,Array.Empty<CandidateTiming>()));});
        string log=Path.Combine(root,"logs/router.log");
        File.WriteAllText(log,"""
            INFO [10001 0ms] inbound/tun[system-tun]: inbound connection to 198.18.1.12:443
            INFO [10001 0ms] router: found process path: C:\Browser\browser.exe
            INFO [10001 1ms] outbound/direct[direct]: outbound connection to quick.example.com:443
            INFO [10001 2ms] outbound/direct[direct]: outbound connection to 1.1.1.1:443
            ERROR [10001 25ms] connection: open connection to quick.example.com:443 using outbound/direct[direct]: fixture refusal
            INFO [10002 0ms] router: found process path: C:\Browser\browser.exe
            INFO [10002 1ms] outbound/direct[direct]: outbound connection to unrelated.example.com:443
            ERROR [10002 25ms] connection: open connection to other.example.com:443 using outbound/direct[direct]: fixture mismatch
            INFO [10003 0ms] router: found process path: C:\Client\RoutePilot.Agent.exe
            INFO [10003 1ms] outbound/direct[direct]: outbound connection to agent.example.com:443
            ERROR [10003 25ms] connection: open connection to agent.example.com:443 using outbound/direct[direct]: fixture agent

            """);
        worker.Failures(log,CancellationToken.None);worker.DrainFailures(CancellationToken.None);
        Assert(scheduled.Count==1&&scheduled[0].Domain=="quick.example.com"&&scheduled[0].Application==@"C:\Browser\browser.exe",
            "A short FakeIP flow must trigger calibration without an API snapshot; mismatched flow names and diagnostic-agent traffic must not.");
        await worker.CompletePending();
        worker.Status("Auto");
        string epoch=Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json")).GetProperty("epoch").GetString()!;
        worker.StoreDecision(new(new(epoch,"cached.example.com",Environment.ProcessPath!,443),"direct","fixture",now,now.AddMinutes(5),1));worker.Publish();
        string cache=Path.Combine(root,"data/policy-cache.json");string fresh=File.ReadAllText(cache);
        File.WriteAllText(cache,fresh.Replace(PolicyKey.CurrentStrategy,"tls-record-ipv4-v2",StringComparison.Ordinal));
        var legacy=new PolicyWorker(root,session,options);legacy.LoadCache();legacy.Status("Auto");
        Assert(Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json")).GetProperty("decisions").GetArrayLength()==0,
            "The old TLS-only strategy must not certify cached direct routes after HTTP restriction semantics change.");
        File.WriteAllText(cache,fresh);var current=new PolicyWorker(root,session,options);current.LoadCache();current.Status("Auto");
        Assert(Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json")).GetProperty("decisions").GetArrayLength()==1,"Current-strategy cache remains reusable.");
        Console.WriteLine("PASS: HTTP 451 fallback, generic 403 neutrality, TLS refusal, mixed-response uncertainty, short FakeIP flow evidence and cache strategy migration.");
    }
}
