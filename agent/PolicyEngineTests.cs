using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoutePilot.Agent;

internal static class PolicyEngineTests
{
    static void Assert(bool condition,string message){if(!condition)throw new IOException(message);}
    static int FreePort(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
    internal static async Task Run(string projectRoot)
    {
        string root=Path.Combine(Path.GetFullPath(projectRoot),"runtime","optimization-"+Guid.NewGuid().ToString("N"));
        foreach(string part in new[]{"runtime/rules","private","data","logs"})Directory.CreateDirectory(Path.Combine(root,part));
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(110));
        Process? engine=null;PolicyPublisher? publisher=null;Task? controller=null;Task<string>? stderr=null,stdout=null;
        var failures=new ConcurrentQueue<string>();var latencies=new List<long>();int connections=0;bool success=false;
        var ports=new PolicyPorts{Api=FreePort(),Ack=FreePort(),Proof=FreePort()};int mixed=FreePort();
        var sinks=new Dictionary<string,TcpListener>();var sinkTasks=new List<Task>();
        foreach(string route in new[]{"direct","dpi","vpn","fallback"})
        {
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();sinks[route]=listener;
            byte[] label=Encoding.ASCII.GetBytes(route+"\n");
            sinkTasks.Add(Task.Run(async()=>
            {
                try
                {
                    while(!budget.IsCancellationRequested)
                    {
                        using var client=await listener.AcceptTcpClientAsync(budget.Token);
                        try
                        {
                            // Consume the request before closing, otherwise Windows can
                            // reset a socket with unread bytes and discard its reply.
                            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);timeout.CancelAfter(2000);
                            byte[] buffer=new byte[2048];int length=0;
                            while(length<buffer.Length)
                            {
                                int count=await client.GetStream().ReadAsync(buffer.AsMemory(length),timeout.Token);if(count==0)break;length+=count;
                                if(Encoding.ASCII.GetString(buffer,0,length).Contains("\r\n\r\n"))break;
                            }
                            await client.GetStream().WriteAsync(label,timeout.Token);
                        }
                        catch(Exception ex)when(ex is IOException or OperationCanceledException or SocketException){}
                    }
                }
                catch(Exception ex)when(ex is OperationCanceledException or SocketException or ObjectDisposedException){}
            }));
        }
        int Sink(string route)=>((IPEndPoint)sinks[route].LocalEndpoint).Port;
        var session=new PolicySession{Id=Guid.NewGuid().ToString("N"),Running=true};
        string app=Environment.ProcessPath!,domain="transition.example.com";
        File.WriteAllText(Path.Combine(root,"force-direct.txt"),"");File.WriteAllText(Path.Combine(root,"force-vpn.txt"),"");
        Storage.Write(Path.Combine(root,"runtime/bridge.json"),new{outbounds=new[]{new{bind_interface=System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().First().Name}}});
        Storage.Write(Path.Combine(root,"runtime/policy-ports.json"),ports);
        Storage.Write(Path.Combine(root,"data/routing-policy.json"),new PolicyOptions{Mode="Auto",AllowListedDpi=true});
        Storage.Write(Path.Combine(root,"runtime/session.json"),session);
        string token=Guid.NewGuid().ToString("N");File.WriteAllText(Path.Combine(root,"private/controller-token.txt"),token);
        var cachedKey=new PolicyKey("cache-generation",domain,app,443);
        Storage.Write(Path.Combine(root,"data/policy-cache.json"),new PolicyCache(PolicyNetwork.Signature(root),"Auto",true,"",new[]{new PolicyDecision(cachedKey,"vpn","fixture",DateTime.UtcNow,DateTime.UtcNow.AddMinutes(15),1)}));
        PolicyWorker.Prepare(root);

