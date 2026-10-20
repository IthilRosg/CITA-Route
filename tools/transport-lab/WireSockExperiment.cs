using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cita.TransportLab;

// Uses only newly generated keys and a synthetic destination; never a user's profile.
internal static class WireSockExperiment
{
    internal static bool Disposable=>Environment.GetEnvironmentVariable("GITHUB_ACTIONS")=="true" &&
        Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT")=="github-hosted" && Environment.GetEnvironmentVariable("CITA_DISPOSABLE_WIRESOCK_LAB")=="1";
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    static Process Start(string image,params string[] args)
    {
        var info=new ProcessStartInfo(image){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in args)info.ArgumentList.Add(arg);
        return Process.Start(info)??throw new IOException("Experiment process did not start.");
    }
    static void Stop(Process? process){if(process==null)return;try{if(!process.HasExited){process.Kill();Assert(process.WaitForExit(5000),"Experiment process did not stop.");}}finally{process.Dispose();}}
    static async Task<string> Line(Process process)=>await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8))??throw new IOException("Experiment process exited before its checkpoint.");
    static async Task<Pulse> Check(Process process){await process.StandardInput.WriteLineAsync("pulse");await process.StandardInput.FlushAsync();return JsonSerializer.Deserialize<Pulse>(await Line(process))!;}
    static async Task<(string Private,string Public)> Keys(string engine)
    {
        using var process=Start(engine,"generate","wg-keypair");string text=await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        var privateKey=Regex.Match(text,@"(?im)^PrivateKey:\s*(\S+)");var publicKey=Regex.Match(text,@"(?im)^PublicKey:\s*(\S+)");
        Assert(process.ExitCode==0&&privateKey.Success&&publicKey.Success,"Synthetic WireGuard key generation failed.");
        return(privateKey.Groups[1].Value,publicKey.Groups[1].Value);
    }
    static async Task<Process> Peer(Fixture fixture,Echo echo,int index=0)
    {
        var peer=Start(fixture.Image,"--tunnel-peer","4",echo.TcpPort.ToString(),echo.UdpPort.ToString(),index.ToString());
        try{Assert(await Line(peer)=="READY","Synthetic tunnel peer was not ready.");return peer;}catch{Stop(peer);throw;}
    }
    static async Task<Process> Ready(Fixture fixture,Echo echo,Process tunnel,int index=0)
    {
        var timer=Stopwatch.StartNew();
        while(timer.Elapsed.TotalSeconds<25)
        {
            Assert(!tunnel.HasExited,"WireSock SDK process exited before encrypted traffic worked.");
            var peer=await Peer(fixture,echo,index);var pulse=await Check(peer);
            if(pulse.Tcp&&pulse.Udp)return peer;
            Stop(peer);await Task.Delay(250);
        }
        throw new IOException("Real WireSock did not deliver both synthetic TCP and UDP before the deadline.");
    }
    static NetworkInterface? Adapter()=>NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n=>
        n.GetIPProperties().UnicastAddresses.Any(a=>a.Address.Equals(IPAddress.Parse("10.202.0.2"))) && n.NetworkInterfaceType!=NetworkInterfaceType.Loopback);
    internal static async Task Run(string project)
    {
        Assert(Disposable,"Real WireSock experiments require the disposable GitHub-hosted Windows job.");
        project=Path.GetFullPath(project);Assert(Fixture.RuntimeRoot.Equals(Path.Combine(project,"runtime"),StringComparison.OrdinalIgnoreCase),"Experiment build/root differs.");
        string sdkRecord=Path.Combine(project,"runtime","wiresock-fixture","install.json");
        using var record=JsonDocument.Parse(File.ReadAllText(sdkRecord));string cli=record.RootElement.GetProperty("Cli").GetString()!;
        Assert(Path.GetFullPath(cli).StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(cli)=="wiresock-client.exe","Unrecognized SDK reference client.");
        string engine=Path.Combine(project,"runtime","smart-router.exe");
        var loopback=NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.NetworkInterfaceType==NetworkInterfaceType.Loopback);
        var physical=NetworkInterface.GetAllNetworkInterfaces().First(n=>n.NetworkInterfaceType!=NetworkInterfaceType.Loopback&&n.OperationalStatus==OperationalStatus.Up&&n.Supports(NetworkInterfaceComponent.IPv4));
        var fixture=Fixture.Create(Wfp.Luid((uint)loopback.GetIPProperties().GetIPv4Properties().Index),Wfp.Luid((uint)physical.GetIPProperties().GetIPv4Properties().Index));
        var checks=new List<string>();var observations=new List<object>();bool cleanup=false,passed=false;string? failure=null;
        Process? server=null,tunnel=null,peer=null;Task<string>? tunnelOut=null,tunnelErr=null;
        using var echo=new Echo(IPAddress.Loopback);using var direct=new Peer(IPAddress.Loopback,echo.TcpPort,echo.UdpPort);
        Console.WriteLine("Synthetic WireSock fixture: "+fixture.Root);
        try
        {
            await direct.Connect();Assert((await direct.Check()) is{Tcp:true,Udp:true},"Direct baseline failed.");
            var serverKeys=await Keys(engine);var clientKeys=await Keys(engine);
            using var port=new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback,0));int wirePort=((IPEndPoint)port.Client.LocalEndPoint!).Port;port.Close();
            string serverConfig=Path.Combine(fixture.Root,"server.json"),profile=Path.Combine(fixture.Root,"fixture.conf");
            File.WriteAllText(serverConfig,JsonSerializer.Serialize(new{log=new{level="error"},endpoints=new[]{new{type="wireguard",tag="wg",system=false,address=new[]{"10.202.0.1/24"},private_key=serverKeys.Private,listen_port=wirePort,
                peers=new[]{new{public_key=clientKeys.Public,allowed_ips=new[]{"10.202.0.2/32"}}}}},outbounds=new[]{new{type="direct",tag="echo"}},
                route=new{rules=new[]{new{inbound=new[]{"wg"},action="route",outbound="echo",override_address="127.0.0.1"}}}}));
            // Only this benchmark /32 is routed/captured, never the runner's default route.
            File.WriteAllText(profile,$"[Interface]\nPrivateKey = {clientKeys.Private}\nAddress = 10.202.0.2/32\n[Peer]\nPublicKey = {serverKeys.Public}\nEndpoint = 127.0.0.1:{wirePort}\nAllowedIPs = 198.18.123.1/32\n#@ws:AllowedApps = {fixture.Image}\nPersistentKeepalive = 1\n");
            server=Start(engine,"run","-c",serverConfig);_=server.StandardOutput.ReadToEndAsync();_=server.StandardError.ReadToEndAsync();await Task.Delay(500);
            Assert(!server.HasExited,"Synthetic user-space WireGuard server did not start.");
            Process Connect(bool virtualAdapter)
            {
                var args=new List<string>{"run","-config",profile,"-network-lock","disabled","-log-level","error"};if(virtualAdapter)args.Add("-lac");
                var result=Start(cli,args.ToArray());tunnelOut=result.StandardOutput.ReadToEndAsync();tunnelErr=result.StandardError.ReadToEndAsync();return result;
            }
            tunnel=Connect(false);peer=await Ready(fixture,echo,tunnel);
            checks.Add("Real WireSock transparent-mode encrypted TCP/UDP baseline using fresh local keys");
            using(var wfp=new Wfp())wfp.Replace(fixture,fixture.LoopbackLuid);
            long before=echo.Received;var blocked=await Check(peer);
            Assert(!blocked.Tcp&&!blocked.Udp&&echo.Received==before,"Transparent sockets unexpectedly bypassed the local interface restriction.");
            Assert((await direct.Check()) is{Tcp:true,Udp:true},"The same direct sockets were affected by a fixture-only restriction.");
            checks.Add("Transparent mode conflict reproduced: ALE blocks the original bridge sockets; direct survives");
            using(var wfp=new Wfp())wfp.Remove(fixture);Stop(peer);peer=null;
            peer=await Ready(fixture,echo,tunnel);Stop(peer);peer=null;Stop(tunnel);tunnel=null;await Task.Delay(1000);
            tunnel=Connect(true);
            var timer=Stopwatch.StartNew();NetworkInterface? adapter=null;
            while(timer.Elapsed.TotalSeconds<15 && (adapter=Adapter())==null){Assert(!tunnel.HasExited,"WireSock virtual-mode process exited.");await Task.Delay(200);}
            Assert(adapter!=null,"The synthetic WireSock virtual adapter did not appear.");
            int index=adapter!.GetIPProperties().GetIPv4Properties().Index;ulong luid=Wfp.Luid((uint)index);
            peer=await Ready(fixture,echo,tunnel,index);
            using(var wfp=new Wfp())wfp.Replace(fixture,luid);
            Assert((await Check(peer)) is{Tcp:true,Udp:true},"Virtual-adapter traffic is incompatible with the candidate ALE boundary.");
            checks.Add("Real WireSock virtual-adapter TCP/UDP remains usable through an interface-bound application boundary");
            Stop(tunnel);tunnel=null;await Task.Delay(1000);before=echo.Received;
            var lost=await Check(peer);Assert(!lost.Tcp&&!lost.Udp&&echo.Received==before,"Synthetic tunnel payload arrived after WireSock stopped.");
            Assert((await direct.Check()) is{Tcp:true,Udp:true},"Direct sockets failed when WireSock stopped.");
            checks.Add("WireSock process loss stops existing synthetic tunnel delivery while the same direct sockets survive");
            Stop(peer);peer=null;
            using(var wfp=new Wfp())wfp.Remove(fixture);
            tunnel=Connect(true);timer.Restart();adapter=null;
            while(timer.Elapsed.TotalSeconds<15 && (adapter=Adapter())==null){Assert(!tunnel.HasExited,"WireSock reconnect process exited.");await Task.Delay(200);}
            Assert(adapter!=null,"Virtual adapter did not return after reconnect.");
            int nextIndex=adapter!.GetIPProperties().GetIPv4Properties().Index;ulong nextLuid=Wfp.Luid((uint)nextIndex);
            observations.Add(new{adapterIdentityReused=nextLuid==luid,indexReused=nextIndex==index});
            using(var wfp=new Wfp())wfp.Replace(fixture,nextLuid);
            peer=await Ready(fixture,echo,tunnel,nextIndex);Assert((await direct.Check()) is{Tcp:true,Udp:true},"Direct sockets failed after reconnect.");
            checks.Add("Explicitly rebound/rearmed reconnect restores new synthetic tunnel flows; adapter reuse is recorded");
            passed=true;
        }
        catch(Exception ex){failure=ex.Message;throw;}
        finally
        {
            Stop(peer);Stop(tunnel);Stop(server);
            string? cleanupError=null;
            try{using var wfp=new Wfp();wfp.Remove(fixture);cleanup=true;}catch(Exception ex){cleanupError=ex.Message;}
            // Configurations/keys and native logs are never printed or uploaded.
            foreach(string file in new[]{"server.json","fixture.conf"})File.Delete(Path.Combine(fixture.Root,file));
            object Diagnostic(Task<string>? task)
            {
                if(task is not{IsCompletedSuccessfully:true})return new{complete=false};
                // Fixed classifications only: no native configuration, endpoint,
                // private/public key, hardware ID or license line is published.
                string value=task.Result;
                return new{complete=true,lines=value.Count(c=>c=='\n'),adapterError=Regex.IsMatch(value,"adapter|interface",RegexOptions.IgnoreCase),
                    permissionError=Regex.IsMatch(value,"denied|privilege|administrator",RegexOptions.IgnoreCase),
                    configurationError=Regex.IsMatch(value,"invalid|parse|configuration|decode",RegexOptions.IgnoreCase),
                    loopbackMentioned=value.Contains("loopback",StringComparison.OrdinalIgnoreCase),
                    unknownField=Regex.Match(value,"unknown field [\\\"]?([a-z_]+)",RegexOptions.IgnoreCase).Groups[1].Value};
            }
            File.WriteAllText(Path.Combine(fixture.Root,"wiresock-result.json"),JsonSerializer.Serialize(new{schema=1,passed,cleanup,failure,cleanupError,checks,observations,
                diagnosticOut=Diagnostic(tunnelOut),diagnosticError=Diagnostic(tunnelErr),checkedUtc=DateTime.UtcNow,
                wiresockDriverTested=checks.Count>0,physicalPacketCaptureVerified=false,perPacketIsolationProved=false,guiServiceLifecycleTested=false},new JsonSerializerOptions{WriteIndented=true}));
            foreach(string check in checks)Console.WriteLine("PASS: "+check);
            Assert(cleanup,"Synthetic fixture filter cleanup failed.");
        }
        Console.WriteLine("LIMIT: SDK core on synthetic local tunnel. Physical leakage, GUI-service transitions, AWG and long-term stability still need separate proof.");
    }
}
