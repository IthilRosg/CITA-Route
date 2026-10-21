using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace RoutePilot.Agent;

internal static class ComponentRecoveryTests
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    static int FreePort(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
    static string Quote(string value)=>"'"+value.Replace("'","''")+"'";
    internal static async Task Run(string projectRoot,int soakCycles=0,int soakSeconds=0)
    {
        Assert(soakCycles is >=0 and <=20 && (soakSeconds==0||soakSeconds is >=30 and <=900),"Invalid bounded soak budget.");
        string root=Path.Combine(Path.GetFullPath(projectRoot),"runtime","component-engine-"+Guid.NewGuid().ToString("N"));
        foreach(string name in new[]{"runtime","logs","data"})Directory.CreateDirectory(Path.Combine(root,name));
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds((soakCycles>0?150:45)+soakSeconds));
        var echo=new TcpListener(IPAddress.Loopback,0);echo.Start();int echoPort=((IPEndPoint)echo.LocalEndpoint).Port;
        var peers=new ConcurrentBag<Task>();
        var server=Task.Run(async()=>
        {
            try{while(!budget.IsCancellationRequested){var client=await echo.AcceptTcpClientAsync(budget.Token);peers.Add(Task.Run(async()=>
            {
                using(client)try{byte[] bytes=new byte[256];int read;while((read=await client.GetStream().ReadAsync(bytes,budget.Token))>0)await client.GetStream().WriteAsync(bytes.AsMemory(0,read),budget.Token);}
                catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException){}
            }));}}
            catch(Exception ex)when(ex is SocketException or OperationCanceledException or ObjectDisposedException){}
        });
        Process? router=null,bridge=null,restarted=null,guarded=null;var logs=new List<Task<string>>();bool success=false;int frames=0;
        int bridgePort=FreePort(),directPort=FreePort(),vpnPort=FreePort();
        string routerExe=Path.GetFullPath(Path.Combine(projectRoot,"runtime/smart-router.exe")),bridgeExe=Path.GetFullPath(Path.Combine(projectRoot,"runtime/vpn-bridge.exe"));
        Process Start(string image,string config)
        {
            var info=new ProcessStartInfo(image){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string arg in new[]{"run","-c",config,"--disable-color"})info.ArgumentList.Add(arg);
            var p=Process.Start(info)??throw new IOException("Fixture engine did not start.");
            logs.Add(p.StandardOutput.ReadToEndAsync());logs.Add(p.StandardError.ReadToEndAsync());return p;
        }
        async Task<Stream> Open(int port,bool waitForReady=true)
        {
            for(int i=0;;i++){try{return await PolicyTransport.Socks(port,"127.0.0.1",echoPort,budget.Token);}catch(Exception ex)when(waitForReady && i<60 && ex is SocketException or IOException){await Task.Delay(40,budget.Token);}}
        }
        async Task Exchange(Stream stream,byte[] request,byte[] reply)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);timeout.CancelAfter(1500);
            await stream.WriteAsync(request,timeout.Token);await stream.ReadExactlyAsync(reply,timeout.Token);
            Assert(reply.SequenceEqual(request),"Established direct TCP socket lost/corrupted data.");
        }
        Task Ping(Stream stream){byte[] request=Encoding.ASCII.GetBytes("CITA-ECHO-"+Guid.NewGuid().ToString("N"));return Exchange(stream,request,new byte[request.Length]);}
        async Task Update(string state)
        {
            File.WriteAllText(Path.Combine(root,"observation.txt"),state);
            var result=await Tools.Run("powershell.exe",new[]{"-NoProfile","-ExecutionPolicy","Bypass","-File",Path.Combine(root,"recover.ps1")},seconds:8,cancellation:budget.Token).WaitAsync(TimeSpan.FromSeconds(10),budget.Token);
            Assert(result.Code==0,"Fixture recovery failed: "+result.Text);
        }
        try
        {
            string bridgeConfig=Path.Combine(root,"runtime/bridge.json"),routerConfig=Path.Combine(root,"runtime/router.json");
            Storage.Write(bridgeConfig,new{log=new{level="error"},inbounds=new[]{new{type="mixed",tag="bridge",listen="127.0.0.1",listen_port=bridgePort}},outbounds=new[]{new{type="direct",tag="egress"}}});
            Storage.Write(routerConfig,new{log=new{level="error"},
                inbounds=new[]{new{type="mixed",tag="direct-test",listen="127.0.0.1",listen_port=directPort},new{type="mixed",tag="vpn-test",listen="127.0.0.1",listen_port=vpnPort}},
                outbounds=new object[]{new{type="direct",tag="direct"},new{type="socks",tag="vpn",server="127.0.0.1",server_port=bridgePort,version="5"}},
                route=new{rules=new[]{new{inbound=new[]{"vpn-test"},action="route",outbound="vpn"}},final="direct"}});
            bridge=Start(bridgeExe,bridgeConfig);router=Start(routerExe,routerConfig);
            Storage.Write(Path.Combine(root,"runtime/session.json"),new{id="fixture",running=true,bridgeReady=true,bridgePid=bridge.Id,bridgeStartedUtc=bridge.StartTime.ToUniversalTime(),bridgeGeneration=1,routerPid=router.Id,routerStartedUtc=router.StartTime.ToUniversalTime()});
            // Restrict recovery visibility to fixture PIDs; no live lease, CLI or TUN.
            string script="$ErrorActionPreference='Stop'\n"+string.Join("\n",new[]{"FileStorage.ps1","WireSockRecovery.ps1","SessionRecovery.ps1"}.Select(n=>". "+Quote(Path.Combine(projectRoot,n))))+"\n"+
                "$root="+Quote(root)+";$runtime=Join-Path $root 'runtime';$bridgeExe="+Quote(bridgeExe)+";$routerExe="+Quote(routerExe)+"\n"+
                "function Invoke-AgentCoordinator($Command) {}\n"+
                "function Get-WireSockObservation { [pscustomobject]@{state=[IO.File]::ReadAllText((Join-Path $root 'observation.txt'))} }\n"+
                "function Get-OurProcesses { $s=Read-JsonFile (Join-Path $runtime 'session.json'); @($s.routerPid,$s.bridgePid)|ForEach-Object {Get-Process -Id $_ -ErrorAction SilentlyContinue} }\n"+
                "function Start-Helper($Exe,$Config) { Start-Process -FilePath $Exe -ArgumentList @('run','-c',('\"'+$Config+'\"'),'--disable-color') -WindowStyle Hidden -PassThru }\n"+
                "Update-WireSockBridge $true 'fixture'\n";
            File.WriteAllText(Path.Combine(root,"recover.ps1"),script,new UTF8Encoding(true));
            await using var direct=await Open(directPort);await using var vpn=await Open(vpnPort);
            await Ping(direct);await Ping(vpn);frames++;
            Task<Telemetry.WireSockSample> Connected()=>Task.FromResult(new Telemetry.WireSockSample{Profile="SmartRouting-AWG2",ConnectionState=3,Code="NoStatistics"});
            Task<Telemetry.WireSockSample> Unknown()=>Task.FromResult(new Telemetry.WireSockSample{Code="Unavailable"});
            Assert((await ComponentLiveness.PauseBridge(root,"old-session",1,bridgeExe,Unknown)).Code=="StaleSession"&&!bridge.HasExited,"A late safety action touched the new session.");
            Assert((await ComponentLiveness.PauseBridge(root,"fixture",2,bridgeExe,Unknown)).Code=="StaleSession"&&!bridge.HasExited,"A late safety action touched another bridge generation.");
            Assert((await ComponentLiveness.PauseBridge(root,"fixture",1,bridgeExe,Connected)).Code=="VpnConfirmed"&&!bridge.HasExited,"Fresh native confirmation did not supersede old CLI uncertainty.");
            string sessionPath=Path.Combine(root,"runtime/session.json");var saved=JsonNode.Parse(File.ReadAllText(sessionPath))!;
            var wrong=saved.DeepClone();wrong["bridgePid"]=router.Id;wrong["bridgeStartedUtc"]=router.StartTime.ToUniversalTime();Storage.Write(sessionPath,wrong);
            Assert((await ComponentLiveness.PauseBridge(root,"fixture",1,bridgeExe,Unknown)).Code=="IdentityUnavailable"&&!router.HasExited,"Bridge safety touched the direct router.");
            Storage.Write(sessionPath,saved);
            using(var wrongTime=CheckedProcess.Open(new(bridge.Id,bridge.StartTime.ToUniversalTime().AddTicks(1),bridgeExe)))
                Assert(wrongTime==null&&!bridge.HasExited,"Even a sub-millisecond process identity mismatch must not permit termination.");
            using(var busy=File.Open(Path.Combine(root,"runtime/network-control.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                Assert((await ComponentLiveness.PauseBridge(root,"fixture",1,bridgeExe,Unknown)).Stopped,"A busy coordinator prevented isolated bridge protection.");
            Assert(JsonNode.DeepEquals(saved,JsonNode.Parse(File.ReadAllText(sessionPath))),"Emergency protection overwrote the coordinator's session state.");
            await Update("Unknown");await bridge.WaitForExitAsync(budget.Token).WaitAsync(TimeSpan.FromSeconds(3));
            for(int i=0;i<30;i++){await Ping(direct);frames++;}
            bool refused=false;
            try{await using var unavailable=await Open(vpnPort,false);await Ping(unavailable);}catch(Exception ex)when(ex is IOException or SocketException){refused=true;}
            Assert(refused,"VPN-only connection fell back to direct while its bridge was paused.");
            bool oldVpnClosed=false;
            try{await Ping(vpn);}catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException){oldVpnClosed=true;}
            Assert(oldVpnClosed,"Paused bridge retained an established VPN socket.");Assert(!router.HasExited,"Bridge failure terminated the direct router.");
            await Update("Connected");
            var session=Storage.Read<JsonElement>(Path.Combine(root,"runtime/session.json"));restarted=Process.GetProcessById(session.GetProperty("bridgePid").GetInt32());
            Assert(session.GetProperty("routerPid").GetInt32()==router.Id && session.GetProperty("id").GetString()=="fixture","Bridge repair reset session/router identity.");
            await using var restored=await Open(vpnPort);await Ping(restored);
            for(int i=0;i<30;i++){await Ping(direct);frames++;}
            Assert(session.GetProperty("bridgeGeneration").GetInt32()==3,"Component generation did not advance on pause/resume.");
            var owner=new PolicyOwner{Pid=restarted.Id,StartedUtc=restarted.StartTime.ToUniversalTime(),Path=bridgeExe,SessionId="fixture"};
            Storage.Write(Path.Combine(root,"runtime/policy-process.json"),owner);
            var at=DateTime.UtcNow;
            Storage.Write(Path.Combine(root,"runtime/policy-heartbeat.json"),new PolicyPulse{InstanceId=Guid.NewGuid().ToString("N"),SessionId="fixture",Pid=owner.Pid,StartedUtc=owner.StartedUtc,Phase="Running",ProgressUtc=at,DeadlineUtc=at.AddSeconds(15),Sequence=1});
            Assert(ComponentLiveness.StopStalePolicy(root,"fixture",bridgeExe).Code=="ProgressCurrent"&&!restarted.HasExited,"A responsive worker was stopped.");
            Assert(ComponentLiveness.StopStalePolicy(root,"fixture",bridgeExe,at.AddSeconds(16)).Stopped,"A live but stale fixture worker was not stopped by its validated handle.");
            await Ping(direct);frames++;
            guarded=Start(bridgeExe,bridgeConfig);await using var finalVpn=await Open(vpnPort);await Ping(finalVpn);
            var final=JsonNode.Parse(File.ReadAllText(sessionPath))!;final["bridgePid"]=guarded.Id;final["bridgeStartedUtc"]=guarded.StartTime.ToUniversalTime();final["bridgeGeneration"]=4;Storage.Write(sessionPath,final);
            Storage.Write(Path.Combine(root,"runtime/session-health.json"),new{sessionId="fixture",bridgeGeneration=4,healthy=false,code="EgressMismatch",updatedUtc=DateTime.UtcNow});
            string safetyPath=Path.Combine(root,"runtime/bridge-safety.json");
            Storage.Write(safetyPath,new BridgeSafety{SessionId="fixture",BridgeGeneration=4,Hold=true,Code="EgressMismatch",UpdatedUtc=DateTime.UtcNow});
            using(var busy=File.Open(Path.Combine(root,"runtime/network-control.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            using(var locked=File.Open(safetyPath,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                var paused=await ComponentLiveness.PauseBridge(root,"fixture",4,bridgeExe,Connected);
                Assert(paused.Stopped&&!paused.HoldPersisted,"A locked safety file prevented bridge pause or falsely confirmed persistence.");
            }
            Assert(ComponentLiveness.Read<BridgeSafety>(Path.Combine(root,"runtime/bridge-safety.json")).Hold,"An exit mismatch must remain held after its original health result expires.");
            await Update("Connected");Assert(!Storage.Read<JsonElement>(sessionPath).GetProperty("bridgeReady").GetBoolean(),"Automatic resume bypassed the independent per-session safety hold.");
            await Ping(direct);frames++;
            if(soakCycles>0)
            {
                Assert(soakCycles<=20,"Soak process/port budgets are capped.");
                File.Delete(safetyPath);File.Delete(Path.Combine(root,"runtime/session-health.json"));
                await using var continuous=await Open(directPort);
                await using var video=await Open(directPort);
                byte[] udpKey=RandomNumberGenerator.GetBytes(32);
                await using var directReflector=new UdpReflectorFixture(udpKey,true);
                await using var vpnReflector=new UdpReflectorFixture(udpKey,true);
                using var trafficStop=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                long continuousFrames=0,maxRttMs=0,videoBytes=0,vpnBytes=0,udpSent=0,udpReceived=0;Exception? trafficFailure=null;
                var elapsed=Stopwatch.StartNew();
                var traffic=Task.Run(async()=>
                {
                    try{while(!trafficStop.IsCancellationRequested){var timer=Stopwatch.StartNew();await Ping(continuous);Interlocked.Increment(ref continuousFrames);maxRttMs=Math.Max(maxRttMs,timer.ElapsedMilliseconds);await Task.Delay(20,trafficStop.Token);}}
                    catch(OperationCanceledException)when(trafficStop.IsCancellationRequested){}
                    catch(Exception ex){trafficFailure=ex;}
                });
                var videoTraffic=Task.Run(async()=>
                {
                    byte[] payload=RandomNumberGenerator.GetBytes(32768),reply=new byte[payload.Length];
                    try{while(!trafficStop.IsCancellationRequested){await Exchange(video,payload,reply);Interlocked.Add(ref videoBytes,payload.Length);await Task.Delay(40,trafficStop.Token);}}
                    catch(OperationCanceledException)when(trafficStop.IsCancellationRequested){}
                    catch(Exception ex){trafficFailure=ex;}
                });
                var voiceTraffic=Task.Run(async()=>
                {
                    try{while(!trafficStop.IsCancellationRequested){var round=await UdpQualityProbe.Run(directPort,directReflector.Endpoint,udpKey,trafficStop.Token,20,500);
                        Assert(round.Sent==10&&round.Received==8,"Direct UDP did not preserve the fixture's controlled 20% loss during mixed traffic.");Interlocked.Add(ref udpSent,round.Sent);Interlocked.Add(ref udpReceived,round.Received);}}
                    catch(OperationCanceledException)when(trafficStop.IsCancellationRequested){}
                    catch(Exception ex){trafficFailure=ex;}
                });
                router.Refresh();long startBytes=router.PrivateMemorySize64;int startHandles=router.HandleCount;
                TimeSpan startCpu=router.TotalProcessorTime;long peakBytes=startBytes;int peakHandles=startHandles;
                try
                {
                    for(int cycle=0;cycle<soakCycles;cycle++)
                    {
                        await Update("Connected");var current=Storage.Read<JsonElement>(sessionPath);
                        restarted?.Dispose();restarted=Process.GetProcessById(current.GetProperty("bridgePid").GetInt32());
                        await using var flow=await Open(vpnPort);await Ping(flow);
                        if(soakSeconds>0)
                        {
                            var round=await UdpQualityProbe.Run(vpnPort,vpnReflector.Endpoint,udpKey,budget.Token,20,500);
                            Assert(round.Received==8,"Mixed traffic did not reach the restored VPN UDP path.");
                            var connected=Stopwatch.StartNew();byte[] payload=RandomNumberGenerator.GetBytes(32768),reply=new byte[payload.Length];
                            while(connected.Elapsed.TotalSeconds<soakSeconds/(2.0*soakCycles))
                            {await Exchange(flow,payload,reply);vpnBytes+=payload.Length;await Task.Delay(40,budget.Token);}
                        }
                        var paused=await ComponentLiveness.PauseBridge(root,"fixture",current.GetProperty("bridgeGeneration").GetInt32(),bridgeExe,Unknown);
                        Assert(paused.Stopped,"Repeated bridge pause failed.");
                        bool cycleRefused=false;try{await using var rejected=await Open(vpnPort,false);await Ping(rejected);}catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException){cycleRefused=true;}
                        Assert(cycleRefused,"Repeated paused VPN route fell back instead of refusing a new flow.");
                        if(soakSeconds>0)
                        {
                            int before=vpnReflector.Requests;bool udpRefused=false;
                            try{udpRefused=(await UdpQualityProbe.Run(vpnPort,vpnReflector.Endpoint,udpKey,budget.Token,20,500)).Received==0;}
                            catch(Exception ex)when(ex is IOException or SocketException){udpRefused=true;}
                            Assert(udpRefused&&vpnReflector.Requests==before,"Paused VPN UDP reached the reflector through a fallback path.");
                            await Task.Delay(TimeSpan.FromSeconds(soakSeconds/(2.0*soakCycles)),budget.Token);
                        }
                        await Ping(direct);Assert(trafficFailure==null,"Continuous direct traffic failed during recovery.");
                        Assert(Storage.Read<JsonElement>(sessionPath).GetProperty("routerPid").GetInt32()==router.Id,"Soak replaced the direct router.");
                        router.Refresh();peakBytes=Math.Max(peakBytes,router.PrivateMemorySize64);peakHandles=Math.Max(peakHandles,router.HandleCount);
                        if(soakSeconds>0)Console.WriteLine($"Mixed soak: cycle {cycle+1}/{soakCycles}, elapsed {elapsed.Elapsed.TotalSeconds:F0}s, direct frames {continuousFrames}, video bytes {videoBytes}, UDP {udpReceived}/{udpSent}.");
                    }
                }
                finally{trafficStop.Cancel();await Task.WhenAll(traffic,videoTraffic,voiceTraffic);}
                router.Refresh();long deltaBytes=router.PrivateMemorySize64-startBytes;int deltaHandles=router.HandleCount-startHandles;
                Assert(trafficFailure==null&&continuousFrames>0,"The persistent direct soak stream did not survive.");
                Assert(videoBytes>0&&udpSent>0,"Mixed TCP/UDP traffic was not exercised.");
                Assert(deltaHandles<64&&deltaBytes<128L*1024*1024,"Repeated recovery caused excessive short-run router resource growth.");
                Storage.Write(Path.Combine(projectRoot,"data",soakSeconds>0?"mixed-soak.json":"lifecycle-soak.json"),new{schema=2,cycles=soakCycles,durationSeconds=elapsed.Elapsed.TotalSeconds,continuousFrames,maxRttMs,videoBytes,vpnBytes,udpSent,udpReceived,routerPeakPrivateBytes=peakBytes,routerPeakHandles=peakHandles,
                    routerCpuPercent=100*(router.TotalProcessorTime-startCpu).TotalSeconds/elapsed.Elapsed.TotalSeconds/Environment.ProcessorCount,routerPrivateBytesDelta=deltaBytes,routerHandlesDelta=deltaHandles,passed=true,
                    loopbackOnly=true,wireSockMocked=true,longTermStabilityProved=false,checkedUtc=DateTime.UtcNow});
                Console.WriteLine($"PASS: {soakCycles} bridge recovery cycles; same direct TCP stream delivered {continuousFrames} frames, max RTT {maxRttMs} ms, router private bytes delta {deltaBytes}, handles delta {deltaHandles}. Bounded loopback soak only.");
            }
            success=true;
            Console.WriteLine("PASS: same direct TCP socket survives bridge safety/repair and policy termination; established VPN sockets close, new VPN cannot fall back, busy coordinator cannot prevent a confirmed pause, native/generation/identity guards and persistent exit hold. Loopback only.");
        }
        finally
        {
            // A failed handoff may leave the resumed fixture alive before Update
            // returns. Recover it only by this fixture's PID/path/start identity.
            try
            {
                var saved=Storage.Read<JsonElement>(Path.Combine(root,"runtime/session.json"));int pid=saved.GetProperty("bridgePid").GetInt32();
                if(pid!=bridge?.Id && pid!=restarted?.Id && pid!=guarded?.Id)
                {
                    var p=Process.GetProcessById(pid);
                    if(Path.GetFullPath(p.MainModule!.FileName).Equals(bridgeExe,StringComparison.OrdinalIgnoreCase) && p.StartTime.ToUniversalTime().ToFileTimeUtc()==saved.GetProperty("bridgeStartedUtc").GetDateTime().ToUniversalTime().ToFileTimeUtc())
                    {restarted?.Dispose();restarted=p;}
                    else p.Dispose();
                }
            }catch{}
            foreach(var p in new[]{guarded,restarted,bridge,router}.Where(p=>p!=null)){try{if(!p!.HasExited){p.Kill(true);await p.WaitForExitAsync();}}catch{}p!.Dispose();}
            budget.Cancel();echo.Stop();await server;try{await Task.WhenAll(peers).WaitAsync(TimeSpan.FromSeconds(2));}catch{}
            File.WriteAllLines(Path.Combine(root,"logs/engines.log"),await Task.WhenAll(logs));
            Storage.Write(Path.Combine(root,"result.json"),new{success,directFrames=frames,network="loopback only; real sing-box; mocked WireSock/coordinator",finishedUtc=DateTime.UtcNow});Console.WriteLine("Component integration artifacts: "+root);
        }
    }
}
