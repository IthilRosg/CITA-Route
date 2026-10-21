using System.Net;
using System.Text.Json.Nodes;

namespace RoutePilot.Agent;

internal static class SystemRouterTests
{
    internal static void Run()
    {
        void Assert(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        Assert(!SystemRouter.HasIpv6Gateway(new[]{IPAddress.Parse("192.0.2.1"),IPAddress.IPv6Any,IPAddress.IPv6Loopback}),"Unspecified, loopback and IPv4 gateways must not advertise an IPv6 TUN default.");
        Assert(SystemRouter.HasIpv6Gateway(new[]{IPAddress.Parse("fe80::1")}),"A physical link-local IPv6 gateway is valid.");
        foreach(bool ipv6 in new[]{false,true})
        {
            // Cover old dual-stack installed templates, including a second
            // preparation after the available physical family has changed.
            var tun=JsonNode.Parse("{\"interface_name\":\"SmartRouting\",\"address\":[\"172.30.125.1/30\",\"fd7a:125::1/126\"],\"route_exclude_address\":[\"100.64.0.0/10\"]}")!;
            SystemRouter.ConfigureDiagnosticTun(tun,ipv6);
            Assert(tun["route_address"]!.AsArray().Any(a=>a!.GetValue<string>()=="::/0")==ipv6,"Installed templates must not retain a stale IPv6 default on an IPv4-only underlay.");
            Assert(tun["address"]!.AsArray().Any(a=>a!.GetValue<string>().Contains(':')),"Internal IPv6 must remain present to avoid StrictRoute's global IPv6 block.");
            Assert(tun["route_address"]!.AsArray().Any(a=>a!.GetValue<string>()==(ipv6?"::/0":"fd7a:126::/126")),"IPv4-only underlays may route only the TUN's own IPv6 subnet.");
            Assert(tun["address"]!.AsArray().Any(a=>a!.GetValue<string>()=="172.30.126.1/30"),"Diagnostic IPv4 addressing must stay available.");
            Assert(tun["route_exclude_address"]!.AsArray().Any(a=>a!.GetValue<string>()=="fd7a:115c:a1e0::/48"),"The Tailscale overlay exclusion must survive template preparation.");
            SystemRouter.ConfigureDiagnosticTun(tun,!ipv6);
            Assert(tun["route_address"]!.AsArray().Any(a=>a!.GetValue<string>()=="::/0")!=ipv6,"The next preparation must follow current physical capabilities.");
            Assert(tun["route_exclude_address"]!.AsArray().Count==2,"Repeated preparation must not accumulate exclusions.");
        }
        var rules=JsonNode.Parse("[{\"inbound\":[\"policy-ack\"],\"action\":\"reject\"},{\"action\":\"sniff\"},{\"rule_set\":[\"auto-vpn\"],\"action\":\"route\",\"outbound\":\"vpn\"}]")!.AsArray();
        SystemRouter.PreserveTailscaleTransport(rules);
        Assert(rules[0]!["action"]!.GetValue<string>()=="reject","Tailscale compatibility must preserve the dedicated policy-proof boundary.");
        Assert(rules[1]!["process_name"]!.AsArray().Any(n=>n!.GetValue<string>()=="tailscaled.exe") && rules[1]!["outbound"]!.GetValue<string>()=="direct","The Tailscale daemon transport must precede sniffing and adaptive decisions.");
        SystemRouter.PreserveTailscaleTransport(rules);
        Assert(rules.Count==4,"Installed compatible templates must not accumulate process rules.");
        Console.WriteLine("PASS: IPv4-only/dual-stack TUN topology, old-template migration, Tailscale overlay exclusions and early transport rules.");
    }
}
