using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cita.NativeUpdater;

internal sealed record UpgradePlan(int Schema,string Run,string OwnerSid,string Boot,DateTimeOffset DeadlineUtc,string HelperSha,string IntentSha,Dictionary<string,string> Files);
internal sealed record ProcessProof(int Pid,long StartedUtcTicks);
internal sealed record ControllerProof(string Run,string Boot,ProcessProof Process,long JobHandle);
internal sealed record WatcherProof(string Run,string Role,ProcessProof Process,bool JobHeld);
internal sealed record RecoveryResult(string Run,bool Success);

internal sealed class ProtectedRun:IDisposable
{
    internal readonly string Root,Id,Image,AgentRoot;
    internal readonly UpgradePlan Plan;
    readonly List<FileStream> pins=new();
    static readonly JsonSerializerOptions Strict=new(){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    internal ProtectedRun(string root)
    {
        AgentRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RoutePilot","Agent");
        Root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));string leaf=Path.GetFileName(Root);
        if(!string.Equals(Path.GetDirectoryName(Root),AgentRoot,StringComparison.OrdinalIgnoreCase)||leaf.Length!=46||!leaf.StartsWith("native-update-",StringComparison.Ordinal)||!IdValid(leaf[14..]))throw new IOException("Upgrade run path refused.");
        Id=leaf[14..];CheckPath(Root);Image=Path.Combine(Root,"Cita.NativeUpdater.exe");
        if(!string.Equals(Environment.ProcessPath,Image,StringComparison.OrdinalIgnoreCase))throw new IOException("Upgrade image location differs.");
        try
        {
            Plan=Read<UpgradePlan>("plan.json",16384);
            if(Plan.Schema!=1||Plan.Run!=Id||!System.Text.RegularExpressions.Regex.IsMatch(Plan.OwnerSid??"",@"\AS-1-5-21-(\d+-){3}\d+\z")||!long.TryParse(Plan.Boot,out long boot)||boot<=0||!HashValid(Plan.HelperSha)||!HashValid(Plan.IntentSha)||Plan.Files==null||Plan.Files.Count is <2 or >24)throw new IOException("Upgrade plan refused.");
            if(!Plan.Files.ContainsKey("Actor.ps1")||!Plan.Files.ContainsKey("Recover.ps1"))throw new IOException("Upgrade scripts missing.");
            pins.Add(Pin(Image,Plan.HelperSha));pins.Add(Open("plan.json"));
            foreach(var file in Plan.Files)
            {
                if(file.Key.Length is <1 or >100||file.Key.IndexOfAny(new[]{'/', '\\', ':','\0'})>=0||file.Key is "." or ".."||file.Key.EndsWith('.')||file.Key.EndsWith(' ')||file.Key.Equals("plan.json",StringComparison.OrdinalIgnoreCase)||file.Key.Equals("Cita.NativeUpdater.exe",StringComparison.OrdinalIgnoreCase)||!HashValid(file.Value))throw new IOException("Upgrade payload name refused.");
                pins.Add(Pin(Path.Combine(Root,file.Key),file.Value));
            }
        }catch{Dispose();throw;}
    }
    internal static bool IdValid(string id)=>id.Length==32&&id.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool HashValid(string hash)=>hash.Length==64&&hash.All(c=>c is >= '0' and <= '9' or >= 'A' and <= 'F');
    internal FileStream Open(string name){string path=Path.Combine(Root,name);CheckPath(path);return new(path,FileMode.Open,FileAccess.Read,FileShare.Read);}
    internal T Read<T>(string name,int limit=16384)
    {
        using var f=Open(name);if(f.Length is <1||f.Length>limit)throw new IOException("Upgrade record size refused.");return JsonSerializer.Deserialize<T>(f,Strict)??throw new IOException("Upgrade record empty.");
    }
    internal FileStream Intent()
    {
        string path=Path.Combine(AgentRoot,"native","intent.json");CheckPath(path);var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        try{if(f.Length is <1 or >4096||Convert.ToHexString(SHA256.HashData(f))!=Plan.IntentSha)throw new IOException("Upgrade intent bytes differ.");f.Position=0;using var doc=JsonDocument.Parse(f);var fields=doc.RootElement.EnumerateObject().Where(x=>x.NameEquals("Desired")).ToArray();if(fields.Length!=1||fields[0].Value.ValueKind!=JsonValueKind.False)throw new IOException("Upgrade intent is not disconnected.");f.Position=0;return f;}catch{f.Dispose();throw;}
    }
    internal static FileStream Pin(string path,string sha)
    {
        CheckPath(path);var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        try{if(!HashValid(sha)||Convert.ToHexString(SHA256.HashData(f))!=sha)throw new IOException("Upgrade payload digest differs.");f.Position=0;return f;}catch{f.Dispose();throw;}
    }
    internal void Save(string name,object value)
    {
        string path=Path.Combine(Root,name);if(Path.GetDirectoryName(path)!=Root)throw new IOException("Upgrade record escaped root.");
        string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var f=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){JsonSerializer.Serialize(f,value);f.Flush(true);}
            // Record owners must not depend on the caller's token default owner.
            var acl=new FileSecurity();acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));acl.SetAccessRuleProtection(true,false);
            foreach(string sid in new[]{"S-1-5-18","S-1-5-32-544"})acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),FileSystemRights.FullControl,AccessControlType.Allow));
            new FileInfo(tmp).SetAccessControl(acl);File.Move(tmp,path,true);
        }finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    internal static Process Hold(ProcessProof proof,string image)
    {
        if(proof.Pid<=0||proof.StartedUtcTicks<=0)throw new IOException("Upgrade process proof refused.");
        var p=Process.GetProcessById(proof.Pid);
        try{_=p.Handle;if(p.HasExited||p.StartTime.ToUniversalTime().Ticks!=proof.StartedUtcTicks||!string.Equals(p.MainModule?.FileName,image,StringComparison.OrdinalIgnoreCase))throw new IOException("Upgrade process identity differs.");return p;}catch{p.Dispose();throw;}
    }
    internal static ProcessProof Identity(Process p)=>new(p.Id,p.StartTime.ToUniversalTime().Ticks);
    internal static void CheckPath(string path)
    {
        bool first=true;
        for(string? p=Path.GetFullPath(path);p!=null;p=Path.GetDirectoryName(p))
        {
            FileSystemInfo item=Directory.Exists(p)?new DirectoryInfo(p):new FileInfo(p);
            if((item.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Upgrade path link refused.");
            var acl=item is DirectoryInfo dir?(FileSystemSecurity)dir.GetAccessControl():((FileInfo)item).GetAccessControl();
            static bool Trusted(string? sid)=>sid is "S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
            if(!Trusted(acl.GetOwner(typeof(SecurityIdentifier))?.Value))throw new UnauthorizedAccessException("Upgrade path owner refused.");
            long mutation=first?0x500d0156:0x500c0040;
            foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))if(rule.AccessControlType==AccessControlType.Allow&&(rule.PropagationFlags&PropagationFlags.InheritOnly)==0&&!Trusted(rule.IdentityReference.Value)&&(((long)rule.FileSystemRights&0xffffffff)&mutation)!=0)throw new UnauthorizedAccessException("Upgrade path is writable.");
            first=false;
        }
    }
    public void Dispose(){foreach(var f in pins)f.Dispose();pins.Clear();}
}
