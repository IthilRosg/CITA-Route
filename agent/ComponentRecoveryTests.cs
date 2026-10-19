using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

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
        Process? router=null,bridge=null,restarted=null;var logs=new List<Task<string>>();bool success=false;int frames=0;
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
            Assert(session.GetProperty("bridgeGeneration").GetInt32()==3,"Component generation did not advance on pause/resume.");success=true;
            Console.WriteLine("PASS: same direct TCP socket survived bridge pause/resume; old VPN socket closed, new VPN refused while paused, restored VPN works. Loopback only.");
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
            foreach(var p in new[]{restarted,bridge,router}.Where(p=>p!=null)){try{if(!p!.HasExited){p.Kill(true);await p.WaitForExitAsync();}}catch{}p!.Dispose();}
            budget.Cancel();echo.Stop();await server;try{await Task.WhenAll(peers).WaitAsync(TimeSpan.FromSeconds(2));}catch{}
            File.WriteAllLines(Path.Combine(root,"logs/engines.log"),await Task.WhenAll(logs));
            Storage.Write(Path.Combine(root,"result.json"),new{success,directFrames=frames,network="loopback only; real sing-box; mocked WireSock/coordinator",finishedUtc=DateTime.UtcNow});Console.WriteLine("Component integration artifacts: "+root);
        }
    }
}
