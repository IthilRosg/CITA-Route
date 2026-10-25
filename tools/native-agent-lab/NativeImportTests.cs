using System.Text;
using RoutePilot.Agent;

internal static class NativeImportTests
{
    internal static async Task Run()
    {
        const string profile="[Interface]\nPrivateKey = sentinel-private-value\n[Peer]\nEndpoint = 203.0.113.10:3080\nAllowedIPs = 1.1.1.7/24, 10.8.0.0/16\n";
        NativeProfileImportRequest Request(string text)=>new(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),"../fixture\n");
        var parsed=await NativeProfileImport.Parse(Request(profile),CancellationToken.None);
        if(parsed.Allowed[0]!="1.1.1.0/24"||parsed.Allowed[1]!="10.8.0.0/16"||parsed.Mtu!=1400||parsed.Name.Contains('/')||parsed.Name.Contains('\n'))throw new Exception("Import normalization failed");
        int rejected=0;
        foreach(var request in new[]{new NativeProfileImportRequest("!","bad"),new NativeProfileImportRequest(new string('a',16385),"bad"),Request(profile+"Endpoint = 203.0.113.11:3080\n"),Request(profile.Replace("203.0.113.10","127.0.0.1")),Request(profile.Replace("203.0.113.10","224.0.0.1")),Request(profile.Replace("1.1.1.7/24, 10.8.0.0/16","::/0")),Request(profile.Replace("1.1.1.7/24, 10.8.0.0/16","1.1.1.7/24,1.1.1.0/24")),Request(profile+"\0"),Request(profile+"MTU = 9000\n")})
        {
            try{await NativeProfileImport.Parse(request,CancellationToken.None);throw new Exception("Unsafe input accepted");}catch(System.IO.InvalidDataException error){if(error.ToString().Contains("sentinel-private-value"))throw new Exception("Private data leaked");rejected++;}
        }
        Console.WriteLine($"PASS: native import normalization and {rejected} malformed/unsupported input cases; no networking or key output.");
    }
}
