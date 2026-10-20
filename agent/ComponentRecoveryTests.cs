using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoutePilot.Agent;

internal static class ComponentRecoveryTests
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    static int FreePort(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
    static string Quote(string value)=>"'"+value.Replace("'","''")+"'";
    internal static async Task Run(string projectRoot)
    {
        string root=Path.Combine(Path.GetFullPath(projectRoot),"runtime","component-engine-"+Guid.NewGuid().ToString("N"));
        foreach(string name in new[]{"runtime","logs","data"})Directory.CreateDirectory(Path.Combine(root,name));
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(45));
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
        async Task Ping(Stream stream)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);timeout.CancelAfter(1500);
            byte[] request=Encoding.ASCII.GetBytes("CITA-ECHO-"+Guid.NewGuid().ToString("N")),reply=new byte[request.Length];
            await stream.WriteAsync(request,timeout.Token);await stream.ReadExactlyAsync(reply,timeout.Token);
            Assert(reply.SequenceEqual(request),"Established direct TCP socket lost/corrupted data.");
        }
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
            await Ping(direct);frames++;success=true;
            Console.WriteLine("PASS: same direct TCP socket survives bridge safety/repair and policy termination; established VPN sockets close, new VPN cannot fall back, busy coordinator cannot prevent a confirmed pause, native/generation/identity guards and persistent exit hold. Loopback only.");
        }
        finally
        {
            // A failed handoff may leave the resumed fixture alive before Update
            // returns. Recover it only by this fixture's PID/path/start identity.
            if(restarted==null)try
            {
                var saved=Storage.Read<JsonElement>(Path.Combine(root,"runtime/session.json"));int pid=saved.GetProperty("bridgePid").GetInt32();
                if(pid!=bridge?.Id)
                {
                    var p=Process.GetProcessById(pid);
                    if(Path.GetFullPath(p.MainModule!.FileName).Equals(bridgeExe,StringComparison.OrdinalIgnoreCase) && Math.Abs((p.StartTime.ToUniversalTime()-saved.GetProperty("bridgeStartedUtc").GetDateTime().ToUniversalTime()).TotalMilliseconds)<100)restarted=p;
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
