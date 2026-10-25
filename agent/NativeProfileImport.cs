using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RoutePilot.Agent;

public sealed record NativeProfileImportRequest(string Base64,string Name,string RequestId="");

internal static class NativeProfileImport
{
    internal sealed record Input(string Config,string Name,string Endpoint,string[] Allowed,int Mtu);
    internal static async Task<Input> Parse(NativeProfileImportRequest request,CancellationToken stop)
    {
        if(request.Base64.Length>16384)throw new InvalidDataException("Native profile size refused.");
        byte[] bytes;try{bytes=Convert.FromBase64String(request.Base64);}catch{throw new InvalidDataException("Native profile encoding refused.");}
        string text;try{text=new UTF8Encoding(false,true).GetString(bytes).TrimStart('\ufeff');}finally{CryptographicOperations.ZeroMemory(bytes);}
        if(text.Length<1||text.Contains('\0'))throw new InvalidDataException("Native profile text refused.");
        var endpoints=Regex.Matches(text,@"(?mi)^\s*Endpoint\s*=\s*([^\r\n]+)");
        if(endpoints.Count!=1)throw new InvalidDataException("Exactly one native endpoint required.");
        string raw=endpoints[0].Groups[1].Value.Trim();int colon=raw.LastIndexOf(':');
        if(colon<1||!ushort.TryParse(raw[(colon+1)..],NumberStyles.None,CultureInfo.InvariantCulture,out ushort port)||port==0)throw new InvalidDataException("Native endpoint refused.");
        string host=raw[..colon];IPAddress? address;
        if(!IPAddress.TryParse(host,out address))
        {
            if(host.Length>253||Uri.CheckHostName(host)!=UriHostNameType.Dns)throw new InvalidDataException("Native endpoint hostname refused.");
            address=(await Dns.GetHostAddressesAsync(host,AddressFamily.InterNetwork,stop)).OrderBy(x=>x.ToString(),StringComparer.Ordinal).FirstOrDefault();
        }
        if(address==null||address.AddressFamily!=AddressFamily.InterNetwork||IPAddress.IsLoopback(address)||address.Equals(IPAddress.Any)||address.GetAddressBytes()[0]>=224)throw new InvalidDataException("Native endpoint IPv4 required.");
        string endpoint=address+":"+port;
        var replacement=endpoints[0].Groups[1];text=text[..replacement.Index]+endpoint+text[(replacement.Index+replacement.Length)..];
        var allowed=Regex.Matches(text,@"(?mi)^\s*AllowedIPs\s*=\s*([^\r\n]+)");
        if(allowed.Count!=1)throw new InvalidDataException("Exactly one native peer required.");
        string[] prefixes=allowed[0].Groups[1].Value.Split(',').Select(Prefix).ToArray();
        if(prefixes.Length is <1 or >64||prefixes.Distinct(StringComparer.Ordinal).Count()!=prefixes.Length)throw new InvalidDataException("Native allowed routes refused.");
        int mtu=1400;var mtus=Regex.Matches(text,@"(?mi)^\s*MTU\s*=\s*(\d+)\s*$");if(mtus.Count>0&&(!int.TryParse(mtus[0].Groups[1].Value,out mtu)||mtu is <576 or >1420))throw new InvalidDataException("Native MTU refused.");
        string name=new(request.Name.Where(c=>!char.IsControl(c)&&c!='/'&&c!='\\').Take(80).ToArray());if(string.IsNullOrWhiteSpace(name))name="Импортированный профиль";
        return new(text,name,endpoint,prefixes,mtu);
    }
    internal static string Prefix(string value)
    {
        string[] parts=value.Trim().Split('/');if(parts.Length!=2||!IPAddress.TryParse(parts[0],out var ip)||ip.AddressFamily!=AddressFamily.InterNetwork||!int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out int bits)||bits is <0 or >32)throw new InvalidDataException("Only IPv4 native peer routes are supported.");
        byte[] b=ip.GetAddressBytes();for(int i=0;i<4;i++)b[i]&=(byte)(bits>=8*(i+1)?255:bits<=8*i?0:255<<(8-(bits-8*i)));return new IPAddress(b)+"/"+bits;
    }
    static void WritePrivate(string path,string text)
    {
        var acl=new FileSecurity();acl.SetAccessRuleProtection(true,false);acl.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null));
        foreach(var sid in new[]{WellKnownSidType.LocalSystemSid,WellKnownSidType.BuiltinAdministratorsSid})acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid,null),FileSystemRights.FullControl,AccessControlType.Allow));
        using var file=new FileInfo(path).Create(FileMode.CreateNew,FileSystemRights.FullControl,FileShare.None,4096,FileOptions.WriteThrough,acl);
        byte[] bytes=Encoding.UTF8.GetBytes(text);try{file.Write(bytes);file.Flush(true);}finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    static async Task<JsonElement> Probe(string image,string mode,object spec,NativeProcessJob job,CancellationToken stop)
    {
        var info=new ProcessStartInfo(image){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add(mode);
        using var p=Process.Start(info)??throw new IOException("Native import probe unavailable.");job.Attach(p);_=p.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try{await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(spec).AsMemory(),stop);p.StandardInput.Close();var result=await NativeAwgBackend.ReadReply(p.StandardOutput,stop);await p.WaitForExitAsync(stop);if(p.ExitCode!=0||!result.GetProperty("success").GetBoolean())throw new IOException("Native import connectivity check failed.");return result;}
        finally{if(!p.HasExited){p.Kill(true);await p.WaitForExitAsync();}}
    }
    internal sealed class Prepared:IDisposable
    {
        readonly List<string> files;internal NativeTrustedBundle Bundle{get;}internal string Settings{get;}bool committed;
        internal Prepared(NativeTrustedBundle bundle,string settings,List<string> files){Bundle=bundle;Settings=settings;this.files=files;}
        internal void Commit(string root,CancellationToken stop)
        {
            string temporary=Path.Combine(root,"settings-"+Guid.NewGuid().ToString("N")+".tmp");
            try{WritePrivate(temporary,Settings);stop.ThrowIfCancellationRequested();File.Move(temporary,Path.Combine(root,"service.json"),true);committed=true;}finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        public void Dispose(){if(!committed){Bundle.Dispose();foreach(string path in files)File.Delete(path);}}
    }
    internal static async Task<Prepared> Prepare(string root,NativeTrustedBundle old,JsonElement oldPolicy,NativeProfileImportRequest request,CancellationToken stop,Action<string>? progress=null)
    {
        // Only existing protected component paths are executable. No imported
        // scripts, routes, program paths or exit expectations are trusted.
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(stop);budget.CancelAfter(TimeSpan.FromSeconds(45));stop=budget.Token;
        progress?.Invoke("ReadingProfile");Input input=await Parse(request,stop);string session=Guid.NewGuid().ToString("N"),token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        progress?.Invoke("ValidatingProfile");var spec=JsonSerializer.SerializeToElement(new{Session=session,Generation=1UL,Token=token,Config=input.Config});await NativeAwgBackend.PreflightAsync(old.OwnerPath,spec,stop);
        string exit;
        using(var job=new NativeProcessJob())
        {
            try
            {
                await using var backend=new NativeAwgBackend(old.OwnerPath,true,job.Attach);var ready=await backend.StartAsync(spec,stop);int port=ready.GetProperty("port").GetInt32();
                progress?.Invoke("ProbingHttps");var https=await Probe(old.OwnerPath,"--proxy-client",new{Port=port,Token=token,LiteralIPv4=true},job,stop);exit=https.GetProperty("egressHash").GetString()??"";
                if(exit.Length!=64||!exit.All(Uri.IsHexDigit))throw new InvalidDataException("Native import exit proof missing.");
                progress?.Invoke("ProbingUdp");await Probe(old.OwnerPath,"--proxy-udp-client",new{Port=port,Token=token,Server="1.1.1.1"},job,stop);
            }
            finally{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(8));await job.TerminateAsync(cleanup.Token);}
        }
        // Retain immutable old revisions for rollback. Bound storage rather than
        // deleting unknown or leased files to make room.
        if(Directory.EnumerateFiles(root,"catalog-*.json").Take(32).Count()>=32)throw new IOException("Native profile revision limit reached.");
        var policy=JsonSerializer.SerializeToElement(new{Vpn=input.Allowed.Append("::/0").ToArray(),Direct=oldPolicy.GetProperty("Direct").EnumerateArray().Select(x=>x.GetString()!).Append("255.255.255.255/32").Distinct().ToArray()});
        progress?.Invoke("CompilingPolicy");var guard=new NativeWfpGuard(old.GuardPath,Guid.NewGuid().ToString("N"),policy);await guard.PlanAsync(stop);
        string id=Guid.NewGuid().ToString("N");var files=new List<string>();NativeTrustedBundle? candidate=null;
        string Add(string name,string text){string path=Path.Combine(root,name);files.Add(path);WritePrivate(path,text);return name;}
        NativeTrustedBundle.Entry Entry(string name){using var stream=File.OpenRead(Path.Combine(root,name));return new(name,Convert.ToHexString(SHA256.HashData(stream)));}
        try
        {
            progress?.Invoke("WritingSnapshot");
            string profile=Add("profile-"+id+".conf",input.Config),policyFile=Add("policy-"+id+".json",policy.GetRawText());
            var core=JsonNode.Parse(NativeTrustedBundle.ReadProtectedText(old.CoreConfigPath))!;var inbound=core["inbounds"]!.AsArray().Single(x=>x?["type"]?.GetValue<string>()=="tun")!;
            inbound["route_address"]=JsonSerializer.SerializeToNode(guard.Prefixes.Where(x=>!x.Contains(':')).ToArray());inbound["route_exclude_address"]=JsonSerializer.SerializeToNode(new[]{input.Endpoint[..input.Endpoint.LastIndexOf(':')]+"/32"});inbound["mtu"]=input.Mtu;
            core["outbounds"]!.AsArray().Single(x=>x?["type"]?.GetValue<string>()=="cita_native_lab")!["owner"]=Guid.NewGuid().ToString("N");
            string coreFile=Add("core-"+id+".json",core.ToJsonString());
            var catalog=new NativeTrustedBundle.Catalog(1,Entry(Path.GetFileName(old.OwnerPath)),Entry(Path.GetFileName(old.CorePath)),Entry(Path.GetFileName(old.GuardPath)),Entry(policyFile),Entry(profile),Entry(coreFile),exit,input.Name);
            string catalogName=Add("catalog-"+id+".json",JsonSerializer.Serialize(catalog));string hash=Entry(catalogName).Sha256;
            candidate=NativeTrustedBundle.Open(root,hash,catalogName);return new(candidate,JsonSerializer.Serialize(new{Enabled=true,CatalogSha256=hash,CatalogName=catalogName}),files);
        }
        catch{candidate?.Dispose();foreach(string path in files)if(File.Exists(path))File.Delete(path);throw;}
    }
}
