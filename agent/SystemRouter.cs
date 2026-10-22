namespace RoutePilot.Agent;

public static class SystemRouter
{
    internal static void PreserveDirectDnsProbe(System.Text.Json.Nodes.JsonNode data)
    {
        var inbounds=data["inbounds"]!.AsArray();
        foreach(var old in inbounds.Where(n=>n?["tag"]?.GetValue<string>()=="direct-dns-probe").ToArray())inbounds.Remove(old);
        inbounds.Add(System.Text.Json.JsonSerializer.SerializeToNode(new{type="mixed",tag="direct-dns-probe",listen="127.0.0.1",listen_port=20832}));
        var rules=data["route"]!["rules"]!.AsArray();
        foreach(var old in rules.Where(n=>n?["inbound"] is System.Text.Json.Nodes.JsonArray names&&names.Any(v=>v?.GetValue<string>()=="direct-dns-probe")).ToArray())rules.Remove(old);
        rules.Insert(0,System.Text.Json.JsonSerializer.SerializeToNode(new{inbound=new[]{"direct-dns-probe"},action="reject"}));
        rules.Insert(0,System.Text.Json.JsonSerializer.SerializeToNode(new{inbound=new[]{"direct-dns-probe"},ip_cidr=new[]{"1.1.1.1/32","8.8.8.8/32"},port=new[]{443},action="route",outbound="direct"}));
    }
    internal static bool HasIpv6Gateway(IEnumerable<System.Net.IPAddress> gateways)=>gateways.Any(ip=>
        ip.AddressFamily==System.Net.Sockets.AddressFamily.InterNetworkV6 &&
        !ip.Equals(System.Net.IPAddress.IPv6Any) && !System.Net.IPAddress.IsLoopback(ip));
    internal static string[] DiagnosticRoutes(bool ipv6)=>new[]{"0.0.0.0/0",ipv6?"::/0":"fd7a:126::/126"};
    internal static void ConfigureDiagnosticTun(System.Text.Json.Nodes.JsonNode tun,bool ipv6)
    {
        tun["interface_name"]="RoutePilot-Probe";
        // StrictRoute without an IPv6 interface installs a global IPv6 WFP
        // block. Keep internal addressing; restrict the advertised routes.
        tun["address"]=System.Text.Json.JsonSerializer.SerializeToNode(new[]{"172.30.126.1/30","fd7a:126::1/126"});
        tun["route_address"]=System.Text.Json.JsonSerializer.SerializeToNode(DiagnosticRoutes(ipv6));
        var exclusions=tun["route_exclude_address"]?.AsArray()??new System.Text.Json.Nodes.JsonArray();
        if(tun["route_exclude_address"]==null)tun["route_exclude_address"]=exclusions;
        if(!exclusions.Any(value=>value?.GetValue<string>()=="fd7a:115c:a1e0::/48"))exclusions.Add("fd7a:115c:a1e0::/48");
    }
    internal static void PreserveTailscaleTransport(System.Text.Json.Nodes.JsonArray rules)
    {
        string[] processes={"tailscaled.exe","tailscale.exe","tailscale-ipn.exe"};
        int sniff=Enumerable.Range(0,rules.Count).FirstOrDefault(i=>rules[i]?["action"]?.GetValue<string>()=="sniff",rules.Count);
        if(rules.Take(sniff).Any(rule=>rule?["outbound"]?.GetValue<string>()=="direct" &&
            rule?["process_name"] is System.Text.Json.Nodes.JsonArray names && processes.All(p=>names.Any(n=>n?.GetValue<string>()==p))))return;
        rules.Insert(sniff,System.Text.Json.JsonSerializer.SerializeToNode(new{process_name=processes,action="route",outbound="direct"}));
    }
    static bool PhysicalIpv6(System.Net.NetworkInformation.NetworkInterface nic)=>HasIpv6Gateway(nic.GetIPProperties().GatewayAddresses.Select(g=>g.Address));
    internal static void PreserveTailscaleDns(System.Text.Json.Nodes.JsonNode data)
    {
        string name=System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n=>
            System.Text.RegularExpressions.Regex.IsMatch(n.Description,@"^Tailscale(?: Tunnel(?: #\d+)?)?$",System.Text.RegularExpressions.RegexOptions.IgnoreCase))?.Name??"Tailscale";
        var outbounds=data["outbounds"]!.AsArray();
        foreach(var previous in outbounds.Where(x=>x?["tag"]?.GetValue<string>()=="tailnet").ToArray())outbounds.Remove(previous);
        outbounds.Add(System.Text.Json.JsonSerializer.SerializeToNode(new{type="direct",tag="tailnet",bind_interface=name,connect_timeout="3s"}));
        var servers=data["dns"]!["servers"]!.AsArray();
        foreach(var previous in servers.Where(x=>x?["tag"]?.GetValue<string>()=="dns-tailnet").ToArray())servers.Remove(previous);
        servers.Add(System.Text.Json.JsonSerializer.SerializeToNode(new{type="udp",tag="dns-tailnet",server="100.100.100.100",server_port=53,detour="tailnet"}));
        var dnsRules=data["dns"]!["rules"]!.AsArray();
        foreach(var previous in dnsRules.Where(x=>x?["server"]?.GetValue<string>()=="dns-tailnet").ToArray())dnsRules.Remove(previous);
        dnsRules.Insert(0,System.Text.Json.JsonSerializer.SerializeToNode(new{domain_suffix=new[]{"ts.net"},action="route",server="dns-tailnet"}));
        var rules=data["route"]!["rules"]!.AsArray();
        foreach(var previous in rules.Where(x=>x?["server"]?.GetValue<string>()=="dns-tailnet"||x?["outbound"]?.GetValue<string>()=="tailnet").ToArray())rules.Remove(previous);
        int sniff=Enumerable.Range(0,rules.Count).FirstOrDefault(i=>rules[i]?["action"]?.GetValue<string>()=="sniff",rules.Count);
        int afterDns=Enumerable.Range(sniff,rules.Count-sniff).FirstOrDefault(i=>rules[i]?["action"]?.GetValue<string>()=="hijack-dns",sniff);
        rules.Insert(afterDns+1,System.Text.Json.JsonSerializer.SerializeToNode(new{domain_suffix=new[]{"ts.net"},action="resolve",server="dns-tailnet",strategy="prefer_ipv4"}));
        rules.Insert(afterDns+2,System.Text.Json.JsonSerializer.SerializeToNode(new{ip_cidr=new[]{"100.64.0.0/10","fd7a:115c:a1e0::/48"},action="route",outbound="tailnet"}));
    }
    // Fixed diagnostic topology. No caller-supplied executable, command or config is accepted.
    public static string Prepare(AgentConfig config,RunState state)
    {
        string template=Path.Combine(AppContext.BaseDirectory,"production-router.json");
        if(File.Exists(template))return PrepareTemplate(config,state,template);
        var physical=Tools.Physical(state.PhysicalInterfaceIndex);
        string folder=Tools.RunFolder(config,state.Id),nic=physical.Name;
        bool ipv6=PhysicalIpv6(physical);
        var rules=new List<object>
        {
            new{process_name=new[]{"tailscaled.exe","tailscale.exe","tailscale-ipn.exe"},action="route",outbound="direct"},
            new{action="sniff",timeout="300ms"},new{protocol="dns",action="hijack-dns"},
            new{process_name=new[]{"wiresock-connect-service.exe","wiresock-connect-app-service.exe"},action="route",outbound="direct"},
            new{domain_suffix=new[]{"cloudflare.com"},action="route",outbound="vpn"},
            new{domain_suffix=new[]{"ipify.org","yandex.ru"},action="route",outbound="direct"},
            new{rule_set=new[]{"personal-direct"},action="route",outbound="direct"},
            new{rule_set=new[]{"personal-vpn"},action="route",outbound="vpn"},
        };
        // Exact SNI/HTTP-name repair for pre-existing OpenAI hosts redirects.
        // A shared IP alone is never attributed to one of these domains.
        string hosts=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","drivers","etc","hosts");
        foreach(string line in File.ReadLines(hosts))
        foreach(string domain in line.Split('#')[0].Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Skip(1))
            if(System.Text.RegularExpressions.Regex.IsMatch(domain,@"^(?:[a-z0-9-]+\.)*(?:openai\.com|chatgpt\.com|oaistatic\.com|oaiusercontent\.com|sora\.com)$"))
                rules.Insert(3,new{domain=new[]{domain},action="route-options",override_address=domain});
        rules.Insert(3,new{inbound=new[]{"dpi-probe"},network="tcp",action="route",outbound="dpi",tls_record_fragment=true});
        rules.Insert(3,new{inbound=new[]{"direct-probe"},action="route",outbound="direct"});
        rules.Add(new{domain_suffix=new[]{"openai.com","chatgpt.com","oaistatic.com","oaiusercontent.com","sora.com"},action="route",outbound="vpn"});
        rules.Add(new{rule_set=new[]{"russia-inside","discord","telegram"},action="route",outbound="vpn"});
        rules.Add(new{ip_version=6,action="reject"});rules.Add(new{ip_cidr=new[]{"198.18.0.0/15","fc00::/18"},action="reject"});
        var local=new List<object>();
        foreach(var pair in new[]{("russia-inside","russia_inside.srs"),("discord","discord.srs"),("telegram","telegram.json"),("personal-direct","personal-direct.json"),("personal-vpn","personal-vpn.json")})
        {
            string path=Path.Combine(AppContext.BaseDirectory,"rules",pair.Item2);
            if(!File.Exists(path))throw new FileNotFoundException("Protected routing rules are missing.");
            local.Add(new{type="local",tag=pair.Item1,format=pair.Item2.EndsWith("srs")?"binary":"source",path});
        }
        string output=Path.Combine(config.DataDirectory,"router-"+state.Id+".json");
        var generated=System.Text.Json.JsonSerializer.SerializeToNode(new
        {
            log=new{level="info",timestamp=true,output=Path.Combine(config.DataDirectory,"system-router-"+state.Id+".log")},
            dns=new{servers=new object[]{
                new{type="https",tag="dns-direct",server="1.1.1.1",detour="direct"},
                new{type="https",tag="dns-vpn",server="1.1.1.1",detour="vpn"},
                new{type="fakeip",tag="fakeip",inet4_range="198.18.0.0/15",inet6_range="fc00::/18"}},
                rules=new object[]{
                    new{rule_set=new[]{"personal-direct"},action="route",server="dns-direct"},
                    new{domain_suffix=new[]{"youtube.com","cloudflare.com"},query_type=new[]{"A","AAAA"},action="route",server="fakeip"},
                    new{rule_set=new[]{"personal-vpn","russia-inside","discord"},action="route",server="dns-vpn"}},
                final="dns-direct",strategy="ipv4_only",reverse_mapping=true},
            inbounds=new object[]{
                new{type="mixed",tag="local-test",listen="127.0.0.1",listen_port=20819},
                new{type="mixed",tag="dpi-probe",listen="127.0.0.1",listen_port=20828},
                new{type="mixed",tag="direct-probe",listen="127.0.0.1",listen_port=20829},
                new{type="tun",tag="system-tun",interface_name="RoutePilot-Probe",address=new[]{"172.30.126.1/30","fd7a:126::1/126"},route_address=DiagnosticRoutes(ipv6),mtu=1400,auto_route=true,strict_route=true,stack="mixed",dns_mode="hijack",
                    route_exclude_address=new[]{"10.0.0.0/8","172.16.0.0/12","192.168.0.0/16","100.64.0.0/10","fd7a:115c:a1e0::/48","fe80::/10","224.0.0.0/4","ff00::/8",config.EndpointIp+"/32"}}},
            outbounds=new object[]{new{type="direct",tag="direct",bind_interface=nic},new{type="direct",tag="dpi",bind_interface=nic},new{type="socks",tag="vpn",server="127.0.0.1",server_port=20818,version="5"}},
            route=new{rules,rule_set=local,final="direct",default_domain_resolver="dns-direct",auto_detect_interface=true}
        })!;
        PreserveTailscaleDns(generated);PolicyDns.Configure(generated);PreserveDirectDnsProbe(generated);Storage.Write(output,generated);
        return output;
    }
    static string PrepareTemplate(AgentConfig config,RunState state,string template)
    {
        var data=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(template))!.AsObject();
        var physical=Tools.Physical(state.PhysicalInterfaceIndex);string nic=physical.Name;
        data["log"]=System.Text.Json.JsonSerializer.SerializeToNode(new{level="info",timestamp=true,output=Path.Combine(config.DataDirectory,"system-router-"+state.Id+".log")});
        foreach(var inbound in data["inbounds"]!.AsArray())
        {
            string type=inbound!["type"]!.GetValue<string>();
            if(type=="tun"){ConfigureDiagnosticTun(inbound,PhysicalIpv6(physical));}
            else if(type=="mixed" && inbound["listen"]!.GetValue<string>()=="127.0.0.1")
            {if(inbound["listen_port"]!.GetValue<int>()==20809)inbound["listen_port"]=20819;}
            else throw new InvalidDataException("Unexpected production inbound.");
        }
        foreach(var outbound in data["outbounds"]!.AsArray())
        {
            string type=outbound!["type"]!.GetValue<string>(),tag=outbound["tag"]!.GetValue<string>();
            if(type=="direct" && tag is "direct" or "dpi" or "direct-auto" or "dpi-auto")outbound["bind_interface"]=nic;
            else if(type=="socks" && tag=="vpn" && outbound["server"]!.GetValue<string>()=="127.0.0.1")outbound["server_port"]=20818;
            else if(type=="direct" && tag=="policy-proof"){}
            else if(type=="direct" && tag=="tailnet"){}
            else throw new InvalidDataException("Unexpected production outbound.");
        }
        foreach(var set in data["route"]!["rule_set"]!.AsArray())
        {
            if(set!["type"]!.GetValue<string>()!="local")throw new InvalidDataException("Only installed local rules are allowed.");
            string name=Path.GetFileName(set["path"]!.GetValue<string>());
            if(!new[]{"russia_inside.srs","discord.srs","telegram.json","personal-direct.json","personal-vpn.json","auto-direct.json","auto-dpi.json","auto-vpn.json","auto-blocked.json"}.Contains(name))throw new InvalidDataException("Unexpected production rule file.");
            set["path"]=Path.Combine(AppContext.BaseDirectory,"rules",name);
        }
        // Only the two diagnostic hostnames receive temporary FakeIP addresses.
        var dnsRules=data["dns"]!["rules"]!.AsArray();
        foreach(var rule in dnsRules.Where(r=>r?["server"]?.GetValue<string>()=="fakeip").ToArray())dnsRules.Remove(rule);
        dnsRules.Insert(0,System.Text.Json.JsonSerializer.SerializeToNode(new{domain_suffix=new[]{"youtube.com","cloudflare.com"},query_type=new[]{"A","AAAA"},action="route",server="fakeip"}));
        var routeRules=data["route"]!["rules"]!.AsArray();
        PreserveTailscaleTransport(routeRules);
        routeRules.Insert(3,System.Text.Json.JsonSerializer.SerializeToNode(new{domain_suffix=new[]{"cloudflare.com"},action="route",outbound="vpn"}));
        routeRules.Insert(3,System.Text.Json.JsonSerializer.SerializeToNode(new{domain_suffix=new[]{"ipify.org"},action="route",outbound="direct"}));
        data["experimental"]!.AsObject().Remove("cache_file");
        data["experimental"]!["clash_api"]!["external_controller"]="127.0.0.1:20820";
        PreserveTailscaleDns(data);
        PolicyDns.Configure(data);
        PreserveDirectDnsProbe(data);
        string path=Path.Combine(config.DataDirectory,"router-"+state.Id+".json");Storage.Write(path,data);return path;
    }
}
