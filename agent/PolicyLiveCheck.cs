using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace RoutePilot.Agent;

internal static class PolicyLiveCheck
{
    internal static async Task Site(string root,string domain)
    {
        domain=PolicySafety.Domain(domain)??throw new ArgumentException("Specify a public DNS hostname, not a URL or address.");
        var session=Storage.Read<PolicySession>(Path.Combine(root,"runtime/session.json"));
        if(!session.Running||session.BridgeGeneration<=0)throw new IOException("An active ordinary session with a known bridge generation is required.");
        var ports=PolicyPorts.Read(root);using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(14));
        var attempts=new System.Collections.Concurrent.ConcurrentQueue<object>();
        var resolved=await PolicyTransport.SelectResolved(domain,ports,stop.Token,probe:async(address,port,token)=>
        {
            var result=await PolicyTransport.Probe(domain,address,port,token);
            attempts.Enqueue(new{route=port==ports.Vpn?"vpn":port==ports.Dpi?"dpi":"direct",result.Reachable,result.Definitive,result.HttpStatus,result.Milliseconds});
            return result;
        });
        var after=Storage.Read<PolicySession>(Path.Combine(root,"runtime/session.json"));
        if(!after.Running||after.Id!=session.Id||after.BridgeGeneration!=session.BridgeGeneration)throw new IOException("Routing generation changed; site observation discarded.");
        var report=new{success=true,domain,sessionId=session.Id,bridgeGeneration=session.BridgeGeneration,finishedUtc=DateTime.UtcNow,route=resolved.Selection.Route,
            outcome=resolved.Selection.Outcome.ToString(),resolved.DnsMilliseconds,candidates=resolved.Selection.Candidates,attempts=attempts.ToArray(),
            scope="Own credential-free DNS/HEAD only; no rules applied or user requests replayed; TLS/HTTP reachability does not certify browser challenges or the whole application"};
        Storage.Write(Path.Combine(root,"logs/site-check-last.json"),report);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report,Storage.Json));
    }
    internal static async Task Run(string root)
    {
        var session=Storage.Read<PolicySession>(Path.Combine(root,"runtime/session.json"));
        if(!session.Running)throw new IOException("An active ordinary session is required; this check never connects it.");
        var ports=PolicyPorts.Read(root);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var unused=new TcpListener(IPAddress.Loopback,0);unused.Start();int closed=((IPEndPoint)unused.LocalEndpoint).Port;unused.Stop();
        var dnsPorts=new PolicyPorts{Direct=closed,Vpn=ports.Vpn};
        var timer=Stopwatch.StartNew();
        var fallback=await PolicyTransport.Resolve("example.com",dnsPorts,stop.Token);
        if(fallback==null || fallback.Any(ip=>!PolicySafety.Public(ip)))throw new IOException("VPN DNS fallback did not return validated public IPv4.");
        var results=new List<object>{new{test="DNS fallback with an unavailable probe-only direct listener",success=true,milliseconds=timer.ElapsedMilliseconds}};
        foreach(string domain in new[]{"yandex.ru","discord.com"})
        {
            timer.Restart();var resolved=await PolicyTransport.SelectResolved(domain,ports,stop.Token);
            var selected=resolved.Selection;
            results.Add(new{test=domain,success=selected.Route!=null,route=selected.Route,outcome=selected.Outcome.ToString(),milliseconds=timer.ElapsedMilliseconds,candidates=selected.Candidates});
            if(domain=="yandex.ru" && selected.Route!="direct")throw new IOException("A reachable direct control site did not select direct.");
            if(selected.Route==null)throw new IOException(domain+": "+selected.Outcome);
        }
        var legacyDirect=await PolicyWorker.Probe("discord.com",ports.Direct,stop.Token);
        var legacyVpn=await PolicyWorker.Probe("discord.com",ports.Vpn,stop.Token);
        if(!legacyVpn.Reachable || (!legacyDirect.Reachable && legacyDirect.Definitive))throw new IOException("Protected diagnostic probe misclassified the TLS/transport path.");
        results.Add(new{test="protected diagnostic probe compatibility",success=true,directReachable=legacyDirect.Reachable,vpnReachable=legacyVpn.Reachable});
        var after=Storage.Read<PolicySession>(Path.Combine(root,"runtime/session.json"));
        if(after.Id!=session.Id || !after.Running)throw new IOException("The session changed during the observation.");
        var report=new{success=true,sessionId=session.Id,finishedUtc=DateTime.UtcNow,results,scope="credential-free diagnostic HEAD/DNS only; no routing changes or user-request replay"};
        Storage.Write(Path.Combine(root,"logs/optimization-live-last.json"),report);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report,Storage.Json));
    }
}
