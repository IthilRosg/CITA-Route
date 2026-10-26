using System.Diagnostics;
using System.Net;
using System.Text.Json;
using RoutePilot.Agent;

internal static class NativeCoverageLab
{
    internal static async Task<int> ServiceClient()
    {
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(10));var result=await Fetch("cloudflare-dns.com",1,budget.Token);
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(result));bool success=json.RootElement.GetProperty("headersReceived").GetBoolean();
        Console.WriteLine(JsonSerializer.Serialize(new{success,result}));return success?0:1;
    }
    static async ValueTask<Stream> Connect(System.Net.IPAddress address,int port,CancellationToken stop)
    {
        var socket=new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,System.Net.Sockets.SocketType.Stream,System.Net.Sockets.ProtocolType.Tcp);
        try{await socket.ConnectAsync(address,port,stop);return new System.Net.Sockets.NetworkStream(socket,ownsSocket:true);}catch{socket.Dispose();throw;}
    }
    static async Task<IPAddress> ResolveIndependent(string domain,CancellationToken stop)
    {
        using var handler=new SocketsHttpHandler{UseProxy=false,UseCookies=false,AllowAutoRedirect=false,ConnectCallback=(_,ct)=>Connect(IPAddress.Parse("1.1.1.1"),443,ct)};
        using var client=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://cloudflare-dns.com/dns-query?name="+Uri.EscapeDataString(domain)+"&type=A");request.Headers.Accept.ParseAdd("application/dns-json");
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,stop);response.EnsureSuccessStatusCode();
        using var stream=await response.Content.ReadAsStreamAsync(stop);using var body=new MemoryStream();byte[] buffer=new byte[2048];int read;
        while((read=await stream.ReadAsync(buffer,stop))>0){if(body.Length+read>16384)throw new IOException("Resolver answer exceeded limit.");body.Write(buffer,0,read);}
        using var json=JsonDocument.Parse(body.ToArray());if(!json.RootElement.TryGetProperty("Answer",out var answers))throw new IOException("No public A answer.");
        foreach(var answer in answers.EnumerateArray())if(answer.GetProperty("type").GetInt32()==1&&IPAddress.TryParse(answer.GetProperty("data").GetString(),out var ip)&&ip.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork)
        {var b=ip.GetAddressBytes();if(b[0]==0||b[0]==10||b[0]==127||b[0]>=224||(b[0]==169&&b[1]==254)||(b[0]==172&&b[1]>=16&&b[1]<=31)||(b[0]==192&&b[1]==168)||(b[0]==100&&b[1]>=64&&b[1]<=127)||(b[0]==198&&b[1]>=18&&b[1]<=19))continue;return ip;}
        throw new IOException("No public A answer.");
    }
    static readonly string[] Domains=["example.com","cloudflare-dns.com","www.deepl.com","www.ryuugames.com","srnky.com","windows64.net","telegram.org","github.com","api.github.com","learn.microsoft.com","www.wikipedia.org","upload.wikimedia.org","cdn.jsdelivr.net","www.mozilla.org","www.python.org","www.kernel.org"];
    static async Task<object> Fetch(string domain,int round,CancellationToken stop)
    {
        var clock=Stopwatch.StartNew();int? status=null;long bytes=0;string outcome="Unknown";bool headers=false;var causes=new List<string>();
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(stop);budget.CancelAfter(TimeSpan.FromSeconds(8));
        using var handler=new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false,ConnectTimeout=TimeSpan.FromSeconds(4),AutomaticDecompression=DecompressionMethods.None};
        using var client=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
        try
        {
            if(round==2){var address=await ResolveIndependent(domain,budget.Token);handler.ConnectCallback=(_,ct)=>Connect(address,443,ct);}
            using var request=new HttpRequestMessage(HttpMethod.Get,"https://"+domain+"/");request.Headers.UserAgent.ParseAdd("CITA-Route-Lab/0.6");
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,budget.Token);status=(int)response.StatusCode;headers=true;
            using var stream=await response.Content.ReadAsStreamAsync(budget.Token);byte[] buffer=new byte[8192];bool eof=false;
            while(bytes<65536){int read=await stream.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,65536-bytes)),budget.Token);if(read==0){eof=true;break;}bytes+=read;}
            outcome=status>=300&&status<400?"RedirectNotFollowed":status>=400?"HttpErrorResponse":eof||response.Content.Headers.ContentLength==bytes?"BodyComplete":"BodySampleCapped";
        }
        catch(OperationCanceledException)when(!stop.IsCancellationRequested){outcome=headers?"BodyTimeout":"RequestTimeout";}
        catch(HttpRequestException e){outcome=e.HttpRequestError.ToString();for(Exception? cause=e;cause!=null&&causes.Count<5;cause=cause.InnerException)causes.Add(cause is System.Net.Sockets.SocketException socket?"Socket:"+socket.SocketErrorCode:cause.GetType().Name+":"+cause.HResult);}
        catch(IOException){outcome=headers?"BodyReadFailure":"ConnectionFailure";}
        return new{domain,round,resolver=round==0?"LiteralFakeIP":round==1?"Windows":"BootstrappedDoH-IPv4",status,headersReceived=headers,bodyBytes=bytes,outcome,causes,elapsedMs=clock.ElapsedMilliseconds};
    }
    internal static async Task<object> Measure(NativeGuardedSession runtime,string root,CancellationToken stop)
    {
        var results=new List<object>();int originalPid=runtime.Core.ProcessId;int controls=0,maxObservations=0,maxDirectPermits=0;
        for(int round=1;round<=2;round++)
        {
            bool mixed=false;
            // A small allowlist, four anonymous requests at once, 64 KiB/body.
            // No redirects, cookies, auth, POSTs, recursive discovery or port scans.
            foreach(var batch in Domains.Chunk(4))
            {
                var requests=batch.Select(domain=>Fetch(domain,round,stop)).ToArray();
                if(!mixed){var control=CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,domain:true);await Task.WhenAll(requests.Cast<Task>().Append(control));if(!(await control).GetProperty("success").GetBoolean())throw new IOException("Concurrent HTTPS/UDP control failed.");controls++;mixed=true;}
                results.AddRange(await Task.WhenAll(requests));
                maxObservations=Math.Max(maxObservations,runtime.Core.Observations.Length);maxDirectPermits=Math.Max(maxDirectPermits,runtime.Guard.DirectPermits);
                if(runtime.Core.ProcessId!=originalPid||runtime.Core.State!="Active"||runtime.Guard.DirectPermits>256)throw new IOException("Coverage core/guard epoch changed.");
            }
        }
        var retrySamples=new List<object>();using(var observed=JsonDocument.Parse(JsonSerializer.Serialize(results)))
            foreach(var failed in observed.RootElement.EnumerateArray().Where(r=>r.GetProperty("round").GetInt32()==1&&!r.GetProperty("headersReceived").GetBoolean()).Take(1))retrySamples.Add(await Fetch(failed.GetProperty("domain").GetString()!,1,stop));
        var missingName=await Fetch("198.18.0.62",0,stop);var missingDns=await Fetch("cita-route-negative.invalid",1,stop);
        using(var negative=JsonDocument.Parse(JsonSerializer.Serialize(new[]{missingName,missingDns})))if(negative.RootElement.EnumerateArray().Any(r=>r.GetProperty("headersReceived").GetBoolean()))throw new IOException("Unresolved DNS or unknown fake target connected.");
        var afterFailures=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,domain:true);if(!afterFailures.GetProperty("success").GetBoolean()||runtime.Core.ProcessId!=originalPid)throw new IOException("DNS failure disturbed the active core.");
        for(int i=0;i<20&&(runtime.Core.FakeIpVpnDials==0||runtime.Core.FakeIpRefused==0);i++)await Task.Delay(100,stop);
        bool retained=await RetainFakeTcp(runtime,stop);
        using var state=JsonDocument.Parse(JsonSerializer.Serialize(runtime.Snapshot));
        if(runtime.Core.NativeDnsProtocol!=1)throw new IOException("Native DNS capability missing.");
        if(runtime.Core.FakeIpVpnDials==0||runtime.Core.FakeIpRefused==0)throw new IOException("Fake-IP recovery/refusal not reproduced.");
        return new{completed=true,domainCount=Domains.Length,requestCount=results.Count,concurrentHttpsUdpControls=controls,sameCore=true,
            negativeCases=new{missingName,missingDns},controlAfterDnsFailure=true,
            retrySamples,
            fakeTcpRetainedAcrossPolicy=retained,
            nativeDnsProtocol=runtime.Core.NativeDnsProtocol,fakeIpVpnDials=runtime.Core.FakeIpVpnDials,fakeIpRefused=runtime.Core.FakeIpRefused,
            maxConcurrentMatrixRequests=4,bodyLimitBytes=65536,requestTimeoutSeconds=8,policyBudgetPerMinute=32,maxObservations,maxDirectPermits,peakJobCommittedBytes=runtime.ResourceUsage.PeakCommittedBytes,
            successfulCalibrationChecks=state.RootElement.GetProperty("calibrationChecks").GetInt32(),calibratedDomains=state.RootElement.GetProperty("calibratedDomains").GetInt32(),
            scope="Anonymous root GETs through active VM TUN: Windows DNS then bootstrapped authenticated DoH IPv4. TLS validation retained, no system DNS changes. HTTP response/capped sample is not full site/app availability; no per-request route attribution or QUIC proof.",results};
    }
    static async Task<bool> RetainFakeTcp(NativeGuardedSession runtime,CancellationToken stop)
    {
        int pid=runtime.Core.ProcessId;long before=runtime.Core.FakeIpVpnDials;
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(stop);budget.CancelAfter(TimeSpan.FromSeconds(12));
        using var handler=new SocketsHttpHandler{UseProxy=false,UseCookies=false,AllowAutoRedirect=false,MaxConnectionsPerServer=1,ConnectCallback=(_,ct)=>Connect(IPAddress.Parse("198.18.0.62"),443,ct)};
        using var client=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
        async Task ReadTrace()
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,"https://cloudflare-dns.com/cdn-cgi/trace"){Version=HttpVersion.Version11,VersionPolicy=HttpVersionPolicy.RequestVersionExact};
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,budget.Token);if(response.StatusCode!=HttpStatusCode.OK)throw new IOException("Held fake-IP control HTTP failed.");
            using var stream=await response.Content.ReadAsStreamAsync(budget.Token);byte[] bytes=new byte[1024];int total=0,read;while((read=await stream.ReadAsync(bytes,budget.Token))>0){total+=read;if(total>8192)throw new IOException("Held control exceeded body limit.");}
        }
        await ReadTrace();for(int i=0;i<20&&runtime.Core.FakeIpVpnDials<=before;i++)await Task.Delay(100,budget.Token);
        long first=runtime.Core.FakeIpVpnDials;if(first!=before+1)throw new IOException("Held control did not use one VPN-resolved fake-IP connection.");
        await runtime.PublishPolicyAsync(new NativePolicyRequest(Guid.NewGuid().ToString("N"),[]),budget.Token);
        await ReadTrace();await Task.Delay(700,budget.Token);
        if(runtime.Core.ProcessId!=pid||runtime.Core.FakeIpVpnDials!=first)throw new IOException("Held fake-IP TCP reconnected across policy publication.");
        return true;
    }
}
