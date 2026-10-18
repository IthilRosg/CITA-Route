using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace RoutePilot.Agent;

internal static class PolicyLiveCheck
{
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
            timer.Restart();var ips=await PolicyTransport.Resolve(domain,ports,stop.Token);
            if(ips==null)throw new IOException("DNS unavailable for "+domain);
            var selected=await PolicyTransport.Select((port,token)=>PolicyTransport.Probe(domain,ips[0],port,token),ports,stop.Token);
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
