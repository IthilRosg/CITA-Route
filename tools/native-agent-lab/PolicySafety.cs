using System.Net;
using System.Text.RegularExpressions;
namespace RoutePilot.Agent;
// Isolated test build: no trace collection is enabled by the lab runner.
internal static class PolicySafety
{
    internal static string? Domain(string value)
    {
        try{value=new System.Globalization.IdnMapping().GetAscii(value.Trim().TrimEnd('.')).ToLowerInvariant();
            if(value.Length>253||IPAddress.TryParse(value,out _)||!Regex.IsMatch(value,@"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9-]{2,63}$"))return null;
            return value.EndsWith(".local")||value.EndsWith(".lan")||value.EndsWith(".home.arpa")?null:value;
        }catch(ArgumentException){return null;}
    }
}
