using System.Text.Json.Nodes;

namespace RoutePilot.Agent;

// Native net.Dialer timeout, not a TLS/HTTP deadline or a selector race.
// Kept opt-in until slow-network and physical WireSock acceptance tests pass.
internal static class PolicyTcpBudget
{
    internal static string? CanonicalRoute(string? tag)=>tag switch{"direct" or "direct-auto"=>"direct","dpi" or "dpi-auto"=>"dpi","vpn"=>"vpn",_=>null};
    internal static void Configure(JsonNode config,PolicyOptions options)
    {
        bool enabled=options.Mode=="Auto"&&options.ExperimentalFastTcp;
        var outbounds=config["outbounds"]!.AsArray();
        foreach(var old in outbounds.Where(o=>o?["tag"]?.GetValue<string>() is "direct-auto" or "dpi-auto").ToArray())outbounds.Remove(old);
        foreach(var outbound in outbounds.ToArray())
            if(outbound?["type"]?.GetValue<string>()=="direct"&&outbound["tag"]?.GetValue<string>() is "direct" or "dpi")
            {
                if(options.Mode=="Auto")outbound["connect_timeout"]="3s";
                if(enabled){var copy=outbound.DeepClone();copy["tag"]=outbound["tag"]!.GetValue<string>()+"-auto";copy["connect_timeout"]="1s";outbounds.Add(copy);}
            }
        bool Contains(JsonNode node,string property,string value)=>
            node[property] is JsonArray values&&values.Any(v=>v?.GetValue<string>()==value)||
            node["rules"] is JsonArray children&&children.Any(c=>c!=null&&Contains(c,property,value));
        foreach(var rule in config["route"]?["rules"]?.AsArray()??new JsonArray())
        {
            if(rule?["action"]?.GetValue<string>()!="route")continue;
            string? tag=rule["outbound"]?.GetValue<string>();
            string? original=tag switch{"direct-auto"=>"direct","dpi-auto"=>"dpi",_=>tag};
            if(original is not ("direct" or "dpi"))continue;
            bool eligible=Contains(rule,"inbound",original+"-probe")||Contains(rule,"rule_set","auto-"+original);
            rule["outbound"]=enabled&&eligible?original+"-auto":original;
        }
    }
}
