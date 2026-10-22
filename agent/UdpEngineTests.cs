using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace RoutePilot.Agent;

// Real engine + nested SOCKS UDP chain; no TUN, OS routes, WireSock or service changes.
internal static class UdpEngineTests
{
    internal static async Task Run(string root,bool fastTcp=false)
    {
        string folder=Path.Combine(root,"runtime","udp-engine-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var ports=new HashSet<int>();
        int Port()
        {
            while(true)
            {
                var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
                try{using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,port));if(ports.Add(port))return port;}
                catch(SocketException){}
                finally{listener.Stop();}
            }
        }
        int direct=Port(),viaVpn=Port(),bridge=Port();
        byte[] key=RandomNumberGenerator.GetBytes(32);await using var reflector=new UdpReflectorFixture(key,true);
        object Inbound(string tag,int port)=>new{type="mixed",tag,listen="127.0.0.1",listen_port=port};
        string bridgeConfig=Path.Combine(folder,"bridge.json"),routerConfig=Path.Combine(folder,"router.json");
        Storage.Write(bridgeConfig,new{log=new{disabled=true},inbounds=new[]{Inbound("bridge",bridge)},outbounds=new[]{new{type="direct",tag="direct"}},route=new{final="direct"}});
        Storage.Write(routerConfig,new{log=new{disabled=true},inbounds=new[]{Inbound("direct-probe",direct),Inbound("vpn-probe",viaVpn)},
            outbounds=new object[]{new{type="direct",tag="direct"},new{type="socks",tag="vpn",server="127.0.0.1",server_port=bridge,version="5"}},
            route=new{rules=new object[]{new{inbound=new[]{"direct-probe"},action="route",outbound="direct"},new{inbound=new[]{"vpn-probe"},action="route",outbound="vpn"}},final="direct"}});
        var configured=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(routerConfig))!;PolicyTcpBudget.Configure(configured,new(){Mode="Auto",ExperimentalFastTcp=fastTcp});Storage.Write(routerConfig,configured);
        Process Start(string config)
        {
            var info=new ProcessStartInfo(EngineTestAssets.Router(root)){UseShellExecute=false,CreateNoWindow=true};
            info.ArgumentList.Add("run");info.ArgumentList.Add("-c");info.ArgumentList.Add(config);return Process.Start(info)!;
        }
        using var bridgeProcess=Start(bridgeConfig);using var router=Start(routerConfig);
        try
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));
            foreach(int port in new[]{direct,viaVpn,bridge})
            {
                while(true)
                {
                    if(router.HasExited || bridgeProcess.HasExited)throw new IOException("Fixture engine exited.");
                    try{using var socket=new TcpClient();await socket.ConnectAsync(IPAddress.Loopback,port,deadline.Token);break;}
                    catch(SocketException){await Task.Delay(50,deadline.Token);}
                }
            }
            var results=await Task.WhenAll(new[]{direct,viaVpn}.Select(port=>UdpQualityProbe.Run(port,reflector.Endpoint,key,deadline.Token,40,500)));
            UdpQualityTests.Assert(results.All(r=>r.Sent==10&&r.Received==8),"Real direct and nested SOCKS UDP paths must both measure controlled 20% loss.");
            bridgeProcess.Kill();await bridgeProcess.WaitForExitAsync(deadline.Token);
            int before=reflector.Requests;bool unavailable=false;
            try{unavailable=(await UdpQualityProbe.Run(viaVpn,reflector.Endpoint,key,deadline.Token,40,500)).Received==0;}
            catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException){unavailable=true;}
            UdpQualityTests.Assert(unavailable && reflector.Requests==before,"An unavailable nested VPN UDP relay must not fall through to the direct outbound.");
            var stillDirect=await UdpQualityProbe.Run(direct,reflector.Endpoint,key,deadline.Token,40,500);
            UdpQualityTests.Assert(stillDirect.Received==8,"Direct UDP must continue when the fixture VPN bridge is down.");
            // Exercise the shipped server implementation too, not only the drop fixture.
            string reflectorDll=Path.Combine(root,"tools","udp-reflector","bin","Release","net10.0","Cita.UdpReflector.dll");
            if(!File.Exists(reflectorDll))throw new IOException("Build tools/udp-reflector in Release before UDP integration.");
            string keyFile=Path.Combine(folder,"reflector-key.txt");File.WriteAllText(keyFile,Convert.ToHexString(key));int serverPort=Port();
            var serverInfo=new ProcessStartInfo(Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT_X64")??Environment.GetEnvironmentVariable("DOTNET_ROOT")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"dotnet"),"dotnet.exe"))
                {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string argument in new[]{reflectorDll,keyFile,"127.0.0.1",serverPort.ToString()})serverInfo.ArgumentList.Add(argument);
            using(var server=Process.Start(serverInfo)!)
            {
                try
                {
                    string? ready=await server.StandardOutput.ReadLineAsync(deadline.Token);
                    if(ready?.StartsWith("CITA UDP reflector listening")!=true)
                        throw new IOException("Production reflector not ready: "+(ready??"EOF")+(server.HasExited?"; "+await server.StandardError.ReadToEndAsync():""));
                    var target=new IPEndPoint(IPAddress.Loopback,serverPort);
                    var echoed=await UdpQualityProbe.Run(direct,target,key,deadline.Token,40,500);
                    UdpQualityTests.Assert(echoed.Received==10,"Production reflector must authenticate and echo all loopback probes.");
                    var rejected=await UdpQualityProbe.Run(direct,target,RandomNumberGenerator.GetBytes(32),deadline.Token,40,500);
                    UdpQualityTests.Assert(rejected.Received==0,"Production reflector must ignore another authentication key.");
                }
                finally{if(!server.HasExited){server.Kill();await server.WaitForExitAsync();}}
            }
            Storage.Write(Path.Combine(root,"logs","udp-engine-test.json"),new{passed=true,checkedUtc=DateTime.UtcNow,directReplies=results[0].Received,vpnReplies=results[1].Received,
                probesPerPath=10,expectedLossPercent=20,noDirectFallback=true,directSurvivesBridgeStop=true,productionReflector=true,wrongKeyRejected=true,scope="Loopback SOCKS UDP engine fixture; no live TUN or remote WireSock proof"});
            Console.WriteLine("PASS: real engine Direct + nested SOCKS UDP, controlled 20% loss, bridge failure without direct fallback, Direct survives VPN bridge stop; fast TCP="+fastTcp+".");
        }
        finally
        {
            foreach(var process in new[]{router,bridgeProcess})if(!process.HasExited){process.Kill();await process.WaitForExitAsync();}
            Directory.Delete(folder,true);
        }
    }
}