        var rules=new List<object>();
        foreach(string route in PolicyPublisher.Routes)
            rules.Add(new{type="logical",mode="and",rules=new object[]{new{inbound=new[]{"policy-ack"}},new{rule_set=new[]{"auto-"+route}}},action="route",outbound="policy-proof",override_address="127.0.0.1",override_port=ports.Proof});
        rules.Add(new{inbound=new[]{"policy-ack"},action="reject"});
        rules.Add(new{action="sniff",timeout="100ms"});
        rules.Add(new{process_name=new[]{"nonexistent-routepilot-fixture.exe"},action="reject"});
        rules.Add(new{type="logical",mode="and",rules=new object[]{new{rule_set=new[]{"auto-blocked"}},new{ip_version=6,invert=true}},action="reject"});
        foreach(string route in new[]{"direct","vpn","dpi"})
        {
            rules.Add(new{type="logical",mode="and",rules=new object[]{new{rule_set=new[]{"auto-"+route}},new{type="logical",mode="and",rules=new[]{new{ip_version=4,invert=true},new{ip_version=6,invert=true}}}},action="resolve",server="fixture-dns",strategy="ipv4_only"});
            rules.Add(new{type="logical",mode="and",rules=new object[]{new{rule_set=new[]{"auto-"+route}},new{ip_version=6,invert=true}},action="route",outbound=route,override_address="127.0.0.1",override_port=Sink(route)});
        }
        rules.Add(new{action="route",outbound="fallback",override_address="127.0.0.1",override_port=Sink("fallback")});
        string config=Path.Combine(root,"engine.json");
        Storage.Write(config,new{log=new{level="info",output=Path.Combine(root,"logs/engine.log")},
            dns=new{servers=new[]{new{type="hosts",tag="fixture-dns",predefined=new Dictionary<string,string[]>{{domain,new[]{"127.0.0.1"}}}}}},
            inbounds=new[]{new{type="mixed",tag="test",listen="127.0.0.1",listen_port=mixed},new{type="mixed",tag="policy-ack",listen="127.0.0.1",listen_port=ports.Ack}},
            outbounds=new[]{"direct","dpi","vpn","fallback","policy-proof"}.Select(tag=>new{type="direct",tag}).ToArray(),
            route=new{rules,rule_set=PolicyPublisher.Routes.Select(route=>new{type="local",format="source",tag="auto-"+route,path=Path.Combine(root,"runtime/rules/auto-"+route+".json")}).ToArray()},
            experimental=new{clash_api=new{external_controller="127.0.0.1:"+ports.Api,secret=token}}});
        int addressFamilyCase=0;
        async Task<string> Request(CancellationToken stop,string? forcedHost=null)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop);timeout.CancelAfter(2000);
            await using var stream=await PolicyTransport.Socks(mixed,forcedHost??(Interlocked.Increment(ref addressFamilyCase)%2==0?domain:"127.0.0.1"),443,timeout.Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HEAD / HTTP/1.1\r\nHost: "+domain+"\r\nConnection: close\r\n\r\n"),timeout.Token);
            using var reader=new StreamReader(stream,Encoding.ASCII,false,128,true);return (await reader.ReadLineAsync(timeout.Token))??"";
        }
        Dictionary<string,JsonObject[]> Desired(string route)=>PolicyPublisher.Routes.ToDictionary(r=>r,r=>r==route?new[]{PolicyPublisher.Rule(cachedKey)}:Array.Empty<JsonObject>());
        try
        {
            var start=new ProcessStartInfo(Path.Combine(projectRoot,"runtime/smart-router.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
            foreach(string arg in new[]{"run","-c",config,"--disable-color"})start.ArgumentList.Add(arg);
            engine=Process.Start(start)??throw new IOException("Isolated engine did not start.");
            stderr=engine.StandardError.ReadToEndAsync();stdout=engine.StandardOutput.ReadToEndAsync();
            for(int i=0;i<60;i++)
            {
                if(engine.HasExited)throw new IOException(await stderr);
                try{Assert(await Request(budget.Token)=="vpn","The VERY FIRST connection must use preloaded cached VPN rules.");break;}
                catch(SocketException){await Task.Delay(40,budget.Token);if(i==59)throw;}
                catch(OperationCanceledException)when(engine.HasExited){throw new IOException(await stderr);}
            }
            Console.WriteLine("PASS: isolated real engine uses cached VPN on its first connection; all test outputs stay on loopback.");
            Assert(await Request(budget.Token,"::1")=="fallback","An original IPv6 destination must not inherit an IPv4-only adaptive decision.");
            publisher=new(root,ports,()=>session.Running);
            await publisher.Apply(Desired("vpn"),budget.Token);
            using(var traffic=CancellationTokenSource.CreateLinkedTokenSource(budget.Token))
            {
                var hammer=Task.Run(async()=>
                {
                    while(!traffic.IsCancellationRequested)
                    {
                        try{string result=await Request(traffic.Token);Interlocked.Increment(ref connections);if(result is not ("vpn" or "dpi"))failures.Enqueue("Unexpected protected transition: "+result);}
                        catch(OperationCanceledException)when(traffic.IsCancellationRequested){break;}
                        catch(Exception ex){failures.Enqueue(ex.GetType().Name+": "+ex.Message);}
                        try{await Task.Delay(5,traffic.Token);}catch(OperationCanceledException){break;}
                    }
                });
                try
                {
                    for(int i=0;i<30;i++)
                    {
                        string target=i%2==0?"dpi":"vpn";var timer=Stopwatch.StartNew();await publisher.Apply(Desired(target),budget.Token);latencies.Add(timer.ElapsedMilliseconds);
                        Assert(await Request(budget.Token)==target,"Applied acknowledgement must match the actual new outbound.");
                        if(i%10==9)Console.WriteLine("Transitions verified: "+(i+1)+"/30.");
                    }
                }
                finally{traffic.Cancel();await hammer;}
            }
            Assert(failures.IsEmpty,"A protected transition leaked or failed: "+string.Join("; ",failures.Take(3)));
            string lockedPath=Path.Combine(root,"runtime/rules/auto-dpi.json");
            using(var locked=new FileStream(lockedPath,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                bool refused=false;try{await publisher.Apply(Desired("dpi"),budget.Token);}catch(IOException){refused=true;}
                Assert(refused && await Request(budget.Token)=="vpn","A staging write failure must preserve the old VPN route.");
            }
            await publisher.DisposeAsync();publisher=null;
            var wrongAck=new PolicyPorts{Ack=FreePort(),Proof=ports.Proof};
            publisher=new(root,wrongAck,()=>true);
            bool timedOut=false;try{await publisher.Apply(Desired("dpi"),budget.Token);}catch(OperationCanceledException){timedOut=true;}
            Assert(timedOut && publisher.Version==0 && await Request(budget.Token)=="vpn","Missing engine acknowledgement must not remove the old route or report Applied.");
            await publisher.DisposeAsync();publisher=null;
            Console.WriteLine("PASS: 30 VPN/DPI transitions with mixed hostname/IPv4 requests, independent IPv6, atomic write refusal and lost-ack timeout preserve the protected path.");

            session.RouterPid=engine.Id;Storage.Write(Path.Combine(root,"runtime/session.json"),session);
            controller=PolicyWorker.Run(root);
            for(int i=0;i<80;i++)
            {
                if(controller.IsCompleted)await controller;
                string path=Path.Combine(root,"runtime/policy-state.json");
                if(File.Exists(path) && Storage.Read<JsonElement>(path).GetProperty("phase").GetString()=="Auto")break;
                await Task.Delay(100,budget.Token);if(i==79)throw new IOException("Controller readiness timeout.");
            }
            Assert(await Request(budget.Token)=="vpn","The live controller must restore the cache and confirm it.");
            File.WriteAllText(Path.Combine(root,"runtime/clear-policy.request"),Guid.NewGuid().ToString("N"));
            for(int i=0;i<80;i++)
            {
                if(controller.IsCompleted)await controller;
                var state=Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json"));
                if(state.GetProperty("phase").GetString()=="Auto" && state.GetProperty("decisions").GetArrayLength()==0)break;
                await Task.Delay(100,budget.Token);if(i==79)throw new IOException("Clear did not reach the engine.");
            }
            Assert(await Request(budget.Token)=="fallback","An explicit Clear must withdraw the adaptive rule after acknowledgement.");
            File.WriteAllText(Path.Combine(root,"private/SmartRouting-AWG2.conf"),"synthetic-new-profile-generation");
            await controller.WaitAsync(TimeSpan.FromSeconds(8));controller=null;
            Assert(Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json")).GetProperty("phase").GetString()=="NetworkChanged","A changed network/profile generation must stop the real controller and preserve its terminal diagnostic.");
            controller=PolicyWorker.Run(root);
            for(int i=0;i<80;i++)
            {
                if(controller.IsCompleted)await controller;
                if(Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json")).GetProperty("phase").GetString()=="Auto")break;
                await Task.Delay(100,budget.Token);if(i==79)throw new IOException("Restart after network-generation change timed out.");
            }
            session.Running=false;Storage.Write(Path.Combine(root,"runtime/session.json"),session);await controller.WaitAsync(TimeSpan.FromSeconds(8));controller=null;
            Console.WriteLine("PASS: full controller startup, acknowledged readiness, Clear, profile/network generation change and clean shutdown on the isolated engine.");
            success=true;
        }
        finally
        {
            session.Running=false;Storage.Write(Path.Combine(root,"runtime/session.json"),session);
            if(publisher!=null)await publisher.DisposeAsync();
            if(controller!=null){try{await controller.WaitAsync(TimeSpan.FromSeconds(8));}catch{}}
            if(engine!=null){try{engine.Kill(true);await engine.WaitForExitAsync();}catch{}engine.Dispose();}
            if(stderr!=null)File.WriteAllText(Path.Combine(root,"logs/stderr.log"),await stderr);
            if(stdout!=null)File.WriteAllText(Path.Combine(root,"logs/stdout.log"),await stdout);
            budget.Cancel();foreach(var listener in sinks.Values)listener.Stop();await Task.WhenAll(sinkTasks);
            var sorted=latencies.Order().ToArray();
            Storage.Write(Path.Combine(root,"result.json"),new{success,connections,transitions=sorted.Length,unexpected=failures.ToArray(),medianMilliseconds=sorted.Length>0?sorted[sorted.Length/2]:0,p95Milliseconds=sorted.Length>0?sorted[(int)Math.Ceiling(sorted.Length*.95)-1]:0,network="loopback only; no TUN, WireSock or service changes",finishedUtc=DateTime.UtcNow});
            Console.WriteLine("Optimization integration artifacts: "+root);
        }
    }
}
