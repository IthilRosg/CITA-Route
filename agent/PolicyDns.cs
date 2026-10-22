using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoutePilot.Agent;

internal sealed record PolicyResolver(string Host,string Path,IPAddress Address);
internal sealed record PolicyDnsAnswer(IPAddress[]? Addresses,bool Terminal);
internal static class PolicyDns
{
    internal static readonly PolicyResolver[] Resolvers={
        new("1.1.1.1","/dns-query?type=A&name=",IPAddress.Parse("1.1.1.1")),
        new("dns.google","/resolve?type=A&name=",IPAddress.Parse("8.8.8.8"))
    };
    internal static PolicyDnsAnswer Parse(ReadOnlyMemory<byte> body)
    {
        using var json=JsonDocument.Parse(body);
        int status=json.RootElement.GetProperty("Status").GetInt32();
        if(status is 2 or 5)return new(null,false);
        if(status!=0 || !json.RootElement.TryGetProperty("Answer",out var answers))return new(null,true);
        var addresses=answers.EnumerateArray().Where(a=>a.GetProperty("type").GetInt32()==1)
            .Select(a=>IPAddress.Parse(a.GetProperty("data").GetString()!)).Distinct().ToArray();
        return new(addresses.Length>0&&addresses.All(PolicySafety.Public)?addresses:null,true);
    }
    internal static JsonObject[] Servers()
    {
        var servers=new List<JsonObject>();
        foreach(string route in new[]{"direct","vpn"})
        {
            string tag="dns-"+route;
            servers.Add(new(){["type"]="https",["tag"]=tag+"-primary",["server"]="1.1.1.1",["detour"]=route});
            servers.Add(new(){["type"]="https",["tag"]=tag+"-backup",["server"]="8.8.8.8",["detour"]=route,["tls"]=new JsonObject{["enabled"]=true,["server_name"]="dns.google"}});
            servers.Add(new(){["type"]="cita-fallback",["tag"]=tag,["servers"]=new JsonArray(tag+"-primary",tag+"-backup"),["timeout"]="1500ms"});
        }
        return servers.ToArray();
    }
    internal static void Configure(JsonNode data)
    {
        var servers=data["dns"]!["servers"]!.AsArray();
        var names=new HashSet<string>(Servers().Select(s=>s["tag"]!.GetValue<string>()));
        foreach(var previous in servers.Where(s=>s?["tag"]?.GetValue<string>() is string tag&&names.Contains(tag)).ToArray())servers.Remove(previous);
        foreach(var server in Servers())servers.Add(server);
    }
}
