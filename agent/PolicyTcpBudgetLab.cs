using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace RoutePilot.Agent;

// VM-only opt-in fixture. Healthy destinations are owned loopback sockets;
// failed dials use RFC 5737 TEST-NET. No external DNS or application requests,
// firewall, routing, driver or service changes.
internal static class PolicyTcpBudgetLab
{
    sealed record Sample(bool Fast,int Iteration,long DataMilliseconds,long ReserveMilliseconds,int ActiveAfterReturn);
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    static int FreePort(){var l=new TcpListener(IPAddress.Loopback,0);l.Start();int p=((IPEndPoint)l.LocalEndpoint).Port;l.Stop();return p;}
    internal static async Task Run(string projectRoot)
    {
        using var bios=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        Assert((bios?.GetValue("SystemManufacturer") as string)?.Contains("VMware",StringComparison.OrdinalIgnoreCase)==true,
            "The TCP timeout experiment must run in the authorized VMware lab, never on the physical host.");
        string root=Path.Combine(Path.GetFullPath(projectRoot),"runtime","tcp-budget-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var healthy=new TcpListener(IPAddress.Loopback,0);healthy.Start();int destinationPort=((IPEndPoint)healthy.LocalEndpoint).Port;
        const string blockedAddress="192.0.2.254";
        var peers=new ConcurrentBag<Task>();var samples=new List<Sample>();
        int healthyRequests=0,activeProbes=0;
        var server=Task.Run(async()=>
        {
            try{while(!stop.IsCancellationRequested){var client=await healthy.AcceptTcpClientAsync(stop.Token);peers.Add(Task.Run(async()=>
            {
                using(client)try
                {
                    byte[] buffer=new byte[2048];int length=0;
                    while(length<buffer.Length){int n=await client.GetStream().ReadAsync(buffer.AsMemory(length),stop.Token);if(n==0)return;length+=n;if(Encoding.ASCII.GetString(buffer,0,length).Contains("\r\n\r\n"))break;}
                    string request=Encoding.ASCII.GetString(buffer,0,length);Interlocked.Increment(ref healthyRequests);
                    if(request.Contains("X-Fixture-Slow: yes"))await Task.Delay(1500,stop.Token);
                    await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),stop.Token);
                }catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException){}
            }));}}catch(Exception ex)when(ex is SocketException or OperationCanceledException or ObjectDisposedException){}
        });
        try
        {
            // Prove the native timeout separately in each run. A TEST-NET IP
            // could be rejected by the surrounding NAT/VPN instead of stalling;
            // that would invalidate this experiment rather than prove speed.
            var publicFirst=IPAddress.Parse("1.1.1.1");var publicSecond=IPAddress.Parse("8.8.8.8");
            string rulesFile=Path.Combine(root,"auto-direct.json");Storage.Write(rulesFile,new{version=3,rules=new[]{new{domain=new[]{"multi.fixture.invalid"}}}});
            for(int iteration=0;iteration<3;iteration++)
            foreach(bool fast in new[]{false,true})
            {
                var ports=new PolicyPorts{Direct=FreePort(),Dpi=FreePort(),Vpn=FreePort()};
                int appPort=FreePort();
                string config=Path.Combine(root,fast?"fast.json":"baseline.json");
                var data=System.Text.Json.JsonSerializer.SerializeToNode(new{
                    log=new{level="warn"},dns=new{servers=new[]{new{type="hosts",tag="fixture",predefined=new Dictionary<string,string[]>{{"multi.fixture.invalid",new[]{blockedAddress,"127.0.0.1"}}}}}},
                    inbounds=new[]{("direct-probe",ports.Direct),("dpi-probe",ports.Dpi),("vpn",ports.Vpn),("app",appPort)}.Select(p=>new{type="mixed",tag=p.Item1,listen="127.0.0.1",listen_port=p.Item2}).ToArray(),
                    outbounds=new[]{"direct","dpi","vpn"}.Select(tag=>new{type="direct",tag}).ToArray(),
                    route=new{rules=new object[]{new{inbound=new[]{"app","direct-probe"},action="resolve",server="fixture",strategy="ipv4_only"},new{rule_set=new[]{"auto-direct"},action="route",outbound="direct"},new{inbound=new[]{"direct-probe"},action="route",outbound="direct"},new{inbound=new[]{"dpi-probe"},action="route",outbound="dpi"},new{inbound=new[]{"vpn"},action="route",outbound="vpn"}},rule_set=new[]{new{type="local",format="source",tag="auto-direct",path=rulesFile}},default_domain_resolver="fixture",final="direct"}})!;
                PolicyTcpBudget.Configure(data,new(){Mode="Auto",ExperimentalFastTcp=fast});Storage.Write(config,data);
                var start=new ProcessStartInfo(EngineTestAssets.Router(projectRoot)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(string arg in new[]{"run","-c",config,"--disable-color"})start.ArgumentList.Add(arg);
                using var engine=Process.Start(start)??throw new IOException("TCP budget engine did not start.");
                var output=engine.StandardOutput.ReadToEndAsync();var error=engine.StandardError.ReadToEndAsync();
                try
                {
                    for(int i=0;i<80;i++)
                    {
                        if(engine.HasExited)throw new IOException("TCP fixture engine exited: "+await error);
                        try{using var ready=new TcpClient();await ready.ConnectAsync(IPAddress.Loopback,ports.Direct,stop.Token);break;}
                        catch(SocketException){if(i==79)throw;await Task.Delay(50,stop.Token);}
                    }
                    async Task<bool> Request(int port,string address,CancellationToken token,bool slow=false)
                    {
                        await using var stream=await PolicyTransport.Socks(port,address,destinationPort,token);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HEAD / HTTP/1.1\r\nHost: multi.fixture.invalid\r\n"+(slow?"X-Fixture-Slow: yes\r\n":"")+"Connection: close\r\n\r\n"),token);
                        using var reader=new StreamReader(stream,Encoding.ASCII,false,128,true);return await reader.ReadLineAsync(token)=="HTTP/1.1 200 OK";
                    }
                    var rawClock=Stopwatch.StartNew();bool refused=false;
                    using(var rawDeadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                    {
                        rawDeadline.CancelAfter(4500);
                        try{await using var raw=await PolicyTransport.Socks(ports.Direct,blockedAddress,destinationPort,rawDeadline.Token);}
                        catch(IOException){refused=true;}
                    }
                    Assert(refused&&rawClock.ElapsedMilliseconds>=(fast?750:2500),"Owned single-IP TCP was not a proved dial timeout: "+rawClock.ElapsedMilliseconds+"ms; refused="+refused+"; fast="+fast);
                    var timer=Stopwatch.StartNew();int before=Volatile.Read(ref healthyRequests);
                    Assert(await Request(appPort,"multi.fixture.invalid",stop.Token),"Native adaptive multi-address connection must reach the second IP after the first dial timeout.");
                    long dataMilliseconds=timer.ElapsedMilliseconds;
                    Assert(healthyRequests==before+1,"A timed-out TCP dial must send exactly one application request to the healthy address.");
                    Assert(dataMilliseconds>=(fast?750:2500),"The primary address must really stall before backup, not fail immediately or be skipped. Observed="+dataMilliseconds+"ms; fast="+fast);

                    async Task<CandidateResult> Probe(IPAddress address,int port,CancellationToken token)
                    {
                        Interlocked.Increment(ref activeProbes);var clock=Stopwatch.StartNew();bool connected=false;
                        try
                        {
                            // Logical public answers stay in PolicySafety. Only
                            // this laboratory delegate maps them to owned sinks.
                            string target=port==ports.Vpn||address.Equals(publicSecond)?"127.0.0.1":blockedAddress;
                            await using var stream=await PolicyTransport.Socks(port,target,destinationPort,token);connected=true;
                            await stream.WriteAsync(Encoding.ASCII.GetBytes("HEAD / HTTP/1.1\r\nHost: fixture.invalid\r\nConnection: close\r\n\r\n"),token);
                            using var reader=new StreamReader(stream,Encoding.ASCII,false,128,true);bool okay=await reader.ReadLineAsync(token)=="HTTP/1.1 200 OK";
                            return new(okay,false,"owned HTTP fixture, not a TLS claim",clock.ElapsedMilliseconds){ConnectionEstablished=true};
                        }
                        catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException)
                        {token.ThrowIfCancellationRequested();return new(false,false,"owned TCP fixture",clock.ElapsedMilliseconds){ConnectionEstablished=connected};}
                        finally{Interlocked.Decrement(ref activeProbes);}
                    }
                    timer.Restart();
                    var reserve=await PolicyTransport.SelectResolved("reserve.fixture.invalid",ports,stop.Token,resolve:(_,_)=>Task.FromResult<IPAddress[]?>(new[]{publicFirst}),probe:Probe);
                    long reserveMilliseconds=timer.ElapsedMilliseconds;
                    Assert(reserve.Selection.Route=="vpn"&&activeProbes==0,"A ready VPN must be used only after the preferred TCP failures, with no orphaned probes.");
                    if(fast)
                    {
                        timer.Restart();
                        var multi=await PolicyTransport.SelectResolved("multi.fixture.invalid",ports,stop.Token,resolve:(_,_)=>Task.FromResult<IPAddress[]?>(new[]{publicFirst,publicSecond}),probe:Probe);
                        Assert(multi.Selection.Route=="direct"&&activeProbes==0,"The second direct address must preserve direct priority over an already-ready VPN.");
                        Assert(await Request(ports.Direct,"127.0.0.1",stop.Token,true),"The TCP dial timeout must not kill an established stream with a later application response.");
                        rawClock.Restart();refused=false;
                        try{await using var normal=await PolicyTransport.Socks(appPort,blockedAddress,destinationPort,stop.Token);}
                        catch(IOException){refused=true;}
                        Assert(refused&&rawClock.ElapsedMilliseconds>=2500,"An ordinary non-adaptive direct flow must keep its three-second TCP budget.");
                        using var cancelled=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);cancelled.CancelAfter(100);bool stopped=false;
                        try{await PolicyTransport.SelectResolved("cancel.fixture.invalid",ports,cancelled.Token,resolve:(_,_)=>Task.FromResult<IPAddress[]?>(new[]{publicFirst}),probe:Probe);}
                        catch(OperationCanceledException){stopped=true;}
                        Assert(stopped&&activeProbes==0,"Stop during stalled TCP must drain every candidate and never return a selected route.");
                    }
                    samples.Add(new(fast,iteration,dataMilliseconds,reserveMilliseconds,activeProbes));
                }
                finally{if(!engine.HasExited)engine.Kill(true);await engine.WaitForExitAsync();await output;await error;}
            }
            foreach(var candidate in samples.Where(s=>s.Fast))
            {
                var baseline=samples.Single(s=>!s.Fast&&s.Iteration==candidate.Iteration);
                Assert(candidate.DataMilliseconds+750<baseline.DataMilliseconds&&candidate.ReserveMilliseconds+1500<baseline.ReserveMilliseconds,
                    "Each paired experiment must demonstrate the expected improvement; correct routing alone is insufficient.");
            }
            Storage.Write(Path.Combine(projectRoot,"tcp-budget-last.json"),new{schema=1,success=true,checkedUtc=DateTime.UtcNow,provedNativeTimeout=true,samples,
                scope="VMware only; RFC 5737 TCP SYNs and owned loopback HTTP; real core; simulated route labels, not physical VPN or TLS availability"});
            Console.WriteLine("PASS: proved TCP stall, native second-IP recovery, baseline/experimental reserve wait, direct priority, post-connect lifetime and Stop/draining. Samples: "+System.Text.Json.JsonSerializer.Serialize(samples));
        }
        finally
        {
            stop.Cancel();healthy.Stop();await server;await Task.WhenAll(peers);
        }
    }
}
