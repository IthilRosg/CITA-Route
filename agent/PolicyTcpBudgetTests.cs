using System.Net.NetworkInformation;
using System.Text.Json.Nodes;

namespace RoutePilot.Agent;

internal static class PolicyTcpBudgetTests
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    internal static void Run()
    {
        Assert(!new PolicyOptions().ExperimentalFastTcp,"Existing installations must keep the experiment disabled.");
        var ports=new PolicyPorts();Assert(ports.DnsPort(ports.Direct)==20832&&ports.DnsPort(ports.Dpi)==20832&&ports.DnsPort(ports.Vpn)==ports.Vpn,"Direct/DPI DNS must keep the ordinary direct dial budget; VPN DNS stays on VPN.");
        Assert(PolicyTcpBudget.CanonicalRoute("dpi-auto")=="dpi"&&PolicyTcpBudget.CanonicalRoute("direct-auto")=="direct"&&PolicyTcpBudget.CanonicalRoute("vpn-auto")==null,"Only the exact managed aliases may affect route statistics.");
        var config=JsonNode.Parse("{\"outbounds\":[{\"type\":\"direct\",\"tag\":\"direct\"},{\"type\":\"direct\",\"tag\":\"dpi\"},{\"type\":\"socks\",\"tag\":\"vpn\",\"connect_timeout\":\"7s\"},{\"type\":\"direct\",\"tag\":\"tailnet\",\"connect_timeout\":\"3s\"}],\"route\":{\"rules\":[{\"process_name\":[\"tailscaled.exe\"],\"action\":\"route\",\"outbound\":\"direct\"},{\"rule_set\":[\"personal-direct\"],\"action\":\"route\",\"outbound\":\"direct\"},{\"type\":\"logical\",\"rules\":[{\"rule_set\":[\"auto-direct\"]}],\"action\":\"route\",\"outbound\":\"direct\"},{\"inbound\":[\"dpi-probe\"],\"action\":\"route\",\"outbound\":\"dpi\"}]}}")!;
        PolicyTcpBudget.Configure(config,new(){Mode="Auto",ExperimentalFastTcp=true});
        PolicyTcpBudget.Configure(config,new(){Mode="Auto",ExperimentalFastTcp=true});
        Assert(config["outbounds"]!.AsArray().Count==6,"Repeated preparation must not duplicate experimental outbounds.");
        Assert(config["route"]!["rules"]![0]!["outbound"]!.GetValue<string>()=="direct"&&config["route"]!["rules"]![1]!["outbound"]!.GetValue<string>()=="direct"&&config["outbounds"]![0]!["connect_timeout"]!.GetValue<string>()=="3s","Manual/direct control traffic must retain its normal exit and TCP deadline.");
        Assert(config["route"]!["rules"]![2]!["outbound"]!.GetValue<string>()=="direct-auto"&&config["route"]!["rules"]![3]!["outbound"]!.GetValue<string>()=="dpi-auto","Adaptive flows and their matching probes must use the same bounded exit.");
        Assert(config["outbounds"]![2]!["connect_timeout"]!.GetValue<string>()=="7s"&&config["outbounds"]![3]!["connect_timeout"]!.GetValue<string>()=="3s","The experiment must not impose a timeout on a VPN proxy or the tailnet exit.");
        PolicyTcpBudget.Configure(config,new(){Mode="Auto"});
        Assert(config["outbounds"]!.AsArray().Count==4&&config["route"]!["rules"]![2]!["outbound"]!.GetValue<string>()=="direct","Disabling the experiment must undo aliases and their rule references.");
        Assert(config["outbounds"]![0]!["connect_timeout"]!.GetValue<string>()=="3s","Preparing without the flag must restore the normal timeout.");

        string root=Path.Combine(Path.GetTempPath(),"CitaTcpCache-"+Guid.NewGuid().ToString("N"));
        foreach(string part in new[]{"runtime/rules","data"})Directory.CreateDirectory(Path.Combine(root,part));
        try
        {
            File.WriteAllText(Path.Combine(root,"force-direct.txt"),"");File.WriteAllText(Path.Combine(root,"force-vpn.txt"),"");
            Storage.Write(Path.Combine(root,"runtime/bridge.json"),new{outbounds=new[]{new{bind_interface=NetworkInterface.GetAllNetworkInterfaces().First().Name}}});
            var session=new PolicySession{Id="tcp-cache",Running=true};Storage.Write(Path.Combine(root,"runtime/session.json"),session);
            string fingerprint=PolicyNetwork.Signature(root),epoch=session.Id+":"+fingerprint;
            var key=new PolicyKey(epoch,"budget.fixture.invalid",Environment.ProcessPath!,443);
            var decision=new PolicyDecision(key,"vpn","fixture",DateTime.UtcNow,DateTime.UtcNow.AddMinutes(5),1);
            foreach(bool savedBody in new[]{false,true})
            foreach(bool requestedBody in new[]{false,true})
            foreach(bool savedFast in new[]{false,true})
            foreach(bool requestedFast in new[]{false,true})
            {
                var worker=new PolicyWorker(root,session,new(){Mode="Auto",ExperimentalFastTcp=requestedFast,VerifyBody=requestedBody});
                Storage.Write(Path.Combine(root,"data/policy-cache.json"),new PolicyCache(fingerprint,"Auto",false,"",new[]{decision}){ExperimentalFastTcp=savedFast,BodyProbes=savedBody});
                worker.LoadCache();worker.Publish(false);
                int count=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"runtime/rules/auto-vpn.json")))!["rules"]!.AsArray().Count;
                Assert(count==(savedFast==requestedFast&&savedBody==requestedBody?1:0),"Persistent routes measured under another TCP budget must be recalibrated.");
                worker=new PolicyWorker(root,session,new(){Mode="Auto",ExperimentalFastTcp=requestedFast,VerifyBody=requestedBody});
                Storage.Write(Path.Combine(root,"runtime/policy-active.json"),new PolicyActiveSnapshot(epoch,"Auto",false,"",new[]{decision}){ExperimentalFastTcp=savedFast,BodyProbes=savedBody});
                worker.LoadActiveBindings();worker.Publish(false);
                count=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"runtime/rules/auto-vpn.json")))!["rules"]!.AsArray().Count;
                Assert(count==(savedFast==requestedFast&&savedBody==requestedBody?1:0),"Same-session snapshots must also reject a different TCP budget.");
            }
            // Absence in the old schema means the stable default, not enabled.
            Storage.Write(Path.Combine(root,"data/policy-cache.json"),new{Fingerprint=fingerprint,Mode="Auto",AllowListedDpi=false,ClearRequest="",Decisions=new[]{decision}});
            var legacy=new PolicyWorker(root,session,new(){Mode="Auto",VerifyBody=false});legacy.LoadCache();legacy.Publish(false);
            Assert(JsonNode.Parse(File.ReadAllText(Path.Combine(root,"runtime/rules/auto-vpn.json")))!["rules"]!.AsArray().Count==1,"A legacy cache remains compatible with the disabled experiment.");
        }
        finally{Directory.Delete(root,true);}
        Console.WriteLine("PASS: fast TCP remains opt-in; normal timeout restore, proxy/tailnet scope and cache/snapshot budget isolation with legacy compatibility.");
    }
}
