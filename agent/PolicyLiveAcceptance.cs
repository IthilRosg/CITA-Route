using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace RoutePilot.Agent;

// Explicit laboratory entry point. Requires an already healthy ordinary guest
// session. It owns only extra proxy processes, never the installed tunnel.
internal static class PolicyLiveAcceptance
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    static int FreePort(){var l=new TcpListener(IPAddress.Loopback,0);l.Start();int p=((IPEndPoint)l.LocalEndpoint).Port;l.Stop();return p;}
    internal static async Task Run(string projectRoot)
    {
        using var bios=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        Assert((bios?.GetValue("SystemManufacturer") as string)?.Contains("VMware",StringComparison.OrdinalIgnoreCase)==true,"Live acceptance is restricted to the authorized VMware guest.");
        string installed=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"CITA Test","Client","client");
        string sessionFile=Path.Combine(installed,"runtime/session.json");
        using var session=JsonDocument.Parse(File.ReadAllText(sessionFile));
        string id=session.RootElement.GetProperty("id").GetString()!;int generation=session.RootElement.GetProperty("bridgeGeneration").GetInt32();
        Assert(session.RootElement.GetProperty("running").GetBoolean()&&session.RootElement.GetProperty("bridgeReady").GetBoolean(),"A healthy owned WireSock session must be started separately by the guest fixture.");
        using var bridge=JsonDocument.Parse(File.ReadAllText(Path.Combine(installed,"runtime/bridge.json")));
        string physical=bridge.RootElement.GetProperty("outbounds")[0].GetProperty("bind_interface").GetString()!;
        using var reference=JsonDocument.Parse(File.ReadAllText(@"C:\CitaLab\server-egress-reference.json"));
        string serverHash=reference.RootElement.GetProperty("serverEgressHash").GetString()!;
        string root=Path.Combine(Path.GetFullPath(projectRoot),"runtime","live-acceptance-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(130));var results=new List<object>();
        bool Current()
        {
            using var current=JsonDocument.Parse(File.ReadAllText(sessionFile));var item=current.RootElement;
            return item.GetProperty("running").GetBoolean()&&item.GetProperty("id").GetString()==id&&item.GetProperty("bridgeGeneration").GetInt32()==generation;
        }
        async Task<string> ExitHash(int port)
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);deadline.CancelAfter(9000);
            await using var stream=await PolicyTransport.Socks(port,"www.cloudflare.com",443,deadline.Token);
            await using var tls=new SslStream(stream,false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost="www.cloudflare.com"},deadline.Token);
            await tls.WriteAsync(Encoding.ASCII.GetBytes("GET /cdn-cgi/trace HTTP/1.1\r\nHost: www.cloudflare.com\r\nConnection: close\r\n\r\n"),deadline.Token);
            using var reader=new StreamReader(tls,Encoding.ASCII,false,1024,true);string? first=await reader.ReadLineAsync(deadline.Token);
            Assert(first?.StartsWith("HTTP/1.1 200 ")==true,"The actual route's TLS/HTTP egress check did not return 200.");
            int bytes=0;while(true)
            {
                string? line=await reader.ReadLineAsync(deadline.Token);if(line==null)break;
                bytes+=line.Length;Assert(bytes<16384,"Oversized public egress fixture response.");
                if(line.StartsWith("ip=")&&IPAddress.TryParse(line[3..],out var ip)&&PolicySafety.Public(ip))
                    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ip.ToString()))).ToLowerInvariant();
            }
            throw new IOException("The public egress check contained no IPv4 proof.");
        }
        foreach(bool fast in new[]{false,true})
        {
            Assert(Current(),"The underlying session generation changed before the candidate run.");
            var ports=new PolicyPorts{Direct=FreePort(),Dpi=FreePort(),Vpn=FreePort(),DirectDns=20832};
            string config=Path.Combine(root,fast?"fast.json":"normal.json");
            var data=JsonSerializer.SerializeToNode(new{
                log=new{level="warn"},dns=new{servers=PolicyDns.Servers(),strategy="ipv4_only",final="dns-direct"},
                inbounds=new[]{("direct-probe",ports.Direct),("dpi-probe",ports.Dpi),("vpn-probe",ports.Vpn)}.Select(p=>new{type="mixed",tag=p.Item1,listen="127.0.0.1",listen_port=p.Item2}).ToArray(),
                outbounds=new object[]{new{type="direct",tag="direct",bind_interface=physical},new{type="direct",tag="dpi",bind_interface=physical},new{type="socks",tag="vpn",server="127.0.0.1",server_port=20808,version="5"}},
                route=new{rules=new object[]{new{inbound=new[]{"direct-probe","dpi-probe"},action="resolve",server="dns-direct",strategy="ipv4_only"},new{inbound=new[]{"vpn-probe"},action="resolve",server="dns-vpn",strategy="ipv4_only"},new{inbound=new[]{"direct-probe"},action="route",outbound="direct"},new{inbound=new[]{"dpi-probe"},network="tcp",action="route",outbound="dpi",tls_record_fragment=true},new{inbound=new[]{"vpn-probe"},action="route",outbound="vpn"}},final="direct",default_domain_resolver="dns-direct"}})!;
            SystemRouter.PreserveDirectDnsProbe(data);PolicyTcpBudget.Configure(data,new(){Mode="Auto",ExperimentalFastTcp=fast});Storage.Write(config,data);
            var start=new ProcessStartInfo(EngineTestAssets.Router(projectRoot)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string arg in new[]{"run","-c",config,"--disable-color"})start.ArgumentList.Add(arg);
            using var engine=Process.Start(start)??throw new IOException("Live candidate engine did not start.");var output=engine.StandardOutput.ReadToEndAsync();var error=engine.StandardError.ReadToEndAsync();
            try
            {
                for(int i=0;i<80;i++){if(engine.HasExited)throw new IOException("Candidate engine startup failed.");try{using var ready=new TcpClient();await ready.ConnectAsync(IPAddress.Loopback,ports.Direct,budget.Token);break;}catch(SocketException){if(i==79)throw;await Task.Delay(50,budget.Token);}}
                var clock=Stopwatch.StartNew();var cpu=engine.TotalProcessorTime;
                string directHash=await ExitHash(ports.Direct),vpnHash=await ExitHash(ports.Vpn);
                Assert(vpnHash==serverHash,"The candidate's VPN output does not match the independently recorded test-server exit.");
                var domains=new List<object>();
                foreach(string domain in new[]{"example.com","www.cloudflare.com","www.microsoft.com"})
                {
                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);deadline.CancelAfter(11000);
                    var selected=await PolicyTransport.SelectResolved(domain,ports,deadline.Token);
                    Assert(selected.Selection.Route!=null&&selected.Selection.Outcome==ProbeOutcome.Reachable,"No confirmed TLS route for the public acceptance domain: "+domain);
                    domains.Add(new{domain,route=selected.Selection.Route,decisionMilliseconds=selected.Selection.DecisionMilliseconds,dnsMilliseconds=selected.DnsMilliseconds,candidates=selected.Selection.Candidates});
                    Assert(Current(),"The underlying WireSock session changed during a real domain test.");
                }
                engine.Refresh();results.Add(new{fast,vpnExitVerified=true,directUsesSameObservedExit=directHash==vpnHash,domains,
                    sampleSeconds=Math.Round(clock.Elapsed.TotalSeconds,2),privateMiB=Math.Round(engine.PrivateMemorySize64/1048576.0,2),handles=engine.HandleCount,
                    machineCpuPercent=Math.Round(100*(engine.TotalProcessorTime-cpu).TotalSeconds/clock.Elapsed.TotalSeconds/Environment.ProcessorCount,2)});
            }
            finally{if(!engine.HasExited)engine.Kill(true);await engine.WaitForExitAsync();await output;await error;}
        }
        Assert(Current(),"Session identity must survive all candidate proxy tests.");
        Storage.Write(Path.Combine(projectRoot,"live-acceptance-last.json"),new{schema=1,success=true,checkedUtc=DateTime.UtcNow,results,
            scope="Candidate core and controller probes over an already healthy installed guest WireSock bridge; host NAT/VPN affects direct; no TUN replacement, physical failover or VPN-only leak proof"});
        Console.WriteLine("PASS: real WireSock server exit, native per-route DNS and authenticated TLS for three public domains in both TCP modes; session identity preserved. "+JsonSerializer.Serialize(results));
    }
}
