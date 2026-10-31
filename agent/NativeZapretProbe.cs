using System.Net;
using System.Net.Security;
using System.Security.Authentication;

namespace RoutePilot.Agent;

// Fixed anonymous connectivity probe. No redirects, user paths, cookies,
// credentials or application requests are copied to an alternative path.
internal static class NativeZapretProbe
{
    internal static async Task<NativePathProbeResult> RunAsync(string domain,
        Func<CancellationToken,Task<INativeConnectorConnection>> connect,CancellationToken stop,TimeSpan? testBudget=null)
    {
        if(domain==null||domain.Length is <1 or >253||domain.Any(c=>c>127)||
            !Uri.TryCreate("https://"+domain+"/",UriKind.Absolute,out var target)||target.HostNameType!=UriHostNameType.Dns||
            !string.Equals(target.IdnHost,domain.TrimEnd('.'),StringComparison.OrdinalIgnoreCase)||target.AbsolutePath!="/"||
            target.Query.Length!=0||target.Fragment.Length!=0||target.UserInfo.Length!=0||target.Port!=443)
            throw new InvalidDataException("Zapret probe requires an exact DNS name.");
        stop.ThrowIfCancellationRequested();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(testBudget??TimeSpan.FromSeconds(7));
        INativeConnectorConnection? connection=null;int attempts=0,connected=0,rejectedCertificate=0;bool headers=false;
        using var handler=new SocketsHttpHandler
        {
            UseProxy=false,UseCookies=false,AllowAutoRedirect=false,AutomaticDecompression=DecompressionMethods.None,
            MaxResponseHeadersLength=16,MaxConnectionsPerServer=1,
            SslOptions=new SslClientAuthenticationOptions{EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,
                RemoteCertificateValidationCallback=(_,_,_,errors)=>{if(errors!=SslPolicyErrors.None)Interlocked.Exchange(ref rejectedCertificate,1);return errors==SslPolicyErrors.None;}},
            ConnectCallback=async(_,token)=>
            {
                if(Interlocked.Increment(ref attempts)!=1)throw new IOException("Zapret connectivity probe retry budget exceeded.");
                connection=await connect(token);Interlocked.Exchange(ref connected,1);return connection.Stream;
            }
        };
        using var client=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,target)
            {Version=HttpVersion.Version11,VersionPolicy=HttpVersionPolicy.RequestVersionExact};
            request.Headers.UserAgent.ParseAdd("CITA-Route-Connectivity/1");request.Headers.ConnectionClose=true;
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
            headers=true;
            return await InspectAsync(response,deadline.Token);
        }
        catch(OperationCanceledException)when(!stop.IsCancellationRequested){return new(false,TimeoutReason(Volatile.Read(ref connected)!=0,headers));}
        catch(HttpRequestException error){stop.ThrowIfCancellationRequested();return new(false,FailureReason(error.HttpRequestError,Volatile.Read(ref connected)!=0,headers,Volatile.Read(ref rejectedCertificate)!=0));}
        catch(IOException)when(headers){stop.ThrowIfCancellationRequested();return new(false,"zapret-http-body-failed");}
        finally
        {
            // Confirmed socket/worker retirement is part of probe success;
            // an unconfirmed cleanup propagates instead of publishing a rule.
            client.Dispose();if(connection!=null)await connection.DisposeAsync();
        }
    }
    internal static string TimeoutReason(bool connected,bool headers)=>headers?"zapret-http-body-timeout":connected?"zapret-tls-or-headers-timeout":"zapret-connect-timeout";
    internal static string FailureReason(HttpRequestError error,bool connected,bool headers,bool rejectedCertificate)
        =>rejectedCertificate?"zapret-tls-certificate":headers?"zapret-http-body-failed":!connected?"zapret-connect-failed":error==HttpRequestError.SecureConnectionError?"zapret-tls-failed":"zapret-http-headers-failed";
    internal static async Task<NativePathProbeResult> InspectAsync(HttpResponseMessage response,CancellationToken stop)
    {
        if((int)response.StatusCode is >=300 and <400)return new(false,"http-redirect");
        if((int)response.StatusCode is <200 or >=300)return new(false,"http-refused");
        using var body=await response.Content.ReadAsStreamAsync(stop);byte[] sample=new byte[1024];int used=0;
        while(used<sample.Length){int count=await body.ReadAsync(sample.AsMemory(used),stop);if(count==0)break;used+=count;}
        return used>0?new(true,"reachable"):new(false,"http-body-empty");
    }
}
