using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RoutePilot.Agent;

// No URL, address, response body or exception message belongs in this record.
public sealed record ControlAttempt(string Provider,string Stage,string Outcome,long Milliseconds,
    string Deadline,int? HttpStatus,int? SocketError,string Error);

internal static class ControlTrace
{
    internal static string Provider(string url)=>url switch
    {
        "https://www.cloudflare.com/cdn-cgi/trace"=>"Cloudflare",
        "https://api.ipify.org"=>"Ipify",_=>"Custom"
    };
    internal static ControlAttempt Failure(string provider,string phase,Exception error,long milliseconds,string deadline,int? status=null)
    {
        var http=error as HttpRequestException;
        SocketException? socket=null;
        for(Exception? current=error;current!=null;current=current.InnerException)
            if(current is SocketException found){socket=found;break;}
        string stage=http?.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError=>"DNS",HttpRequestError.ConnectionError=>"TCP",
            HttpRequestError.SecureConnectionError=>"TLS",HttpRequestError.ProxyTunnelError=>"Proxy",
            HttpRequestError.HttpProtocolError or HttpRequestError.InvalidResponse=>"HTTP",_=>phase
        };
        return new(provider,stage,deadline=="Caller"?"Cancelled":deadline is "Attempt" or "Overall"||error is OperationCanceledException?"Timeout":"Failed",
            Math.Clamp(milliseconds,0,60000),deadline,status??(int?)http?.StatusCode,
            socket==null?null:(int)socket.SocketErrorCode,
            http!=null?http.HttpRequestError.ToString():error is OperationCanceledException?"Cancelled":error is InvalidDataException?"InvalidResponse":error is IOException?"IO":"Unexpected");
    }
    internal static async Task<(IPAddress Ip,long Milliseconds)> Run(HttpClient client,string[] urls,CancellationToken cancellation,
        Action<ControlAttempt>? observe=null,int totalMilliseconds=8000,int attemptMilliseconds=4000)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(totalMilliseconds);
        var total=Stopwatch.StartNew();Exception? failure=null;
        foreach(string url in urls)
        {
            cancellation.ThrowIfCancellationRequested();
            using var attempt=CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);attempt.CancelAfter(attemptMilliseconds);
            var timer=Stopwatch.StartNew();string phase="Request";int? status=null;
            try
            {
                using var response=await client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,attempt.Token);
                status=(int)response.StatusCode;phase="HTTP";response.EnsureSuccessStatusCode();phase="Body";
                using var stream=await response.Content.ReadAsStreamAsync(attempt.Token);byte[] bytes=new byte[8193];int used=0,read;
                while(used<bytes.Length&&(read=await stream.ReadAsync(bytes.AsMemory(used),attempt.Token))>0)used+=read;
                if(used==bytes.Length)throw new InvalidDataException("Unexpected trace response size.");
                string text=System.Text.Encoding.UTF8.GetString(bytes,0,used);phase="Response";
                string? value=url.Contains("ipify",StringComparison.Ordinal)?text.Trim():text.Split('\n').FirstOrDefault(line=>line.StartsWith("ip=",StringComparison.Ordinal))?[3..].Trim();
                if(!IPAddress.TryParse(value,out var ip)||ip.AddressFamily!=AddressFamily.InterNetwork)throw new InvalidDataException("Trace did not return a comparable IPv4 source.");
                observe?.Invoke(new(Provider(url),"Complete","Succeeded",timer.ElapsedMilliseconds,"None",status,null,"None"));
                return(ip,total.ElapsedMilliseconds);
            }
            catch(Exception ex)when(ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
            {
                failure=ex;
                observe?.Invoke(Failure(Provider(url),phase,ex,timer.ElapsedMilliseconds,
                    cancellation.IsCancellationRequested?"Caller":timeout.IsCancellationRequested?"Overall":attempt.IsCancellationRequested?"Attempt":"None",status));
                cancellation.ThrowIfCancellationRequested();
                if(timeout.IsCancellationRequested)break;
            }
        }
        throw new IOException("The control HTTPS probes failed.",failure);
    }
}
