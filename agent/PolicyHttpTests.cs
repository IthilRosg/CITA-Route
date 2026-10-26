using System.Net.NetworkInformation;
using System.Text.Json;

namespace RoutePilot.Agent;

internal static class PolicyHttpTests
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    internal static async Task Run(string temporary)
    {
        var ports=new PolicyPorts();var calls=new List<int>();
        var rootUri=new Uri("https://example.com/");
        Assert(PolicyTransport.Redirect(rootUri,302,new Uri("/en/",UriKind.Relative),0)?.AbsoluteUri=="https://example.com/en/","A same-host HTTPS redirect can complete a bounded independent body probe.");
        foreach(var target in new[]{"http://example.com/","https://other.example.com/","https://example.com:8443/","https://user:secret@example.com/","https://127.0.0.1/"})
            Assert(PolicyTransport.Redirect(rootUri,302,new Uri(target),0)==null,"Redirect must preserve pinned TLS hostname and public destination boundary.");
        Assert(PolicyTransport.Redirect(rootUri,302,new Uri("/loop",UriKind.Relative),2)==null&&PolicyTransport.Redirect(rootUri,200,new Uri("/x",UriKind.Relative),0)==null,"Redirect count and response status are bounded.");
        var chosen=await PolicyTransport.Select((port,_)=>
        {calls.Add(port);return Task.FromResult(PolicyTransport.HttpResponse(port==ports.Vpn?200:451,1));},ports,CancellationToken.None);
        Assert(chosen.Route=="vpn"&&chosen.Candidates.Take(2).All(c=>c.Outcome==ProbeOutcome.HttpRestricted)&&calls.Count(p=>p==ports.Vpn)==2,
            "Two authenticated HTTP 451 refusals must permit a twice-confirmed VPN instead of certifying direct.");
        chosen=await PolicyTransport.Select((_,_)=>Task.FromResult(PolicyTransport.HttpResponse(451,1)),ports,CancellationToken.None);
        Assert(chosen.Route==null&&chosen.Outcome==ProbeOutcome.HttpRestricted,"A 451 on every path cannot produce a usable route.");
        chosen=await PolicyTransport.Select((port,_)=>Task.FromResult(PolicyTransport.HttpResponse(port==ports.Direct?403:200,1)),ports,CancellationToken.None);
        Assert(chosen.Route=="direct","A generic 403/browser challenge is not sufficient evidence to route around a site's authentication policy.");
        foreach(int status in new[]{204,301,401,403,404,429,500,503})
        {
            var response=PolicyTransport.HttpResponse(status,1,verifyBody:true);
            Assert(!response.Reachable&&!response.Definitive&&response.ConnectionEstablished&&response.Evidence=="BodyUnknown",
                "An application response must not certify completed body delivery or claim a network block.");
        }
        chosen=await PolicyTransport.Select((_,_)=>Task.FromResult(PolicyTransport.HttpResponse(403,1,verifyBody:true)),ports,CancellationToken.None);
        Assert(chosen.Route==null&&chosen.Outcome==ProbeOutcome.Indeterminate,"A challenge on all paths cannot create a usable-content route.");
        Task<CandidateResult> BodyCandidate(int port,CancellationToken _) => Task.FromResult(port==ports.Vpn?
            new CandidateResult(true,false,"bounded body",1){Evidence="BodySample",BodyBytes=65536}:
            PolicyTransport.HttpResponse(403,1,verifyBody:true));
        chosen=await PolicyTransport.Select(BodyCandidate,ports,CancellationToken.None);
        Assert(chosen.Route==null&&chosen.Outcome==ProbeOutcome.Indeterminate,"An isolated root challenge cannot trigger automatic VPN selection without a real failure hint.");
        chosen=await PolicyTransport.Select(BodyCandidate,ports,CancellationToken.None,allowUnstableFallback:true);
        Assert(chosen.Route=="vpn"&&chosen.Candidates[2].Evidence=="BodySample"&&PolicyTransport.UnstablePreferred(chosen),
            "A real failure hint may select a twice-proved body path temporarily; the challenge itself remains uncertain.");
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
        PolicyWorker worker=null!;
        string log=Path.Combine(root,"logs/router.log");
        string fixture="""
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

            """;
        foreach(string newline in new[]{"\n","\r\n"})
        {
            scheduled.Clear();
            worker=new PolicyWorker(root,session,options,(candidate,_)=>{scheduled.Add(candidate);return Task.FromResult(new CalibrationResult(ProbeOutcome.Unavailable,null,0,0,Array.Empty<CandidateTiming>()));});
            File.WriteAllText(log,fixture.ReplaceLineEndings(newline));
            worker.Failures(log,CancellationToken.None);worker.DrainFailures(CancellationToken.None);
            Assert(scheduled.Count==2&&scheduled.Any(k=>k.Domain=="quick.example.com"&&k.Application==@"C:\Browser\browser.exe")&&scheduled.All(k=>k.Domain is "quick.example.com" or "unrelated.example.com"),
                $"Short FakeIP/first-use flows must trigger exactly their calibrations; unrelated failures/agent traffic must not. Newline={(newline.Length==2?"CRLF":"LF")}, scheduled={scheduled.Count}.");
            await worker.CompletePending();
        }
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
        Console.WriteLine("PASS: HTTP 451 fallback, generic 403 neutrality, TLS refusal, mixed-response uncertainty, LF/CRLF short FakeIP flow evidence and cache strategy migration.");
    }
}
