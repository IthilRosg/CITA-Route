using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using RoutePilot.Agent;

internal static class ServersLab
{
    static JsonElement Spec(string config,ulong generation)=>JsonSerializer.SerializeToElement(new{Session=Guid.NewGuid().ToString("N"),Generation=generation,Token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),Config=config});
    static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    internal static async Task<int> Run(string root)
    {
        var observations=new List<object>();bool completed=false;string? errorType=null;string stage="prepare";
        try
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(210));var stop=deadline.Token;string image=Path.Combine(root,"native-awg-lab.exe");
            string a=await File.ReadAllTextAsync(Path.Combine(root,"private","peer.conf"),stop),b=await File.ReadAllTextAsync(Path.Combine(root,"private","second-server.conf"),stop);
            string Field(string config,string field)=>Regex.Match(config,@"(?mi)^\s*"+field+@"\s*=\s*([^\r\n]+)").Groups[1].Value.Trim();
            string hostA=Field(a,"Endpoint").Split(':')[0],hostB=Field(b,"Endpoint").Split(':')[0];
            if(hostA==hostB||Field(a,"PublicKey")==Field(b,"PublicKey"))throw new InvalidDataException("Distinct servers required");
            await using var profiles=new NativeAwgProfiles(image);
            ulong generation=0;
            foreach(var item in new[]{("A",a,true),("B",b,true),("wrong-server-key",Regex.Replace(b,@"(?mi)^PublicKey\s*=.*$","PublicKey = "+Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),false),("unreachable",Regex.Replace(b,@"(?mi)^Endpoint\s*=.*$","Endpoint = 192.0.2.1:9"),false),("A-restored",a,true)})
            {
                stage=item.Item1;var spec=Spec(item.Item2,++generation);var ready=await profiles.SwitchAsync(spec,stop);int port=ready.GetProperty("port").GetInt32();string token=spec.GetProperty("Token").GetString()!;
                var https=await StartupLab.Probe(image,"--proxy-client",new{Port=port,Token=token,LiteralIPv4=true},stop);
                var udp=await StartupLab.Probe(image,"--proxy-udp-client",new{Port=port,Token=token,Server=Field(item.Item2,"DNS").Split(',')[0].Trim()},stop);
                bool passed=https.GetProperty("success").GetBoolean()&&udp.GetProperty("success").GetBoolean();bool blocked=!https.GetProperty("success").GetBoolean()&&!udp.GetProperty("success").GetBoolean();
                observations.Add(new{name=item.Item1,generation,expectedReachable=item.Item3,https,udp,passed= item.Item3?passed:blocked});
                if(item.Item3?!passed:!blocked)throw new IOException("Server matrix mismatch");
            }
            observations.Add(new{distinctServerHosts=true,distinctServerKeys=true,hostAHash=Hash(hostA),hostBHash=Hash(hostB)});completed=true;
        }
        catch(Exception e){errorType=e.GetType().Name;}
        Console.WriteLine(JsonSerializer.Serialize(new{schema=1,completed,stage,scope="Distinct servers and keys, fresh sequential backend leases; no TUN or OS fallback claim",observations,errorType}));return completed?0:1;
    }
}
