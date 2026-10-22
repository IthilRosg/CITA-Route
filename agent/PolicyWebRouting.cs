using System.Text.Json.Nodes;

namespace RoutePilot.Agent;

internal static class PolicyWebRouting
{
    // protocol belongs to a route rule, not a headless rule set. Both guards
    // are needed: never turn UDP/443 games or voice traffic into a TCP rule.
    internal static JsonObject ProtocolGuard()=>new(){["type"]="logical",["mode"]="or",["rules"]=new JsonArray(new JsonObject{["network"]="tcp"},new JsonObject{["protocol"]="quic"})};
    internal static bool Blocked(JsonNode? node)=>node?["action"]?.GetValue<string>()=="reject"&&node["type"]?.GetValue<string>()=="logical"&&node["mode"]?.GetValue<string>()=="and"&&node["rules"] is JsonArray rules&&rules.Any(r=>r?["rule_set"] is JsonArray sets&&sets.Any(s=>s?.GetValue<string>()=="auto-blocked"));
    internal static bool Guarded(JsonNode? node)=>Blocked(node)&&node!["rules"]!.AsArray().Any(r=>r?["type"]?.GetValue<string>()=="logical"&&r["mode"]?.GetValue<string>()=="or"&&r["rules"] is JsonArray children&&children.Count==2&&
        children.Any(c=>c is JsonObject obj&&obj.Count==1&&c["network"]?.GetValue<string>()=="tcp")&&children.Any(c=>c is JsonObject obj&&obj.Count==1&&c["protocol"]?.GetValue<string>()=="quic"));
    internal static void Configure(JsonNode config)
    {
        foreach(var rule in config["route"]?["rules"]?.AsArray()??new JsonArray())
            if(Blocked(rule)){if(!Guarded(rule))rule!["rules"]!.AsArray().Add(ProtocolGuard());rule!["no_drop"]=true;}
    }
    internal static bool Supported(string root)
    {
        // Fail closed on an old/mixed installed topology. A new worker alone
        // must not put UDP entries into the old, unguarded rejection rule.
        bool found=false;
        try
        {
            foreach(string name in new[]{"router.json","router-proxy.json"})
            {
                string path=Path.Combine(root,"runtime",name);if(!File.Exists(path))continue;
                var config=JsonNode.Parse(File.ReadAllText(path));
                if(config?["route"]?["rules"] is not JsonArray rules||!rules.Any(Guarded))return false;
                found=true;
            }
        }
        catch(Exception ex)when(ex is IOException or System.Text.Json.JsonException or InvalidOperationException){return false;}
        return found;
    }
    internal static JsonObject TcpFallback(PolicyKey key)
    {
        var rule=PolicyPublisher.Rule(key);rule["network"]="udp";return rule;
    }
}
