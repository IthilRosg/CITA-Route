using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace RoutePilot.Agent;

internal static class ControlTraceTests
{
    sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken stop)=>send(request,stop);
    }
    static void Check(bool value,string reason){if(!value)throw new Exception(reason);}
    static readonly string[] Urls={"https://www.cloudflare.com/cdn-cgi/trace","https://api.ipify.org"};
    internal static async Task Run()
    {
        var attempts=new List<ControlAttempt>();int calls=0;
        using(var client=new HttpClient(new Handler((_,_)=>Task.FromResult(++calls==1?new HttpResponseMessage(HttpStatusCode.Forbidden):new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("8.8.8.8")}))))
        {
            var value=await ControlTrace.Run(client,Urls,default,attempts.Add);
            Check(value.Ip.Equals(IPAddress.Parse("8.8.8.8"))&&calls==2,"HTTP failure must still use the existing second control.");
            Check(attempts[0] is {Provider:"Cloudflare",Stage:"HTTP",HttpStatus:403,Outcome:"Failed"}&&attempts[1] is {Provider:"Ipify",Outcome:"Succeeded"},"Per-control HTTP status and success were lost.");
        }
        attempts.Clear();calls=0;
        using(var client=new HttpClient(new Handler((_,_)=>Task.FromException<HttpResponseMessage>(++calls==1?
            new HttpRequestException(HttpRequestError.NameResolutionError,"secret.example/?token=secret",new SocketException((int)SocketError.HostNotFound)):
            new HttpRequestException(HttpRequestError.SecureConnectionError,"private certificate context")))))
        {
            bool failed=false;try{await ControlTrace.Run(client,Urls,default,attempts.Add);}catch(IOException){failed=true;}
            Check(failed&&attempts.Count==2&&attempts[0].Stage=="DNS"&&attempts[1].Stage=="TLS","DNS and TLS failures were collapsed or control order changed.");
            string json=JsonSerializer.Serialize(attempts);Check(!json.Contains("secret")&&!json.Contains("private")&&!json.Contains("https://"),"Diagnostics retained an exception message or URL.");
        }
        attempts.Clear();calls=0;
        using(var client=new HttpClient(new Handler(async(_,stop)=>{if(++calls==1)await Task.Delay(Timeout.Infinite,stop);return new(HttpStatusCode.OK){Content=new StringContent("8.8.8.8")};})))
        {
            await ControlTrace.Run(client,Urls,default,attempts.Add,1000,30);
            Check(attempts[0] is {Outcome:"Timeout",Deadline:"Attempt",Stage:"Request"}&&attempts[1].Outcome=="Succeeded","Unknown timeout stage must stay unknown while allowing bounded fallback.");
        }
        foreach(bool caller in new[]{false,true})
        {
            attempts.Clear();calls=0;using var cancellation=new CancellationTokenSource();
            using var client=new HttpClient(new Handler(async(_,stop)=>{calls++;if(caller)cancellation.Cancel();await Task.Delay(Timeout.Infinite,stop);throw new Exception();}));
            bool cancelled=false,failed=false;
            try{await ControlTrace.Run(client,Urls,cancellation.Token,attempts.Add,30,1000);}catch(OperationCanceledException){cancelled=true;}catch(IOException){failed=true;}
            Check(calls==1&&attempts.Count==1&&(caller?cancelled&&!failed:failed&&!cancelled),"Caller cancellation and overall deadline must remain distinct.");
            Check(attempts[0].Deadline==(caller?"Caller":"Overall"),"Wrong deadline scope.");
        }
        attempts.Clear();calls=0;
        using(var client=new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(++calls==1?new string('x',8193):"8.8.8.8")}))))
        {
            await ControlTrace.Run(client,Urls,default,attempts.Add);
            Check(attempts[0] is {Stage:"Body",Error:"InvalidResponse"}&&calls==2,"Oversized replies must stay bounded and cannot certify an exit.");
        }
    }
}
