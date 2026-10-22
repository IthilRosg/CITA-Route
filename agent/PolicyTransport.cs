using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace RoutePilot.Agent;

public enum ProbeOutcome { Reachable, Unavailable, Indeterminate, DnsUnavailable, TlsAuthenticationError, Cancelled, HttpRestricted, ApplicationFailure }
public sealed record CandidateTiming(string Route,long Milliseconds,ProbeOutcome Outcome);
public sealed record RouteSelection(ProbeOutcome Outcome,string? Route,long Milliseconds,CandidateTiming[] Candidates)
{
    // Milliseconds remains the selected candidate's duration for compatibility.
    // These monotonic milestones include preference/observation waiting and drain.
    public long? DecisionMilliseconds{get;init;}
    public long? CompletionMilliseconds{get;init;}
}
internal sealed record CalibrationResult(ProbeOutcome Outcome,PolicyDecision? Decision,long DnsMilliseconds,long TotalMilliseconds,CandidateTiming[] Candidates)
{
    public long? CandidateReadyMilliseconds{get;init;}
    public long? SelectionDecisionMilliseconds{get;init;}
    public long? SelectionCompletionMilliseconds{get;init;}
}
public sealed class PolicyPorts
{
    public int Api{get;set;}=20810;
    public int Direct{get;set;}=20829;
    public int Dpi{get;set;}=20828;
    public int DirectDns{get;set;}=20832;
    public int Vpn{get;set;}=20808;
    public int Ack{get;set;}=20830;
    public int Proof{get;set;}=20831;
    internal int DnsPort(int probePort)=>probePort==Direct||probePort==Dpi?DirectDns:probePort;
    internal static PolicyPorts Read(string root)=>File.Exists(Path.Combine(root,"runtime","policy-ports.json"))?Storage.Read<PolicyPorts>(Path.Combine(root,"runtime","policy-ports.json")):new();
}

