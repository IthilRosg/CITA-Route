using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace RoutePilot.Agent;

public enum ProbeOutcome { Reachable, Unavailable, Indeterminate, DnsUnavailable, TlsAuthenticationError, Cancelled }
public sealed record CandidateTiming(string Route,long Milliseconds,ProbeOutcome Outcome);
public sealed record RouteSelection(ProbeOutcome Outcome,string? Route,long Milliseconds,CandidateTiming[] Candidates);
internal sealed record CalibrationResult(ProbeOutcome Outcome,PolicyDecision? Decision,long DnsMilliseconds,long TotalMilliseconds,CandidateTiming[] Candidates);
public sealed class PolicyPorts
{
    public int Api{get;set;}=20810;
    public int Direct{get;set;}=20829;
    public int Dpi{get;set;}=20828;
    public int Vpn{get;set;}=20808;
    public int Ack{get;set;}=20830;
    public int Proof{get;set;}=20831;
    internal static PolicyPorts Read(string root)=>File.Exists(Path.Combine(root,"runtime","policy-ports.json"))?Storage.Read<PolicyPorts>(Path.Combine(root,"runtime","policy-ports.json")):new();
}

internal static class PolicyTransport
{
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
    static HttpClient Client(int port,IPAddress address,TimeSpan timeout,Action<SslPolicyErrors>? certificateResult=null)
    {
        var handler=new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,ConnectTimeout=timeout,
            ConnectCallback=(_,stop)=>new ValueTask<Stream>(Socks(port,address.ToString(),443,stop))};
        if(certificateResult!=null)handler.SslOptions.RemoteCertificateValidationCallback=(_,_,_,errors)=>{certificateResult(errors);return errors==SslPolicyErrors.None;};
        return new HttpClient(handler){Timeout=timeout};
    }
    internal static async Task<CandidateResult> Probe(string domain,IPAddress address,int port,CancellationToken stop)
    {
        if(PolicySafety.Domain(domain)!=domain || !PolicySafety.Public(address))throw new ArgumentException("Invalid public policy probe.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop);timeout.CancelAfter(TimeSpan.FromSeconds(3));
        int rejectedCertificate=0;
        using var client=Client(port,address,TimeSpan.FromSeconds(3),errors=>{if(errors!=SslPolicyErrors.None)Interlocked.Exchange(ref rejectedCertificate,1);});
        using var request=new HttpRequestMessage(HttpMethod.Head,"https://"+domain+"/"){Version=HttpVersion.Version11,VersionPolicy=HttpVersionPolicy.RequestVersionExact};
        var timer=Stopwatch.StartNew();
        try
        {using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);return new(true,true,"TLS established; HTTP "+(int)response.StatusCode,timer.ElapsedMilliseconds);}
        catch(HttpRequestException)when(Volatile.Read(ref rejectedCertificate)!=0)
        {return new(false,true,"TLS authentication failed.",timer.ElapsedMilliseconds);}
        catch(Exception ex)when(ex is HttpRequestException or IOException or OperationCanceledException or SocketException)
        {stop.ThrowIfCancellationRequested();return new(false,false,ex is HttpRequestException http?http.HttpRequestError+": "+http.Message:ex.GetType().Name,timer.ElapsedMilliseconds);}
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
            finally {round.Cancel();foreach(var task in tasks){try{await task;}catch(OperationCanceledException){}}}
            stop.ThrowIfCancellationRequested();
        }
        return null;
    }
    static async Task<IPAddress[]?> ResolveOne(string domain,string host,string path,IPAddress address,int port,CancellationToken stop)
    {
        try
        {
            using var client=Client(port,address,TimeSpan.FromMilliseconds(1500));
            using var request=new HttpRequestMessage(HttpMethod.Get,"https://"+host+path+Uri.EscapeDataString(domain));request.Headers.Accept.ParseAdd("application/dns-json");
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,stop);response.EnsureSuccessStatusCode();
            await using var input=await response.Content.ReadAsStreamAsync(stop);using var limited=new MemoryStream();byte[] buffer=new byte[4096];int count;
            while((count=await input.ReadAsync(buffer,stop))>0){if(limited.Length+count>32768)return null;limited.Write(buffer,0,count);}
            using var json=JsonDocument.Parse(limited.ToArray());
            if(json.RootElement.GetProperty("Status").GetInt32()!=0 || !json.RootElement.TryGetProperty("Answer",out var answers))return null;
            var ips=answers.EnumerateArray().Where(a=>a.GetProperty("type").GetInt32()==1).Select(a=>IPAddress.Parse(a.GetProperty("data").GetString()!)).Distinct().ToArray();
            return ips.Length>0 && ips.All(PolicySafety.Public)?ips:null;
        }
        catch(Exception ex)when(ex is HttpRequestException or IOException or SocketException or OperationCanceledException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException){return null;}
    }
    internal static async Task<RouteSelection> Select(Func<int,CancellationToken,Task<CandidateResult>> probe,PolicyPorts ports,CancellationToken stop)
    {
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
                return new(route,timer.ElapsedMilliseconds,second.Definitive && !second.Reachable?ProbeOutcome.TlsAuthenticationError:
                    first.Reachable && second.Reachable?ProbeOutcome.Reachable:first.Reachable || second.Reachable?ProbeOutcome.Indeterminate:ProbeOutcome.Unavailable);
            }
            catch(OperationCanceledException){return new(route,timer.ElapsedMilliseconds,ProbeOutcome.Cancelled);}
        }
        var tasks=candidates.Select(c=>Check(c.Item1,c.Item2)).ToArray();
        string? selected=null;ProbeOutcome outcome=ProbeOutcome.Unavailable;long elapsed=0;
        try
        {
            // Completion order never changes the direct > DPI > VPN policy.
            foreach(var task in tasks)
            {
                var result=await task;stop.ThrowIfCancellationRequested();
                if(result.Outcome is ProbeOutcome.TlsAuthenticationError or ProbeOutcome.Indeterminate){outcome=result.Outcome;break;}
                if(result.Outcome==ProbeOutcome.Reachable){outcome=result.Outcome;selected=result.Route;elapsed=result.Milliseconds;break;}
            }
        }
        finally{cancel.Cancel();await Task.WhenAll(tasks);}
        return new(outcome,selected,elapsed,tasks.Select(t=>t.Result).ToArray());
    }
}
