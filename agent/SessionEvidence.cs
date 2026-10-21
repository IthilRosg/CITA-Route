using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cita.Diagnostics;

// Shared by the service and the UI. Never read connection configuration or
// serialize arbitrary runtime objects: this is an explicit diagnostic allowlist.
public static class SessionEvidence
{
    const int MaxHistory=2048,MaxJson=262144,LogTail=131072;
    static readonly JsonSerializerOptions Json=new(){PropertyNameCaseInsensitive=true};
    static readonly Dictionary<string,(string Fingerprint,DateTime At)> Last=new(StringComparer.OrdinalIgnoreCase);
    public static string DirectoryPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RoutePilot");
    static bool Regular(string path)
    {
        for(string? item=Path.GetFullPath(path);item!=null;item=Path.GetDirectoryName(item))
            if((File.Exists(item)||Directory.Exists(item))&&(File.GetAttributes(item)&FileAttributes.ReparsePoint)!=0)return false;
        return true;
    }
    static JsonElement Read(string path,int limit=MaxJson)
    {
        try
        {
            if(!Regular(path))return default;
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(stream.Length>limit)return default;
            byte[] bytes=new byte[(int)stream.Length];stream.ReadExactly(bytes);
            using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=24});return document.RootElement.Clone();
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException){return default;}
    }
    static JsonElement Get(JsonElement value,string key)
    {
        if(value.ValueKind==JsonValueKind.Object)
            foreach(var property in value.EnumerateObject())if(property.Name.Equals(key,StringComparison.OrdinalIgnoreCase))return property.Value;
        return default;
    }
    static string Text(JsonElement value,string key)=>Get(value,key).ValueKind==JsonValueKind.String?Get(value,key).GetString()??"":"";
    static string Code(JsonElement value,string key)
    {string s=Text(value,key);return Regex.IsMatch(s,@"\A[A-Za-z][A-Za-z0-9_. -]{0,95}\z")?s:"";}
    static string Id(JsonElement value,string key)=>Guid.TryParse(Text(value,key),out var id)?id.ToString("N"):"";
    static bool Flag(JsonElement value,string key)=>Get(value,key).ValueKind==JsonValueKind.True;
    static int Number(JsonElement value,string key)=>Get(value,key).TryInt(out int n)?n:0;
    static DateTime? At(JsonElement value,string key)=>DateTime.TryParse(Text(value,key),null,System.Globalization.DateTimeStyles.RoundtripKind,out var at)?at.ToUniversalTime():null;
    static bool Fresh(DateTime? at,DateTime now,int seconds)=>at!=null&&at<=now.AddSeconds(2)&&at>now.AddSeconds(-seconds);
    static string Line(JsonElement value,string key,string root)
    {string clean=Redact(Text(value,key),root);return clean[..Math.Min(512,clean.Length)];}
    static bool TryInt(this JsonElement value,out int n){n=0;return value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out n);}
    static bool ProcessMatches(int pid,string path,DateTime? started)
    {
        if(pid<=0||started==null)return false;
        try{using var p=Process.GetProcessById(pid);return !p.HasExited&&p.MainModule?.FileName?.Equals(path,StringComparison.OrdinalIgnoreCase)==true&&Math.Abs((p.StartTime.ToUniversalTime()-started.Value).TotalMilliseconds)<100;}
        catch(Exception ex)when(ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException){return false;}
    }
    public static object Snapshot(string root,string source,DateTime now)
        =>Snapshot(root,source,now,true);
    static object Snapshot(string root,string source,DateTime now,bool retrySnapshot)
    {
        string Runtime(string name)=>Path.Combine(root,"runtime",name);
        var session=Read(Runtime("session.json"));var watch=Read(Runtime("watch-state.json"));var health=Read(Runtime("session-health.json"));
        var policy=Read(Runtime("policy-state.json"));var hold=Read(Runtime("bridge-safety.json"));
        var retry=Read(Runtime("component-recovery.json"));
        string id=Id(session,"id");int generation=Number(session,"bridgeGeneration");
        bool sameHealth=id!=""&&Id(health,"sessionId")==id&&Number(health,"bridgeGeneration")==generation;
        bool sameWatch=id!=""&&Id(watch,"sessionId")==id;
        bool held=id!=""&&Id(hold,"sessionId")==id&&Flag(hold,"hold");
        bool listener=false;try{listener=IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p=>p.Address.Equals(System.Net.IPAddress.Loopback)&&p.Port==20808);}catch(NetworkInformationException){}
        var current=Read(Runtime("session.json"));bool consistent=Id(current,"id")==id&&Number(current,"bridgeGeneration")==generation&&Flag(current,"running")==Flag(session,"running");
        if(!consistent&&retrySnapshot)return Snapshot(root,source,now,false);
        return new{source,observedUtc=now,consistent,version=typeof(SessionEvidence).Assembly.GetName().Version?.ToString(3),sessionId=id,running=Flag(session,"running"),recovering=Flag(session,"recovering"),bridgeGeneration=generation,
            bridgeReady=Flag(session,"bridgeReady"),stopReason=Code(session,"stopReason"),routerPid=Number(session,"routerPid"),bridgePid=Number(session,"bridgePid"),
            routerOwned=ProcessMatches(Number(session,"routerPid"),Path.Combine(root,"runtime","smart-router.exe"),At(session,"routerStartedUtc")),
            bridgeOwned=ProcessMatches(Number(session,"bridgePid"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RoutePilot","Agent","vpn-bridge.exe"),At(session,"bridgeStartedUtc")),
            loopbackBridgeListener=listener,startedUtc=At(session,"startedUtc"),stoppedUtc=At(session,"stoppedUtc"),
            watch=new{matchesSession=sameWatch,fresh=sameWatch&&Fresh(At(watch,"updatedUtc"),now,25),phase=Code(watch,"phase"),reason=Line(watch,"reason",root),wireSockState=Code(watch,"wireSockState"),updatedUtc=At(watch,"updatedUtc")},
            health=new{matchesGeneration=sameHealth,fresh=sameHealth&&Fresh(At(health,"updatedUtc"),now,90),healthy=consistent&&Flag(session,"running")&&Flag(session,"bridgeReady")&&sameHealth&&Fresh(At(health,"updatedUtc"),now,90)&&Flag(health,"healthy")&&!held,code=Code(health,"code"),updatedUtc=At(health,"updatedUtc")},
            safety=new{hold=held,code=held?Code(hold,"code"):""},
            policy=new{phase=Code(policy,"phase"),fresh=Fresh(At(policy,"updatedUtc"),now,45),pending=Number(policy,"pending"),queued=Number(policy,"queued"),switches=Number(policy,"switches")},
            retry=new{sessionId=Id(retry,"sessionId"),reconnect=Number(retry,"Reconnect"),repairVpn=Number(retry,"RepairVpn"),bridge=Number(retry,"Bridge"),policy=Number(retry,"Policy")}};
    }
    static string Fingerprint(JsonElement value)
    {
        using var memory=new MemoryStream();using(var writer=new Utf8JsonWriter(memory))WriteFingerprint(writer,value);
        return Convert.ToHexString(SHA256.HashData(memory.ToArray()));
    }
    static void WriteFingerprint(Utf8JsonWriter writer,JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.Object){value.WriteTo(writer);return;}
        writer.WriteStartObject();foreach(var p in value.EnumerateObject())
        {if(p.Name is "observedUtc" or "updatedUtc")continue;writer.WritePropertyName(p.Name);WriteFingerprint(writer,p.Value);}writer.WriteEndObject();
    }
    public static void Observe(string root,string source,string directory,DateTime now)
    {
        string path=Path.Combine(directory,"session-evidence-"+source+".jsonl");
        if(source is not ("ordinary" or "startup")||!Regular(directory)||!Regular(path))return;
        var snapshot=JsonSerializer.SerializeToElement(Snapshot(root,source,now));
        string fingerprint=Fingerprint(snapshot);
        lock(Last)
        {
            if(Last.TryGetValue(path,out var last)&&last.Fingerprint==fingerprint&&last.At<=now&&now-last.At<TimeSpan.FromSeconds(60))return;
            Directory.CreateDirectory(directory);
            if(File.Exists(path)&&new FileInfo(path).Length>4*1024*1024)
            {
                string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                try{File.WriteAllLines(temporary,History(path,now).Select(x=>JsonSerializer.Serialize(SafeObservation(x))),new UTF8Encoding(false));File.Move(temporary,path,true);}
                finally{if(File.Exists(temporary))File.Delete(temporary);}
            }
            string prefix="";
            if(File.Exists(path))
            {using var check=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);if(check.Length>0){check.Position=check.Length-1;if(check.ReadByte()!=10)prefix="\n";}}
            File.AppendAllText(path,prefix+JsonSerializer.Serialize(snapshot)+"\n",new UTF8Encoding(false));
            if(Last.Count>16)Last.Clear();Last[path]=(fingerprint,now);
        }
    }
    static List<JsonElement> History(string path,DateTime now)
    {
        var items=new List<JsonElement>();
        try
        {
            if(!Regular(path))return items;
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            int count=(int)Math.Min(stream.Length,4*1024*1024);long offset=Math.Max(0,stream.Length-count);stream.Position=offset;
            byte[] bytes=new byte[count];int used=0;while(used<count){int n=stream.Read(bytes,used,count-used);if(n==0)break;used+=n;}
            string[] lines=Encoding.UTF8.GetString(bytes,0,used).Split('\n');
            foreach(string line in lines.Skip(offset>0?1:0).TakeLast(MaxHistory+1))
                try{if(line.Length>8192||line.Length==0)continue;using var doc=JsonDocument.Parse(line,new JsonDocumentOptions{MaxDepth=12});var value=doc.RootElement;
                    if(At(value,"observedUtc") is {} at&&at>now.AddHours(-48)&&at<=now.AddSeconds(2))items.Add(value.Clone());}catch(JsonException){}
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
        return items.TakeLast(MaxHistory).ToList();
    }
    public static string Redact(string text,string root)
    {
        text=Regex.Replace(text,@"(?im)^.*(?:privatekey|presharedkey|publickey|authorization|password|controller-token|access[_-]?token|secret)\s*[:=].*$","[sensitive field omitted]");
        text=Regex.Replace(text,@"[A-Za-z0-9+/]{43}=","[key omitted]");
        text=Regex.Replace(text,@"\b[a-fA-F0-9]{64}\b","[identifier omitted]");
        text=Regex.Replace(text,@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b","[token omitted]");
        text=Regex.Replace(text,@"(?i)(https?://[^\s?\""<>]+)\?[^\s\""<>]*","$1?[query omitted]");
        if(root.Length>0)text=text.Replace(root,"[client]",StringComparison.OrdinalIgnoreCase);
        string profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if(profile.Length>0)text=text.Replace(profile,"[user]",StringComparison.OrdinalIgnoreCase);
        text=Regex.Replace(text,@"(?i)\b[a-z0-9.-]+\.ts\.net\b","[tailnet name]");
        return text;
    }
    static string Tail(string path,string root)
    {
        if(!Regular(path))return "[reparse point omitted]";
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        int first=stream.ReadByte(),second=stream.ReadByte();bool unicode=first==255&&second==254;
        // Read a finite snapshot even when the writer is appending continuously.
        int count=(int)Math.Min(stream.Length,LogTail);long offset=Math.Max(0,stream.Length-count);
        if(unicode&&offset%2!=0){offset++;count--;}
        stream.Position=offset;byte[] bytes=new byte[count];
        int read=0;while(read<count){int n=stream.Read(bytes,read,count-read);if(n==0)break;read+=n;}
        string text=unicode?Encoding.Unicode.GetString(bytes,0,read):Encoding.UTF8.GetString(bytes,0,read);
        return Redact(text,root);
    }
    public static void Export(string root,string destination,string? directory=null)
    {
        directory??=DirectoryPath;
        // CreateNew prevents replacing an existing or linked destination.
        using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        using var zip=new ZipArchive(output,ZipArchiveMode.Create);
        void Entry(string name,string content){using var writer=new StreamWriter(zip.CreateEntry(name,CompressionLevel.Optimal).Open(),new UTF8Encoding(false));writer.Write(content);}
        var now=DateTime.UtcNow;
        Entry("report.json",JsonSerializer.Serialize(new{schema=1,version=typeof(SessionEvidence).Assembly.GetName().Version?.ToString(3),createdUtc=now,
            installedAgentVersion=AgentVersion(),
            retention="Export: latest 2048 observations per source within 48 hours; local journal capped near 4 MiB per source. Log tails: 128 KiB per file.",
            ordinary=Snapshot(root,"ordinary",now),startup=Snapshot(Cita.Startup.StartupPaths.Client,"startup",now),resources=ResourceSummary(Read(Path.Combine(directory,"resource-state.json"))),network=NetworkSummary()},new JsonSerializerOptions{WriteIndented=true}));
        foreach(string source in new[]{"ordinary","startup"})
        {
            var history=History(Path.Combine(directory,"session-evidence-"+source+".jsonl"),now);
            if(history.Count>0)
            {
                // Only our schema fields can leave the machine, even if the local
                // journal was replaced by a different version or an untrusted file.
                Entry("history/"+source+".json",Redact(JsonSerializer.Serialize(history.Select(SafeObservation)),root));
            }
        }
        var recovery=Get(Read(Path.Combine(root,"data","recovery-events.json")),"events");
        if(recovery.ValueKind==JsonValueKind.Array)
            Entry("history/recovery-events.json",JsonSerializer.Serialize(recovery.EnumerateArray().TakeLast(64).Select(e=>new{sessionId=Id(e,"sessionId"),phase=Code(e,"phase"),reason=Line(e,"reason",root),wireSockState=Code(e,"wireSockState"),attempts=Number(e,"attempts"),utc=At(e,"utc")})));
        string[] names={"lifecycle-events.jsonl","watch-error.log","policy-error.log","auto-reconnect-error.log","recovery.log","recovery-error.log","router.log","router.previous.log","bridge.log","smart-router-startup.log","last-action.log","connection-lost.txt","app-error.log"};
        foreach(string name in names)
            try{string path=Path.Combine(root,"logs",name);if(File.Exists(path))Entry("logs/"+name,Tail(path,root));}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){Entry("logs/"+name+".unavailable.txt","Journal unavailable: "+ex.GetType().Name);}
        foreach(string name in new[]{"service-error.log","boot-error.log"})
            try{string path=Path.Combine(directory,name);if(File.Exists(path))Entry("service/"+name,Tail(path,root));}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
        Entry("README.txt","CITA Route diagnostic snapshot. No VPN configuration, private keys or controller files are collected. Log tails may contain site and application names. Times in state/history are UTC. A listener alone is not proof of VPN access; compare session/generation and HTTPS freshness. Missing history can mean the service predates this release. Collection does not connect, disconnect or repair the network.");
    }
    static object SafeObservation(JsonElement item)
    {
        // Re-project every field on export; never trust a saved journal wholesale.
        var watch=Get(item,"watch");var health=Get(item,"health");var safety=Get(item,"safety");var policy=Get(item,"policy");var retry=Get(item,"retry");
        return new{source=Code(item,"source"),observedUtc=At(item,"observedUtc"),consistent=Flag(item,"consistent"),version=Version.TryParse(Text(item,"version"),out var version)?version.ToString():"",sessionId=Id(item,"sessionId"),running=Flag(item,"running"),recovering=Flag(item,"recovering"),bridgeGeneration=Number(item,"bridgeGeneration"),bridgeReady=Flag(item,"bridgeReady"),stopReason=Code(item,"stopReason"),routerPid=Number(item,"routerPid"),bridgePid=Number(item,"bridgePid"),routerOwned=Flag(item,"routerOwned"),bridgeOwned=Flag(item,"bridgeOwned"),loopbackBridgeListener=Flag(item,"loopbackBridgeListener"),startedUtc=At(item,"startedUtc"),stoppedUtc=At(item,"stoppedUtc"),
            watch=new{matchesSession=Flag(watch,"matchesSession"),fresh=Flag(watch,"fresh"),phase=Code(watch,"phase"),reason=Line(watch,"reason",""),wireSockState=Code(watch,"wireSockState"),updatedUtc=At(watch,"updatedUtc")},
            health=new{matchesGeneration=Flag(health,"matchesGeneration"),fresh=Flag(health,"fresh"),healthy=Flag(health,"healthy"),code=Code(health,"code"),updatedUtc=At(health,"updatedUtc")},
            safety=new{hold=Flag(safety,"hold"),code=Code(safety,"code")},policy=new{phase=Code(policy,"phase"),fresh=Flag(policy,"fresh"),pending=Number(policy,"pending"),queued=Number(policy,"queued"),switches=Number(policy,"switches")},retry=new{sessionId=Id(retry,"sessionId"),reconnect=Number(retry,"reconnect"),repairVpn=Number(retry,"repairVpn"),bridge=Number(retry,"bridge"),policy=Number(retry,"policy")}};
    }
    static object ResourceSummary(JsonElement value)
    {
        long Long(JsonElement item,string key)=>Get(item,key).ValueKind==JsonValueKind.Number&&Get(item,key).TryGetInt64(out long number)?number:0;
        var sample=Get(value,"sample");var processes=Get(sample,"processes");
        return new{updatedUtc=At(value,"updatedUtc"),memoryAvailable=Flag(sample,"memoryAvailable"),physicalAvailable=Long(sample,"physicalAvailable"),commitTotal=Long(sample,"commitTotal"),commitLimit=Long(sample,"commitLimit"),
            components=processes.ValueKind==JsonValueKind.Array?processes.EnumerateArray().Where(p=>Flag(p,"citaComponent")).Take(16).Select(p=>new{pid=Number(p,"pid"),startedUtc=At(p,"startedUtc"),privateBytes=Long(p,"privateBytes"),handles=Number(p,"handles"),udpPorts=Number(p,"udpPorts")}).ToArray():null};
    }
    static string AgentVersion()
    {
        try{return FileVersionInfo.GetVersionInfo(Path.Combine(Cita.Startup.StartupPaths.AgentDirectory,"RoutePilot.Agent.exe")).FileVersion??"";}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){return "Unavailable";}
    }
    static object NetworkSummary()
    {
        var adapters=new List<object>();bool available=true,nrptAvailable=true;int quadPolicies=0;bool catchAll=false;
        try
        {
            foreach(var adapter in NetworkInterface.GetAllNetworkInterfaces().Take(64))
            {
                var ip=adapter.GetIPProperties();bool tail=Regex.IsMatch(adapter.Description,@"^Tailscale(?: Tunnel(?: #\d+)?)?$",RegexOptions.IgnoreCase);
                adapters.Add(new{kind=adapter.NetworkInterfaceType.ToString(),up=adapter.OperationalStatus==OperationalStatus.Up,tailscale=tail,
                    ipv4Gateway=ip.GatewayAddresses.Any(g=>g.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork),
                    ipv6Gateway=ip.GatewayAddresses.Any(g=>g.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetworkV6),
                    quadDns=ip.DnsAddresses.Any(d=>d.ToString() is "100.100.100.100" or "fd7a:115c:a1e0::53")});
            }
        }catch(NetworkInformationException){available=false;}
        foreach(string key in new[]{@"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\DnsPolicyConfig",@"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig"})
        try
        {
            using var policies=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
            if(policies==null)continue;
            foreach(string name in policies.GetSubKeyNames().Take(256))
            {
                using var rule=policies.OpenSubKey(name);if(rule==null)continue;
                string servers=rule.GetValue("GenericDNSServers") as string??rule.GetValue("NameServers") as string??"";
                if(!servers.Contains("100.100.100.100")&&!servers.Contains("fd7a:115c:a1e0::53",StringComparison.OrdinalIgnoreCase))continue;
                quadPolicies++;object? names=rule.GetValue("Name");
                catchAll|=names is string[] list&&list.Contains(".")||names is string single&&single==".";
            }
        }catch(Exception ex)when(ex is UnauthorizedAccessException or System.Security.SecurityException or IOException){nrptAvailable=false;}
        // Counts and capabilities only: no adapter aliases, addresses, tailnet
        // suffixes, user/device identities or registry contents leave the machine.
        return new{available,adapters,nrptAvailable,quad100Policies=quadPolicies,quad100CatchAll=catchAll};
    }
}