internal static class PolicyTransport
{
    internal static CandidateResult HttpResponse(int status,long milliseconds)
    {
        // A valid TLS response proves the peer, not that this route can serve
        // the application. 451 explicitly refuses access. A generic 403 may be
        // a browser challenge/authentication requirement on every route.
        bool restricted=status==451;
        return new(!restricted,!restricted,restricted?"TLS established; HTTP 451 access restriction":"TLS established; HTTP "+status,milliseconds)
        {ConnectionEstablished=true,HttpStatus=status};
    }
    // IP-only SOCKS destinations keep probes on the validated address. HttpClient
    // still owns TLS and validates the ORIGINAL URI hostname, SNI and certificate.
    internal static async Task<Stream> Socks(int port,string host,int destinationPort,CancellationToken stop)
    {
        var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp){NoDelay=true};
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback,port,stop);
            var stream=new NetworkStream(socket,true);
            await stream.WriteAsync(new byte[]{5,1,0},stop);
            byte[] hello=new byte[2];await stream.ReadExactlyAsync(hello,stop);
            if(hello[0]!=5 || hello[1]!=0)throw new IOException("Local SOCKS authentication refused.");
            byte[] address;
            if(IPAddress.TryParse(host,out var ip))address=new[]{(byte)(ip.AddressFamily==AddressFamily.InterNetwork?1:4)}.Concat(ip.GetAddressBytes()).ToArray();
            else {byte[] name=Encoding.ASCII.GetBytes(host);if(name.Length>253)throw new ArgumentException("Invalid SOCKS hostname.");address=new byte[]{3,(byte)name.Length}.Concat(name).ToArray();}
            await stream.WriteAsync(new byte[]{5,1,0}.Concat(address).Concat(new[]{(byte)(destinationPort>>8),(byte)destinationPort}).ToArray(),stop);
            byte[] reply=new byte[4];await stream.ReadExactlyAsync(reply,stop);
            if(reply[0]!=5 || reply[1]!=0)throw new IOException("Local SOCKS route refused.");
            int size=reply[3] switch{1=>4,4=>16,3=>-1,_=>throw new IOException("Invalid SOCKS response.")};
            if(size<0){byte[] length=new byte[1];await stream.ReadExactlyAsync(length,stop);size=length[0];}
            await stream.ReadExactlyAsync(new byte[size+2],stop);
            return stream;
        }
        catch{socket.Dispose();throw;}
    }
    internal static HttpClient Client(int port,IPAddress address,TimeSpan timeout,Action<SslPolicyErrors>? certificateResult=null,Action? connected=null)
    {
        var handler=new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,ConnectTimeout=timeout,
            ConnectCallback=async(_,stop)=>{var stream=await Socks(port,address.ToString(),443,stop);connected?.Invoke();return stream;}};
        if(certificateResult!=null)handler.SslOptions.RemoteCertificateValidationCallback=(_,_,_,errors)=>{certificateResult(errors);return errors==SslPolicyErrors.None;};
        return new HttpClient(handler){Timeout=timeout};
    }
    internal static async Task<CandidateResult> Probe(string domain,IPAddress address,int port,CancellationToken stop)
    {
        if(PolicySafety.Domain(domain)!=domain || !PolicySafety.Public(address))throw new ArgumentException("Invalid public policy probe.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop);timeout.CancelAfter(TimeSpan.FromSeconds(3));
        int rejectedCertificate=0,connectionEstablished=0;
        using var client=Client(port,address,TimeSpan.FromSeconds(3),errors=>{if(errors!=SslPolicyErrors.None)Interlocked.Exchange(ref rejectedCertificate,1);},()=>Interlocked.Exchange(ref connectionEstablished,1));
        using var request=new HttpRequestMessage(HttpMethod.Head,"https://"+domain+"/"){Version=HttpVersion.Version11,VersionPolicy=HttpVersionPolicy.RequestVersionExact};
        var timer=Stopwatch.StartNew();
        try
        {using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);return HttpResponse((int)response.StatusCode,timer.ElapsedMilliseconds);}
        catch(HttpRequestException)when(Volatile.Read(ref rejectedCertificate)!=0)
        {return new(false,true,"TLS authentication failed.",timer.ElapsedMilliseconds){ConnectionEstablished=Volatile.Read(ref connectionEstablished)!=0};}
        catch(Exception ex)when(ex is HttpRequestException or IOException or OperationCanceledException or SocketException)
        {stop.ThrowIfCancellationRequested();return new(false,false,ex is HttpRequestException http?http.HttpRequestError+": "+http.Message:ex.GetType().Name,timer.ElapsedMilliseconds){ConnectionEstablished=Volatile.Read(ref connectionEstablished)!=0};}
    }
    internal static async Task<IPAddress[]?> Resolve(string domain,PolicyPorts ports,CancellationToken stop)
    {
        if(PolicySafety.Domain(domain)!=domain)return null;
        foreach(var resolver in new[]{("cloudflare-dns.com","/dns-query?type=A&name=",IPAddress.Parse("1.1.1.1")),("dns.google","/resolve?type=A&name=",IPAddress.Parse("8.8.8.8"))})
        {
            using var round=CancellationTokenSource.CreateLinkedTokenSource(stop);round.CancelAfter(TimeSpan.FromMilliseconds(1600));
            var tasks=new[]{ports.Direct,ports.Vpn}.Select(port=>ResolveOne(domain,resolver.Item1,resolver.Item2,resolver.Item3,port,round.Token)).ToList();
            try
            {
                while(tasks.Count>0)
                {
                    var done=await Task.WhenAny(tasks);tasks.Remove(done);
                    var result=await done;
                    if(result!=null){round.Cancel();return result;}
                }
            }
            catch(OperationCanceledException)when(!stop.IsCancellationRequested){}
            finally {round.Cancel();foreach(var task in tasks){try{await task;}catch(OperationCanceledException){}}}
            stop.ThrowIfCancellationRequested();
        }
        return null;
    }
    static async Task<IPAddress[]?> ResolveOne(string domain,string host,string path,IPAddress address,int port,CancellationToken stop)
        =>(await ResolveDetailed(domain,new(host,path,address),port,stop)).Addresses;
    static async Task<PolicyDnsAnswer> ResolveDetailed(string domain,PolicyResolver resolver,int port,CancellationToken stop)
    {
        try
        {
            using var client=Client(port,resolver.Address,TimeSpan.FromMilliseconds(1500));
            using var request=new HttpRequestMessage(HttpMethod.Get,"https://"+resolver.Host+resolver.Path+Uri.EscapeDataString(domain));request.Headers.Accept.ParseAdd("application/dns-json");
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,stop);response.EnsureSuccessStatusCode();
            await using var input=await response.Content.ReadAsStreamAsync(stop);using var limited=new MemoryStream();byte[] buffer=new byte[4096];int count;
            while((count=await input.ReadAsync(buffer,stop))>0){if(limited.Length+count>32768)return new(null,false);limited.Write(buffer,0,count);}
            return PolicyDns.Parse(limited.ToArray());
        }
        catch(Exception ex)when(ex is HttpRequestException or IOException or SocketException or OperationCanceledException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException){stop.ThrowIfCancellationRequested();return new(null,false);}
    }
    internal static async Task<IPAddress[]?> ResolveConfigured(string domain,int port,CancellationToken stop,Func<PolicyResolver,CancellationToken,Task<PolicyDnsAnswer>>? lookup=null)
    {
        if(PolicySafety.Domain(domain)!=domain)throw new ArgumentException("Invalid public DNS domain.");
        foreach(var resolver in PolicyDns.Resolvers)
        {
            stop.ThrowIfCancellationRequested();
            using var attempt=CancellationTokenSource.CreateLinkedTokenSource(stop);attempt.CancelAfter(1500);
            PolicyDnsAnswer answer;
            try{answer=await (lookup?.Invoke(resolver,attempt.Token)??ResolveDetailed(domain,resolver,port,attempt.Token));}
            catch(OperationCanceledException)when(!stop.IsCancellationRequested){continue;}
            stop.ThrowIfCancellationRequested();
            if(answer.Terminal)return answer.Addresses;
        }
        return null;
    }
    internal static async Task<CandidateResult> ProbeAddresses(IPAddress[] addresses,Func<IPAddress,CancellationToken,Task<CandidateResult>> probe,CancellationToken stop)
    {
        if(addresses.Length==0||addresses.Any(ip=>!PolicySafety.Public(ip)))throw new ArgumentException("Invalid public probe addresses.");
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(stop);budget.CancelAfter(3000);
        var timer=Stopwatch.StartNew();
        try
        {
            foreach(var address in addresses.Distinct().Take(4))
            {
                budget.Token.ThrowIfCancellationRequested();
                var result=await probe(address,budget.Token);
                // Match a TCP dialer's boundary: once connected, TLS/HTTP failure
                // cannot be repaired by replaying the stream on another address.
                if(result.Reachable||result.Definitive||result.ConnectionEstablished)return result;
            }
        }
        catch(OperationCanceledException)when(!stop.IsCancellationRequested){}
        stop.ThrowIfCancellationRequested();
        return new(false,false,"Public address attempts unavailable within probe budget.",timer.ElapsedMilliseconds);
    }
    internal static async Task<(RouteSelection Selection,long DnsMilliseconds)> SelectResolved(string domain,PolicyPorts ports,CancellationToken stop,bool allowUnstableFallback=false,
        Func<int,CancellationToken,Task<IPAddress[]?>>? resolve=null,Func<IPAddress,int,CancellationToken,Task<CandidateResult>>? probe=null,string? observeRoute=null,IReadOnlySet<string>? avoidRoutes=null)
    {
        if(PolicySafety.Domain(domain)!=domain)throw new ArgumentException("Invalid public policy domain.");
        using var dnsStop=CancellationTokenSource.CreateLinkedTokenSource(stop);
        async Task<(IPAddress[]? Addresses,long Milliseconds)> Lookup(int port)
        {
            var timer=Stopwatch.StartNew();IPAddress[]? addresses=null;
            try
            {
                // Match dns-direct/dns-vpn in the prepared engine. A DNS answer
                // obtained through VPN must not certify a broken direct resolver
                // or pin the direct probe to another region's CDN address.
                addresses=await (resolve?.Invoke(port,dnsStop.Token)??ResolveConfigured(domain,ports.DnsPort(port),dnsStop.Token));
                if(addresses is not {Length:>0}||addresses.Any(ip=>!PolicySafety.Public(ip)))addresses=null;
            }
            catch(OperationCanceledException)when(dnsStop.IsCancellationRequested){}
            return(addresses,timer.ElapsedMilliseconds);
        }
        var direct=Lookup(ports.Direct);var vpn=Lookup(ports.Vpn);
        RouteSelection selected;
        try
        {
            selected=await Select(async(port,token)=>
            {
                var answer=await (port==ports.Vpn?vpn:direct).WaitAsync(token);
                if(answer.Addresses==null)return new(false,false,"Configured route DNS unavailable",answer.Milliseconds);
                return await ProbeAddresses(answer.Addresses,(ip,attempt)=>probe?.Invoke(ip,port,attempt)??Probe(domain,ip,port,attempt),token);
            },ports,stop,allowUnstableFallback,observeRoute,avoidRoutes);
        }
        finally{dnsStop.Cancel();await Task.WhenAll(direct,vpn);}
        var timings=selected.Candidates.Select(c=>c.Outcome==ProbeOutcome.Unavailable&&(c.Route=="vpn"?vpn.Result:direct.Result).Addresses==null?c with{Outcome=ProbeOutcome.DnsUnavailable}:c).ToArray();
        var outcome=selected.Route==null&&timings.Any(c=>c.Outcome==ProbeOutcome.DnsUnavailable)&&timings.All(c=>c.Outcome is ProbeOutcome.DnsUnavailable or ProbeOutcome.Cancelled)?ProbeOutcome.DnsUnavailable:selected.Outcome;
        long dnsMs=selected.Route=="vpn"?vpn.Result.Milliseconds:selected.Route!=null?direct.Result.Milliseconds:Math.Max(direct.Result.Milliseconds,vpn.Result.Milliseconds);
        return(selected with{Outcome=outcome,Candidates=timings},dnsMs);
    }
    internal static bool UnstablePreferred(RouteSelection selection)=>selection.Route!=null&&selection.Candidates.TakeWhile(c=>c.Route!=selection.Route).Any(c=>c.Outcome==ProbeOutcome.Indeterminate);
    internal static async Task<RouteSelection> Select(Func<int,CancellationToken,Task<CandidateResult>> probe,PolicyPorts ports,CancellationToken stop,bool allowUnstableFallback=false,string? observeRoute=null,IReadOnlySet<string>? avoidRoutes=null)
    {
        var selectionTimer=Stopwatch.StartNew();
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(stop);
        var candidates=new[]{("direct",ports.Direct),("dpi",ports.Dpi),("vpn",ports.Vpn)};
        async Task<CandidateTiming> Check(string route,int port)
        {
            var timer=Stopwatch.StartNew();
            try
            {
                var first=await probe(port,cancel.Token);
                if(first.Definitive && !first.Reachable)return new(route,timer.ElapsedMilliseconds,ProbeOutcome.TlsAuthenticationError);
                var second=await probe(port,cancel.Token);
                var outcome=second.Definitive && !second.Reachable?ProbeOutcome.TlsAuthenticationError:
                    first.Reachable && second.Reachable?ProbeOutcome.Reachable:first.Reachable || second.Reachable?ProbeOutcome.Indeterminate:
                    first.HttpStatus==451&&second.HttpStatus==451?ProbeOutcome.HttpRestricted:ProbeOutcome.Unavailable;
                // A healthy root cannot erase two recent real stream failures
                // on this app/domain/path. Still perform TLS checks normally.
                if(outcome==ProbeOutcome.Reachable&&avoidRoutes?.Contains(route)==true)outcome=ProbeOutcome.ApplicationFailure;
                return new(route,timer.ElapsedMilliseconds,outcome);
            }
            catch(OperationCanceledException){return new(route,timer.ElapsedMilliseconds,ProbeOutcome.Cancelled);}
        }
        var tasks=candidates.Select(c=>Check(c.Item1,c.Item2)).ToArray();
        string? selected=null;ProbeOutcome outcome=ProbeOutcome.Unavailable;long elapsed=0,decisionMilliseconds=0;
        try
        {
            // Completion order never changes the direct > DPI > VPN policy.
            foreach(var task in tasks)
            {
                var result=await task;stop.ThrowIfCancellationRequested();
                if(result.Outcome==ProbeOutcome.TlsAuthenticationError){outcome=result.Outcome;break;}
                if(result.Outcome==ProbeOutcome.Indeterminate){outcome=result.Outcome;if(!allowUnstableFallback)break;continue;}
                if(result.Outcome==ProbeOutcome.Reachable){outcome=result.Outcome;selected=result.Route;elapsed=result.Milliseconds;break;}
            }
            // On a return to a preferred route, finish the current route's
            // independent checks so the stability gate can distinguish a dead
            // path from an otherwise working path. New destinations don't wait.
            int observed=Array.FindIndex(candidates,c=>c.Item1==observeRoute);
            if(selected!=null && observed>=0 && Array.FindIndex(candidates,c=>c.Item1==selected)<observed)
            {await tasks[observed];stop.ThrowIfCancellationRequested();}
            decisionMilliseconds=selectionTimer.ElapsedMilliseconds;
        }
        finally{cancel.Cancel();await Task.WhenAll(tasks);}
        if(selected==null&&outcome==ProbeOutcome.Unavailable&&tasks.Any(t=>t.Result.Outcome==ProbeOutcome.HttpRestricted))outcome=ProbeOutcome.HttpRestricted;
        if(selected==null&&outcome==ProbeOutcome.Unavailable&&tasks.Any(t=>t.Result.Outcome==ProbeOutcome.ApplicationFailure))outcome=ProbeOutcome.ApplicationFailure;
        return new(outcome,selected,elapsed,tasks.Select(t=>t.Result).ToArray())
        {DecisionMilliseconds=decisionMilliseconds,CompletionMilliseconds=selectionTimer.ElapsedMilliseconds};
    }
}
