using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cita.Diagnostics;

public static partial class SessionEvidence
{
    static void ExportWebChecks(Action<string,string> entry,string root)
    {
        foreach(string kind in new[]{"site","resource"})
        {
            var check=Read(Path.Combine(root,"logs",kind+"-check-last.json"));
            var host=Get(check,"domain");string? domain=host.ValueKind==JsonValueKind.String?host.GetString()?.ToLowerInvariant():null;
            if(domain!=null&&(domain.Length>253||Uri.CheckHostName(domain)!=UriHostNameType.Dns||!Regex.IsMatch(domain,@"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9-]{2,63}$")))domain=null;
            if(domain==null)continue;
            long? Bounded(JsonElement parent,string property,long maximum)
            {var value=Get(parent,property);return value.ValueKind==JsonValueKind.Number&&value.TryGetInt64(out long number)&&number>=0&&number<=maximum?number:null;}
            bool Flag(JsonElement parent,string name)=>Get(parent,name).ValueKind==JsonValueKind.True;
            string? SafeCode(JsonElement parent,string name)
            {var value=Get(parent,name);string? code=value.ValueKind==JsonValueKind.String?value.GetString():null;return code!=null&&Regex.IsMatch(code,"^[A-Za-z][A-Za-z0-9]{0,63}$")?code:null;}
            var raw=Get(check,"attempts");var attempts=raw.ValueKind==JsonValueKind.Array?raw.EnumerateArray().Take(6).ToArray():Array.Empty<JsonElement>();
            string? hash=Get(check,"pathHash").ValueKind==JsonValueKind.String?Get(check,"pathHash").GetString():null;
            entry("checks/"+kind+".json",Redact(JsonSerializer.Serialize(new{domain,pathHash=hash!=null&&Regex.IsMatch(hash,"^[A-F0-9]{12}$")?hash:null,
                sessionId=Id(check,"sessionId"),bridgeGeneration=Bounded(check,"bridgeGeneration",int.MaxValue),finishedUtc=At(check,"finishedUtc"),
                route=SafeCode(check,"route"),outcome=SafeCode(check,"outcome"),attempts=attempts.Select(a=>new{
                    route=SafeCode(a,"route"),status=Bounded(a,"status",599)??Bounded(a,"HttpStatus",599),bytes=Bounded(a,"bytes",524288),expectedBytes=Bounded(a,"expectedBytes",1L<<40),
                    complete=Flag(a,"complete"),capped=Flag(a,"capped"),reachable=Flag(a,"Reachable"),state=SafeCode(a,"state"),error=SafeCode(a,"error"),milliseconds=Bounded(a,"milliseconds",60000)??Bounded(a,"Milliseconds",60000)}),
                scope="Projected explicit own diagnostic; compare timestamp/session; no resource path, content, credentials or user request replay"}),root));
        }
    }
}
