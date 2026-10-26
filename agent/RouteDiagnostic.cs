using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace RoutePilot.Agent;

internal sealed record RouteDiagnosticPath(string Route,string Outcome,string Evidence,int? HttpStatus,long Milliseconds,long BodyBytes);
internal sealed record RouteDiagnosticReport(int Schema,string Domain,bool Current,string Code,RouteDiagnosticPath[] Paths);
internal static class RouteDiagnostic
{
    internal static async Task<RouteDiagnosticReport> Check(string domain,PolicyPorts ports,Func<bool> current,CancellationToken stop,
        Func<int,CancellationToken,Task<IPAddress[]?>>? resolve=null,
        Func<string,IPAddress,int,CancellationToken,Task<CandidateResult>>? probe=null)
    {
        if(PolicySafety.Domain(domain)!=domain)throw new ArgumentException("A normalized public DNS domain is required.");
        if(!current())return new(1,domain,false,"SessionUnavailable",Array.Empty<RouteDiagnosticPath>());
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(stop);lifetime.CancelAfter(TimeSpan.FromSeconds(11));
        async Task<IPAddress[]?> Resolve(int port)
        {
            try{return await (resolve?.Invoke(port,lifetime.Token)??PolicyTransport.ResolveConfigured(domain,port,lifetime.Token));}
            catch(OperationCanceledException){stop.ThrowIfCancellationRequested();return null;}
        }
        var direct=Resolve(ports.DirectDns);var vpn=Resolve(ports.Vpn);
        async Task<RouteDiagnosticPath> Path(string route,int port,Task<IPAddress[]?> dns)
        {
            var timer=Stopwatch.StartNew();
            try
            {
                var addresses=await dns;
                if(addresses==null||addresses.Length==0)return new(route,"DnsUnavailable","",null,timer.ElapsedMilliseconds,0);
                if(addresses.Any(ip=>!PolicySafety.Public(ip)))return new(route,"UnsafeAddress","",null,timer.ElapsedMilliseconds,0);
                CandidateResult? last=null;
                async Task<CandidateResult> Probe(IPAddress ip,CancellationToken token)=>last=await
                    (probe?.Invoke(domain,ip,port,token)??PolicyTransport.Probe(domain,ip,port,token,verifyBody:true));
                var first=await PolicyTransport.ProbeAddresses(addresses,Probe,lifetime.Token);
                var second=await PolicyTransport.ProbeAddresses(addresses,Probe,lifetime.Token);
                string outcome=first.Definitive&&!first.Reachable||second.Definitive&&!second.Reachable?"TlsAuthenticationError":
                    first.Reachable&&second.Reachable?"ContentConfirmed":first.HttpStatus==451&&second.HttpStatus==451?"HttpRestricted":
                    first.Evidence=="BodyStalled"&&second.Evidence=="BodyStalled"?"BodyStalled":
                    first.Reachable||second.Reachable||first.Evidence.StartsWith("Body",StringComparison.Ordinal)||second.Evidence.StartsWith("Body",StringComparison.Ordinal)?"Indeterminate":"TransportUnavailable";
                return new(route,outcome,last?.Evidence??"",last?.HttpStatus,timer.ElapsedMilliseconds,Math.Min(first.BodyBytes,second.BodyBytes));
            }
            catch(OperationCanceledException){stop.ThrowIfCancellationRequested();return new(route,"BudgetExceeded","",null,timer.ElapsedMilliseconds,0);}
        }
        var results=await Task.WhenAll(Path("direct",ports.Direct,direct),Path("dpi",ports.Dpi,direct),Path("vpn",ports.Vpn,vpn));
        stop.ThrowIfCancellationRequested();
        bool valid=current();return new(1,domain,valid,valid?"Measured":"SessionChanged",valid?results:Array.Empty<RouteDiagnosticPath>());
    }
    internal static async Task<RouteDiagnosticReport> Run(string root,string domain,CancellationToken stop)
    {
        // This is an explicit, read-only user diagnostic. It neither publishes
        // routes nor replays the URL, cookies, credentials or request body.
        var session=ComponentLiveness.Read<PolicySession>(Path.Combine(root,"runtime","session.json"),131072);
        int generation=session.BridgeGeneration;
        string healthPath=Path.Combine(root,"runtime","session-health.json");
        bool Current()
        {
            try
            {
                var now=ComponentLiveness.Read<JsonElement>(Path.Combine(root,"runtime","session.json"),131072);
                if(!now.GetProperty("running").GetBoolean()||now.GetProperty("id").GetString()!=session.Id||
                    now.GetProperty("bridgeGeneration").GetInt32()!=generation||!now.GetProperty("bridgeReady").GetBoolean()||
                    now.TryGetProperty("recovering",out var recovering)&&recovering.GetBoolean())return false;
                string safety=Path.Combine(root,"runtime","bridge-safety.json");
                if(File.Exists(safety))
                {
                    var hold=ComponentLiveness.Read<JsonElement>(safety,16384);
                    if(hold.TryGetProperty("sessionId",out var owner)&&owner.GetString()==session.Id&&hold.GetProperty("hold").GetBoolean())return false;
                }
                var health=ComponentLiveness.Read<JsonElement>(healthPath,131072);
                DateTime updated=health.GetProperty("updatedUtc").GetDateTime();
                return health.GetProperty("sessionId").GetString()==session.Id&&health.GetProperty("bridgeGeneration").GetInt32()==generation&&
                    updated<=DateTime.UtcNow.AddSeconds(2)&&DateTime.UtcNow-updated<TimeSpan.FromSeconds(90)&&health.GetProperty("healthy").GetBoolean();
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException){return false;}
        }
        using var gate=new Semaphore(1,1,@"Global\CITA.Route.DomainDiagnostic");
        if(!gate.WaitOne(0))return new(1,domain,false,"Busy",Array.Empty<RouteDiagnosticPath>());
        try{return await Check(domain,PolicyPorts.Read(root),Current,stop);}
        finally{gate.Release();}
    }
}
