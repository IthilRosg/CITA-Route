using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace RoutePilot.Agent;

internal static class PolicyDnsEngineTests
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    static int FreePort(){var l=new TcpListener(IPAddress.Loopback,0);l.Start();int p=((IPEndPoint)l.LocalEndpoint).Port;l.Stop();return p;}
    internal static async Task Run(string projectRoot)
    {
        string root=Path.Combine(Path.GetFullPath(projectRoot),"runtime","dns-lab-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var primary=new TcpListener(IPAddress.Loopback,0);var backup=new TcpListener(IPAddress.Loopback,0);var app=new TcpListener(IPAddress.Loopback,0);
        primary.Start();backup.Start();app.Start();
        int primaryPort=((IPEndPoint)primary.LocalEndpoint).Port,backupPort=((IPEndPoint)backup.LocalEndpoint).Port,appPort=((IPEndPoint)app.LocalEndpoint).Port;
        int directPort=FreePort(),vpnPort=FreePort();
        var queries=new ConcurrentDictionary<(bool Backup,string Domain,string Source),int>();
        var requests=new ConcurrentDictionary<string,int>();var peers=new ConcurrentBag<Task>();
        System.Diagnostics.Process? engine=null;TcpListener? acceptedBad=null;Task? badTask=null;Task<string>? stderr=null,stdout=null;
        async Task ServeDns(TcpListener listener,bool secondary)
        {
            try{while(!stop.IsCancellationRequested){var client=await listener.AcceptTcpClientAsync(stop.Token);peers.Add(Task.Run(async()=>
            {
                using(client)try
                {
                    var stream=client.GetStream();
                    while(!stop.IsCancellationRequested)
                    {
                        byte[] length=new byte[2];await stream.ReadExactlyAsync(length,stop.Token);int size=BinaryPrimitives.ReadUInt16BigEndian(length);
                        if(size is <17 or >4096)throw new IOException("Unexpected fixture DNS size.");
                        byte[] query=new byte[size];await stream.ReadExactlyAsync(query,stop.Token);
                        int offset=12;var labels=new List<string>();
                        while(offset<query.Length&&query[offset]!=0){int n=query[offset++];if(n>63||offset+n>=query.Length)throw new IOException("Unexpected DNS question.");labels.Add(Encoding.ASCII.GetString(query,offset,n));offset+=n;}
                        offset++;if(offset+4>query.Length)throw new IOException("Truncated DNS question.");
                        string domain=string.Join('.',labels);int qtype=BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(offset,2));offset+=4;
                        string source=((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();queries.AddOrUpdate((secondary,domain,source),1,(_,n)=>n+1);
                        if(domain.StartsWith("timeout.")&&!secondary){await Task.Delay(2000,stop.Token);continue;}
                        int rcode=domain.StartsWith("negative.")?3:domain.StartsWith("nodata.")?0:!secondary||domain.StartsWith("all-fail.")?2:0;
                        int answers=rcode==0&&qtype==1&&!domain.StartsWith("nodata.")?2:0;
                        using var response=new MemoryStream();byte[] header=new byte[12];query.AsSpan(0,2).CopyTo(header);header[2]=0x81;header[3]=(byte)(0x80|rcode);header[5]=1;header[7]=(byte)answers;response.Write(header);response.Write(query,12,offset-12);
                        if(answers>0)foreach(byte last in new byte[]{2,1})response.Write(new byte[]{0xc0,0x0c,0,1,0,1,0,0,0,60,0,4,127,0,0,last});
                        byte[] data=response.ToArray();BinaryPrimitives.WriteUInt16BigEndian(length,(ushort)data.Length);await stream.WriteAsync(length,stop.Token);await stream.WriteAsync(data,stop.Token);
                    }
                }catch(Exception ex)when(ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException){}
            }));}}catch(Exception ex)when(ex is OperationCanceledException or SocketException or ObjectDisposedException){}
        }
        async Task ServeApp(TcpListener listener,bool healthy)
        {
            try{while(!stop.IsCancellationRequested){var client=await listener.AcceptTcpClientAsync(stop.Token);peers.Add(Task.Run(async()=>
            {
                using(client)try
                {
                    byte[] bytes=new byte[4096];int size=0;
                    while(size<bytes.Length){int read=await client.GetStream().ReadAsync(bytes.AsMemory(size),stop.Token);if(read==0)return;size+=read;if(Encoding.ASCII.GetString(bytes,0,size).Contains("\r\n\r\n"))break;}
                    string text=Encoding.ASCII.GetString(bytes,0,size);string host=text.Split("\r\n").FirstOrDefault(l=>l.StartsWith("Host: ",StringComparison.OrdinalIgnoreCase))??"missing";
                    requests.AddOrUpdate((healthy?"good:":"bad:")+host,1,(_,n)=>n+1);
                    if(healthy)await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),stop.Token);
                }catch(Exception ex)when(ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException){}
            }));}}catch(Exception ex)when(ex is OperationCanceledException or SocketException or ObjectDisposedException){}
        }
        var workers=new[]{ServeDns(primary,false),ServeDns(backup,true),ServeApp(app,true)};
        async Task<bool> Request(int port,string domain)
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(4000);
            try
            {
                await using var stream=await PolicyTransport.Socks(port,domain,appPort,deadline.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HEAD / HTTP/1.1\r\nHost: "+domain+"\r\nConnection: close\r\n\r\n"),deadline.Token);
                using var reader=new StreamReader(stream,Encoding.ASCII,false,128,true);
                return await reader.ReadLineAsync(deadline.Token)=="HTTP/1.1 200 OK";
            }catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException){stop.Token.ThrowIfCancellationRequested();return false;}
        }
        int Queries(bool secondary,string domain,string source)=>queries.TryGetValue((secondary,domain,source),out int n)?n:0;
        int Requests(bool healthy,string domain)=>requests.TryGetValue((healthy?"good:":"bad:")+"Host: "+domain,out int n)?n:0;
        try
        {
            var servers=new List<object>();
            foreach(string route in new[]{"direct","vpn"})
            {
                servers.Add(new{type="tcp",tag="dns-"+route+"-primary",server="127.0.0.1",server_port=primaryPort,detour=route});
                servers.Add(new{type="tcp",tag="dns-"+route+"-backup",server="127.0.0.1",server_port=backupPort,detour=route});
                servers.Add(new{type="cita-fallback",tag="dns-"+route,servers=new[]{"dns-"+route+"-primary","dns-"+route+"-backup"},timeout="150ms"});
            }
            var rules=new List<object>();foreach(string route in new[]{"direct","vpn"})
            {rules.Add(new{inbound=new[]{route},action="resolve",server="dns-"+route,strategy="ipv4_only"});rules.Add(new{inbound=new[]{route},action="route",outbound=route});}
            string config=Path.Combine(root,"engine.json");Storage.Write(config,new{
                log=new{level="warn"},dns=new{servers,final="dns-direct",strategy="ipv4_only"},
                inbounds=new[]{new{type="mixed",tag="direct",listen="127.0.0.1",listen_port=directPort},new{type="mixed",tag="vpn",listen="127.0.0.1",listen_port=vpnPort}},
                outbounds=new[]{new{type="direct",tag="direct",inet4_bind_address="127.0.0.1",connect_timeout="500ms"},new{type="direct",tag="vpn",inet4_bind_address="127.0.0.2",connect_timeout="500ms"}},
                route=new{rules,final="direct",default_domain_resolver="dns-direct"}});
            var fixture=JsonNode.Parse(File.ReadAllText(config))!;SystemRouter.PreserveDirectDnsProbe(fixture);Storage.Write(config,fixture);
            // Validate the actual HTTPS server schema against the pinned native
            // core without starting it or contacting an external resolver.
            var production=JsonNode.Parse(File.ReadAllText(config))!;
            production["dns"]!["servers"]=new JsonArray(PolicyDns.Servers().Cast<JsonNode?>().ToArray());
            SystemRouter.PreserveDirectDnsProbe(production);
            PolicyTcpBudget.Configure(production,new(){Mode="Auto",ExperimentalFastTcp=true});
            string productionConfig=Path.Combine(root,"production-dns-check.json");Storage.Write(productionConfig,production);
            var checkStart=new System.Diagnostics.ProcessStartInfo(EngineTestAssets.Router(projectRoot)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string value in new[]{"check","-c",productionConfig,"--disable-color"})checkStart.ArgumentList.Add(value);
            using(var check=System.Diagnostics.Process.Start(checkStart)??throw new IOException("DNS configuration check did not start."))
            {
                var checkOutput=check.StandardOutput.ReadToEndAsync();var checkError=check.StandardError.ReadToEndAsync();
                try{await check.WaitForExitAsync(stop.Token);Assert(check.ExitCode==0,"Production HTTPS DNS configuration was rejected: "+await checkError);}
                finally{if(!check.HasExited){check.Kill(true);await check.WaitForExitAsync();}await checkOutput;await checkError;}
            }
            var start=new System.Diagnostics.ProcessStartInfo(EngineTestAssets.Router(projectRoot)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string value in new[]{"run","-c",config,"--disable-color"})start.ArgumentList.Add(value);
            engine=System.Diagnostics.Process.Start(start)??throw new IOException("DNS lab engine did not start.");stdout=engine.StandardOutput.ReadToEndAsync();stderr=engine.StandardError.ReadToEndAsync();
            for(int i=0;i<80;i++)
            {
                if(engine.HasExited)throw new IOException("DNS lab engine exited: "+await stderr);
                try{using var ready=new TcpClient();await ready.ConnectAsync(IPAddress.Loopback,directPort,stop.Token);break;}
                catch(SocketException){if(i==79)throw;await Task.Delay(50,stop.Token);}
            }
            const string shared="shared.fixture.invalid";
            bool restricted=false;
            try{await using var refused=await PolicyTransport.Socks(20832,"127.0.0.1",appPort,stop.Token);}
            catch(IOException){restricted=true;}
            Assert(restricted&&requests.IsEmpty,"The resolver-only inbound must reject an unapproved loopback destination before any application bytes are sent.");
            Assert(await Request(directPort,shared),"A real direct stream must survive primary DNS SERVFAIL and refused first IP.");
            Assert(await Request(vpnPort,shared),"The second route must independently resolve and reach the same name.");
            Assert(Queries(false,shared,"127.0.0.1")>0&&Queries(true,shared,"127.0.0.1")>0&&Queries(false,shared,"127.0.0.2")>0&&Queries(true,shared,"127.0.0.2")>0,
                "DNS cache/backup must remain isolated to each route's actual bound transport.");
            Assert(Requests(true,shared)==2,"A refused TCP address must not duplicate the actual application request.");
            foreach(string domain in new[]{"negative.fixture.invalid","nodata.fixture.invalid"})
            {
                Assert(!await Request(directPort,domain),"A negative DNS answer cannot produce application success.");
                Assert(Queries(true,domain,"127.0.0.1")==0,"A valid negative DNS answer must not be replaced by backup.");
            }
            Assert(await Request(directPort,"timeout.fixture.invalid"),"A hung primary DNS must release the request to backup within its budget.");
            Assert(!await Request(directPort,"all-fail.fixture.invalid"),"All failed resolvers must not fabricate an application route.");
            acceptedBad=new TcpListener(IPAddress.Parse("127.0.0.2"),appPort);acceptedBad.Start();badTask=ServeApp(acceptedBad,false);
            const string postConnect="post-connect.fixture.invalid";
            Assert(!await Request(directPort,postConnect)&&Requests(false,postConnect)==1&&Requests(true,postConnect)==0,
                "After TCP establishment, failure must not replay application bytes to another address.");
            Storage.Write(Path.Combine(projectRoot,"dns-engine-last.json"),new{schema=1,success=true,checkedUtc=DateTime.UtcNow,
                scope="real core; owned loopback DNS/HTTP only; route labels are distinct loopback-bound outbounds, not physical VPN proof",
                cases=new[]{"production HTTPS/experimental config accepted","restricted resolver inbound","SERVFAIL backup","same-name route cache isolation","first-IP TCP refusal","single application request","NXDOMAIN terminal","NODATA terminal","DNS timeout backup","all DNS failed","no post-connect replay"}});
            Console.WriteLine("PASS: production HTTPS/experimental DNS configuration and restricted resolver-only inbound; real-core DNS fallback/timeout, route-isolated cache/detours, terminal negatives, refused first IP and no post-connect application replay.");
        }
        finally
        {
            stop.Cancel();primary.Stop();backup.Stop();app.Stop();acceptedBad?.Stop();
            if(engine!=null){if(!engine.HasExited)engine.Kill(true);await engine.WaitForExitAsync();engine.Dispose();}
            await Task.WhenAll(workers);if(badTask!=null)await badTask;await Task.WhenAll(peers);
            if(stdout!=null)await stdout;if(stderr!=null)await stderr;
        }
    }
}
