using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace RoutePilot.Agent;

// Explicit support diagnostic, never an automatic replay of a browser request.
internal static class PolicyResourceCheck
{
    internal const int ByteLimit=512*1024;
    internal static Uri Target(string text)
    {
        if(!Uri.TryCreate(text,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Port!=443||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!=""||PolicySafety.Domain(uri.IdnHost)==null)
            throw new ArgumentException("Use a public HTTPS resource URL on port 443 without credentials, query or fragment.");
        return uri;
    }
    internal static async Task Run(string root,string text)
    {
        var target=Target(text);string domain=PolicySafety.Domain(target.IdnHost)!;
        var session=Storage.Read<PolicySession>(Path.Combine(root,"runtime/session.json"));
        if(!session.Running||session.BridgeGeneration<=0)throw new IOException("An active ordinary session with a known bridge generation is required.");
        var ports=PolicyPorts.Read(root);using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(16));
        async Task<IPAddress[]?> Resolve(int port)
        {
            var addresses=await PolicyTransport.ResolveConfigured(domain,ports.DnsPort(port),stop.Token);
            return addresses is {Length:>0}&&addresses.All(PolicySafety.Public)?addresses:null;
        }
        var direct=Resolve(ports.Direct);var vpn=Resolve(ports.Vpn);
        async Task<object> Check(string route,int port)
        {
            var timer=System.Diagnostics.Stopwatch.StartNew();int? status=null;long bytes=0;long? length=null;bool eof=false,capped=false;string? error=null;
            try
            {
                var addresses=await (port==ports.Vpn?vpn:direct);
                if(addresses==null)return new{route,state="DnsUnavailable",bytes=0};
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using var client=PolicyTransport.Client(port,addresses[0],TimeSpan.FromSeconds(8));
                using var request=new HttpRequestMessage(HttpMethod.Get,target){Version=HttpVersion.Version11,VersionPolicy=HttpVersionPolicy.RequestVersionExact};
                using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                status=(int)response.StatusCode;length=response.Content.Headers.ContentLength;
                await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);byte[] buffer=new byte[8192];
                while(bytes<ByteLimit)
                {
                    int read=await stream.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,ByteLimit-bytes)),timeout.Token);
                    if(read==0){eof=true;break;}bytes+=read;
                }
                capped=bytes==ByteLimit;
            }
            catch(Exception ex)when(ex is HttpRequestException or IOException or OperationCanceledException or System.Net.Sockets.SocketException){error=ex.GetType().Name;}
            bool complete=error==null&&eof&&(!length.HasValue||length==bytes);
            return new{route,status,bytes,expectedBytes=length,complete,capped,error,milliseconds=timer.ElapsedMilliseconds,
                state=error!=null?bytes>0?"PartialTransfer":"TransportFailure":complete?"BodyComplete":capped?"SampleLimit":"IncompleteBody"};
        }
        var attempts=await Task.WhenAll(Check("direct",ports.Direct),Check("dpi",ports.Dpi),Check("vpn",ports.Vpn));
        var after=Storage.Read<PolicySession>(Path.Combine(root,"runtime/session.json"));
        if(!after.Running||after.Id!=session.Id||after.BridgeGeneration!=session.BridgeGeneration)throw new IOException("Routing generation changed; resource observation discarded.");
        var report=new{success=true,domain,pathHash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.AbsolutePath)))[..12],sessionId=session.Id,bridgeGeneration=session.BridgeGeneration,finishedUtc=DateTime.UtcNow,attempts,
            scope="Own explicitly requested anonymous HTTPS GET; first public IPv4 per route; strict original-name TLS; no redirects/cookies/user request replay; at most 512 KiB per path; no content stored or routing rules applied"};
        Storage.Write(Path.Combine(root,"logs/resource-check-last.json"),report);Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report,Storage.Json));
    }
}
