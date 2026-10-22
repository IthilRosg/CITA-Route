using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace RoutePilot.Agent;

// Owned loopback only. The upstream public QUIC fixture is supplied by the lab
// runner, never captured from a user's browsing or sent to its original SNI.
internal static class PolicyWebEngineLab
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    static int Port(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
    internal static async Task Run(string project)
    {
        string root=Path.Combine(project,"runtime","web-engine-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"runtime/rules"));
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var sink=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        int sinkPort=((IPEndPoint)sink.Client.LocalEndPoint!).Port;
        var ports=new PolicyPorts{Ack=Port(),Proof=Port()};int mixed=Port();
        var key=new PolicyKey("fixture","www.google.com",Environment.ProcessPath!,443);
        byte[] quic=File.ReadAllBytes(Path.Combine(project,"runtime","web-quic-initial.bin"));
        Assert(quic.Length is >=1200 and <=65535,"Unexpected public upstream QUIC fixture length.");
        Assert(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(quic))=="4A132C02F053DEF557DB41B8D89E1AAB2A8CFDDF8F646D617C8EE6BBD695048D","QUIC fixture must match pinned upstream TestSniffUQUICChrome115, not a user packet capture.");
        var desired=PolicyPublisher.Routes.ToDictionary(r=>r,_=>Array.Empty<JsonObject>());
        foreach(var pair in desired)Storage.Write(Path.Combine(root,"runtime/rules/auto-"+pair.Key+".json"),new{version=3,rules=pair.Value});
        var rules=new JsonArray();
        foreach(string route in PolicyPublisher.Routes)
            rules.Add(System.Text.Json.JsonSerializer.SerializeToNode(new{type="logical",mode="and",rules=new object[]{new{inbound=new[]{"policy-ack"}},new{rule_set=new[]{"auto-"+route}}},action="route",outbound="proof",override_address="127.0.0.1",override_port=ports.Proof}));
        rules.Add(new JsonObject{["inbound"]=new JsonArray("policy-ack"),["action"]="reject"});
        rules.Add(new JsonObject{["action"]="sniff",["timeout"]="300ms"});
        rules.Add(new JsonObject{["process_name"]=new JsonArray("nonexistent-routepilot-web-fixture.exe"),["action"]="reject"});
        rules.Add(JsonNode.Parse("""{"type":"logical","mode":"and","rules":[{"rule_set":["auto-blocked"]},{"ip_version":6,"invert":true}],"action":"reject"}"""));
        // Every non-rejected packet is explicitly redirected to an owned sink.
        rules.Add(new JsonObject{["action"]="route",["outbound"]="sink",["override_address"]="127.0.0.1",["override_port"]=sinkPort});
        var config=System.Text.Json.JsonSerializer.SerializeToNode(new{log=new{level="info",output=Path.Combine(root,"engine.log")},
            inbounds=new[]{new{type="mixed",tag="app",listen="127.0.0.1",listen_port=mixed},new{type="mixed",tag="policy-ack",listen="127.0.0.1",listen_port=ports.Ack}},
            outbounds=new[]{new{type="direct",tag="sink"},new{type="direct",tag="proof"}},
            dns=new{servers=new[]{new{type="hosts",tag="owned",predefined=new Dictionary<string,string[]>{{"www.google.com",new[]{"127.0.0.1"}}}}}},
            route=new{rules,default_domain_resolver="owned",rule_set=PolicyPublisher.Routes.Select(r=>new{type="local",format="source",tag="auto-"+r,path=Path.Combine(root,"runtime/rules/auto-"+r+".json")})}})!;
        PolicyWebRouting.Configure(config);string file=Path.Combine(root,"engine.json");Storage.Write(file,config);
        var start=new ProcessStartInfo(EngineTestAssets.Router(project)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        foreach(string argument in new[]{"run","-c",file,"--disable-color"})start.ArgumentList.Add(argument);
        using var engine=Process.Start(start)??throw new IOException("Loopback engine did not start.");
        var error=engine.StandardError.ReadToEndAsync();var output=engine.StandardOutput.ReadToEndAsync();
        try
        {
            while(true)
            {
                if(engine.HasExited)throw new IOException("Loopback engine refused fixture: "+await error);
                try{using var ready=new TcpClient();await ready.ConnectAsync(IPAddress.Loopback,mixed,budget.Token);break;}
                catch(SocketException){await Task.Delay(30,budget.Token);}
            }
            await using var publisher=new PolicyPublisher(root,ports,()=>true);await publisher.Apply(desired,budget.Token);
            async Task<bool> Forwarded(byte[] payload)
            {
                // Domain-addressed SOCKS UDP also verifies that a matching
                // domain alone cannot block a non-QUIC datagram.
                using var control=new TcpClient();
                await control.ConnectAsync(IPAddress.Loopback,mixed,budget.Token);var stream=control.GetStream();
                // Mixed SOCKS metadata uses the control source port for process
                // lookup. Keep the owned UDP socket on that same number too.
                using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,((IPEndPoint)control.Client.LocalEndPoint!).Port));
                await stream.WriteAsync(new byte[]{5,1,0},budget.Token);byte[] hello=new byte[2];await stream.ReadExactlyAsync(hello,budget.Token);Assert(hello.SequenceEqual(new byte[]{5,0}),"Fixture SOCKS authentication.");
                int local=((IPEndPoint)udp.Client.LocalEndPoint!).Port;await stream.WriteAsync(new byte[]{5,3,0,1,127,0,0,1,(byte)(local>>8),(byte)local},budget.Token);
                byte[] reply=new byte[10];await stream.ReadExactlyAsync(reply,budget.Token);Assert(reply[1]==0&&reply[3]==1,"Fixture UDP relay is IPv4.");
                var relay=new IPEndPoint(new IPAddress(reply.AsSpan(4,4)),(reply[8]<<8)|reply[9]);if(relay.Address.Equals(IPAddress.Any))relay.Address=IPAddress.Loopback;Assert(IPAddress.IsLoopback(relay.Address),"Relay must remain owned loopback.");
                byte[] domain=Encoding.ASCII.GetBytes(key.Domain);var packet=new byte[7+domain.Length+payload.Length];packet[3]=3;packet[4]=(byte)domain.Length;domain.CopyTo(packet,5);packet[5+domain.Length]=1;packet[6+domain.Length]=187;payload.CopyTo(packet,7+domain.Length);
                await udp.SendAsync(packet,relay,budget.Token);
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);timeout.CancelAfter(1000);
                try{var arrived=await sink.ReceiveAsync(timeout.Token);return arrived.Buffer.SequenceEqual(payload);}
                catch(OperationCanceledException)when(!budget.IsCancellationRequested){return false;}
            }
            Assert(await Forwarded(quic),"Baseline QUIC must reach the owned sink before compatibility is applied.");
            desired["blocked"]=new[]{PolicyWebRouting.TcpFallback(key)};await publisher.Apply(desired,budget.Token);
            Assert(!await Forwarded(quic),"The real engine must reject QUIC for the exact published app/domain/port.");
            Assert(await Forwarded(Encoding.ASCII.GetBytes("owned-non-quic-udp")),"Non-QUIC UDP on the SAME app/domain/443 must remain available.");
            desired["blocked"]=Array.Empty<JsonObject>();await publisher.Apply(desired,budget.Token);
            Assert(await Forwarded(quic),"Removing compatibility must restore new QUIC flows.");
            Storage.Write(Path.Combine(project,"logs/web-engine-test.json"),new{success=true,finishedUtc=DateTime.UtcNow,quicBaseline=true,scopedReject=true,nonQuicUdpPreserved=true,quicRestored=true,scope="Owned loopback SOCKS UDP and real engine; not browser/TUN fallback, live QUIC or WireSock availability"});
            Console.WriteLine("PASS: real engine QUIC baseline, scoped rejection, same-domain non-QUIC UDP preservation, canary publication and QUIC restoration; loopback only.");
        }
        finally
        {
            if(!engine.HasExited){engine.Kill();await engine.WaitForExitAsync();}
            await Task.WhenAll(error,output);
            // Preserve bounded fixture logs on failure for local investigation.
        }
    }
}
