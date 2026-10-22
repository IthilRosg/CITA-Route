using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace RoutePilot.Agent;

// Owned loopback fixtures only. No certificate is installed or trusted and no
// production callback is replaced. Positive HTTPS/WireSock is a separate test.
internal static class PolicyResilienceLab
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    static int FreePort(){var l=new TcpListener(IPAddress.Loopback,0);l.Start();int p=((IPEndPoint)l.LocalEndpoint).Port;l.Stop();return p;}
    internal static async Task Run(string projectRoot)
    {
        string root=Path.Combine(Path.GetFullPath(projectRoot),"runtime","resilience-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(110));
        var cases=new List<object>();var resources=new List<object>();
        using var rsa=RSA.Create(2048);var certRequest=new CertificateRequest("CN=slow.fixture.invalid",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var san=new SubjectAlternativeNameBuilder();san.AddDnsName("slow.fixture.invalid");certRequest.CertificateExtensions.Add(san.Build());
        using var generated=certRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddMinutes(5));
        using var certificate=X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.MachineKeySet);
        foreach(string scenario in new[]{"slow-socks","slow-tls","silent-tls","tls-eof","stop"})
        {
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
            using var serverStop=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);int requests=0;string? sni=null;
            var server=Task.Run(async()=>
            {
                try
                {
                    using var client=await listener.AcceptTcpClientAsync(serverStop.Token);await using var stream=client.GetStream();
                    byte[] hello=new byte[3],command=new byte[10];await stream.ReadExactlyAsync(hello,serverStop.Token);await stream.WriteAsync(new byte[]{5,0},serverStop.Token);
                    await stream.ReadExactlyAsync(command,serverStop.Token);Interlocked.Increment(ref requests);
                    Assert(command[3]==1&&command.AsSpan(4,4).SequenceEqual(new byte[]{1,1,1,1}),"The slow fixture lost public-IP pinning.");
                    if(scenario=="slow-socks")await Task.Delay(1200,serverStop.Token);
                    await stream.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,0},serverStop.Token);
                    if(scenario is "silent-tls" or "stop"){await Task.Delay(Timeout.Infinite,serverStop.Token);return;}
                    if(scenario=="tls-eof")return;
                    if(scenario=="slow-tls")await Task.Delay(1200,serverStop.Token);
                    await using var tls=new SslStream(stream,true);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificateSelectionCallback=(_,name)=>{sni=name;return certificate;}},serverStop.Token);
                }
                catch(Exception ex)when(ex is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException){}
            });
            var clock=Stopwatch.StartNew();int addressCalls=0;bool cancelled=false;CandidateResult? result=null;
            using var stop=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);if(scenario=="stop")stop.CancelAfter(100);
            try
            {
                // ProbeAddresses owns its ordinary 3s round. Test the production
                // probe directly here so the round deadline cannot hide the
                // post-connect evidence while validating its own TLS timeout.
                result=await PolicyTransport.Probe("slow.fixture.invalid",IPAddress.Parse("1.1.1.1"),port,stop.Token);
                if(scenario=="stop")throw new IOException("Stop returned a network result instead of cancellation.");
                Assert(!result.Reachable&&result.ConnectionEstablished,"A post-connect failure must retain TCP evidence and cannot certify availability.");
                if(scenario is "slow-socks" or "slow-tls")Assert(result.Definitive&&sni=="slow.fixture.invalid"&&clock.ElapsedMilliseconds>=1000,"A delayed TLS exchange must still authenticate the original name and reject the untrusted certificate.");
                if(scenario=="silent-tls")Assert(!result.Definitive&&clock.ElapsedMilliseconds>=2500&&clock.ElapsedMilliseconds<4500,"A silent TLS peer must terminate within the existing total budget without invented certificate evidence.");
                var preserved=result;
                await PolicyTransport.ProbeAddresses(new[]{IPAddress.Parse("1.1.1.1"),IPAddress.Parse("8.8.8.8")},(_,_)=>{addressCalls++;return Task.FromResult(preserved);},budget.Token);
                Assert(addressCalls==1,"A slow or failed established connection must not send a second-address request.");
            }
            catch(OperationCanceledException)when(scenario=="stop"&&!budget.IsCancellationRequested){cancelled=true;}
            finally{serverStop.Cancel();listener.Stop();await server;}
            Assert(requests==1,"The fixture must observe exactly one pinned SOCKS destination request.");
            cases.Add(new{scenario,milliseconds=clock.ElapsedMilliseconds,cancelled,connectionEstablished=result?.ConnectionEstablished,certificateRefused=result?.Definitive,secondAddressAttempts=Math.Max(0,addressCalls-1)});
        }
        var app=new TcpListener(IPAddress.Loopback,0);app.Start();int destinationPort=((IPEndPoint)app.LocalEndpoint).Port;
        var peers=new List<Task>();int received=0;
        async Task Serve()
        {
            try{while(!budget.IsCancellationRequested)
            {
                var client=await app.AcceptTcpClientAsync(budget.Token);peers.RemoveAll(t=>t.IsCompleted);
                peers.Add(Task.Run(async()=>
                {
                    using(client)try
                    {
                        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);deadline.CancelAfter(3000);
                        byte[] bytes=new byte[2048];int size=0;
                        while(size<bytes.Length){int read=await client.GetStream().ReadAsync(bytes.AsMemory(size),deadline.Token);if(read==0)return;size+=read;if(Encoding.ASCII.GetString(bytes,0,size).Contains("\r\n\r\n"))break;}
                        string text=Encoding.ASCII.GetString(bytes,0,size);Interlocked.Increment(ref received);
                        // Deterministic application refusals are not claimed as
                        // measured packet loss. UDP loss is tested separately.
                        if(text.Contains("X-Fixture-Drop: yes"))return;
                        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),deadline.Token);
                    }catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException){}
                }));
            }}catch(Exception ex)when(ex is SocketException or OperationCanceledException or ObjectDisposedException){}
        }
        var serving=Serve();
        try
        {
            foreach(bool fast in new[]{false,true})
            {
                int port=FreePort();string config=Path.Combine(root,fast?"fast.json":"normal.json");
                var data=System.Text.Json.JsonSerializer.SerializeToNode(new{log=new{level="warn"},inbounds=new[]{new{type="mixed",tag="direct-probe",listen="127.0.0.1",listen_port=port}},outbounds=new[]{new{type="direct",tag="direct"},new{type="direct",tag="dpi"}},route=new{rules=new[]{new{inbound=new[]{"direct-probe"},action="route",outbound="direct"}},final="direct"}})!;
                PolicyTcpBudget.Configure(data,new(){Mode="Auto",ExperimentalFastTcp=fast});Storage.Write(config,data);
                var start=new ProcessStartInfo(EngineTestAssets.Router(projectRoot)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(string arg in new[]{"run","-c",config,"--disable-color"})start.ArgumentList.Add(arg);
                using var engine=Process.Start(start)??throw new IOException("Resource fixture engine did not start.");var output=engine.StandardOutput.ReadToEndAsync();var error=engine.StandardError.ReadToEndAsync();
                async Task<bool> Request(bool drop)
                {
                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);deadline.CancelAfter(3000);
                    try
                    {
                        await using var stream=await PolicyTransport.Socks(port,"127.0.0.1",destinationPort,deadline.Token);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HEAD / HTTP/1.1\r\nHost: resources.fixture.invalid\r\n"+(drop?"X-Fixture-Drop: yes\r\n":"")+"Connection: close\r\n\r\n"),deadline.Token);
                        using var reader=new StreamReader(stream,Encoding.ASCII,false,128,true);return await reader.ReadLineAsync(deadline.Token)=="HTTP/1.1 200 OK";
                    }catch(IOException){return false;}
                }
                try
                {
                    for(int i=0;i<80;i++){if(engine.HasExited)throw new IOException("Resource engine exited: "+await error);try{using var ready=new TcpClient();await ready.ConnectAsync(IPAddress.Loopback,port,budget.Token);break;}catch(SocketException){if(i==79)throw;await Task.Delay(50,budget.Token);}}
                    for(int i=0;i<40;i++)Assert(await Request(false),"Resource fixture warmup failed.");
                    engine.Refresh();long privateBefore=engine.PrivateMemorySize64;int handlesBefore=engine.HandleCount;
                    TimeSpan cpuBefore=engine.TotalProcessorTime;var wall=Stopwatch.StartNew();
                    int success=0,failure=0,expectedReceived=received;
                    await Task.WhenAll(Enumerable.Range(0,4).Select(async worker=>
                    {
                        for(int i=0;i<150;i++)
                        {
                            bool drop=i%5==0,okay=await Request(drop);
                            if(drop){Assert(!okay,"An intentional application refusal became a false success.");Interlocked.Increment(ref failure);}
                            else{Assert(okay,"A healthy interleaved application request failed.");Interlocked.Increment(ref success);}
                            await Task.Delay(10,budget.Token);
                        }
                    }));
                    await Task.Delay(750,budget.Token);engine.Refresh();
                    Assert(received-expectedReceived==600&&success==480&&failure==120,"The receiver must independently confirm exactly 600 requests, 20% application refusals and no replay.");
                    long privateAfter=engine.PrivateMemorySize64;int handlesAfter=engine.HandleCount;
                    Assert(handlesAfter-handlesBefore<=32&&privateAfter-privateBefore<64*1024*1024,"Resource fixture exceeded its declared handle/memory growth ceiling.");
                    resources.Add(new{fast,requests=600,success,applicationRefusals=failure,concurrentClients=4,seconds=Math.Round(wall.Elapsed.TotalSeconds,2),
                        machineCpuPercent=Math.Round(100*(engine.TotalProcessorTime-cpuBefore).TotalSeconds/wall.Elapsed.TotalSeconds/Environment.ProcessorCount,2),
                        privateBeforeMiB=Math.Round(privateBefore/1048576.0,2),privateAfterMiB=Math.Round(privateAfter/1048576.0,2),handlesBefore,handlesAfter});
                }
                finally{if(!engine.HasExited)engine.Kill(true);await engine.WaitForExitAsync();await output;await error;}
            }
        }
        finally{budget.Cancel();app.Stop();await serving;await Task.WhenAll(peers);}
        Storage.Write(Path.Combine(projectRoot,"resilience-last.json"),new{schema=1,success=true,checkedUtc=DateTime.UtcNow,cases,resources,scope="Owned loopback; untrusted TLS rejection and application refusals, not SYN delay/packet loss/WireSock proof"});
        Console.WriteLine("PASS: slow SOCKS/TLS, TLS stall/refusal, Stop and no address replay; 1,200 native-core requests including 240 controlled application refusals, bounded concurrency/resources. "+System.Text.Json.JsonSerializer.Serialize(resources));
    }
}
