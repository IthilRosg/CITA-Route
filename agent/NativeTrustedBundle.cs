using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace RoutePilot.Agent;

// The catalog digest must come from the protected service configuration. A
// catalog beside user-writable executables is not a trust root. Keep this lease
// alive until every child has exited: open handles deny writes and replacement.
internal sealed class NativeTrustedBundle:IDisposable
{
    internal sealed record Entry(string Name,string Sha256);
    internal sealed record Catalog(int Schema,Entry Owner,Entry Core,Entry? Guard=null,Entry? Policy=null,Entry? Profile=null,Entry? CoreConfig=null,string ExpectedExitHash="",string ProfileName="");
    readonly List<FileStream> leases=new();
    internal string OwnerPath{get;private set;}="";
    internal string CorePath{get;private set;}="";
    internal string GuardPath{get;private set;}="";
    internal string PolicyText{get;private set;}="";
    internal string ProfileText{get;private set;}="";
    internal string CoreConfigPath{get;private set;}="";
    internal string ExpectedExitHash{get;private set;}="";
    internal string ProfileName{get;private set;}="Подготовленный профиль";
    internal string ProfileId{get;private set;}="";
    internal string CatalogName{get;private set;}="native-catalog.json";
    internal static string ReadProtectedText(string path,int maxBytes=65536)
    {
        var file=new FileInfo(Path.GetFullPath(path));CheckAcl(file);for(var parent=file.Directory;parent!=null;parent=parent.Parent)CheckAcl(parent,true);
        using var input=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length<1||input.Length>maxBytes)throw new InvalidDataException("Protected native document size.");using var reader=new StreamReader(input);return reader.ReadToEnd();
    }
    static bool Hash(string? value)=>value?.Length==64&&value.All(Uri.IsHexDigit);
    static bool Trusted(string sid)=>sid is "S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    static void CheckAcl(FileSystemInfo item,bool ancestor=false)
    {
        if((item.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Native component reparse point refused.");
        FileSystemSecurity acl=item is DirectoryInfo dir?dir.GetAccessControl():((FileInfo)item).GetAccessControl();
        if(acl.GetOwner(typeof(SecurityIdentifier)) is not {} owner||!Trusted(owner.Value))throw new UnauthorizedAccessException("Untrusted native component owner.");
        var mutation=ancestor?FileSystemRights.DeleteSubdirectoriesAndFiles|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership:
            FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership;
        foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
        {
            if((rule.PropagationFlags&PropagationFlags.InheritOnly)!=0)continue;
            if(rule.AccessControlType==AccessControlType.Allow&&(rule.FileSystemRights&mutation)!=0&&!Trusted(rule.IdentityReference.Value))
                throw new UnauthorizedAccessException("Native component is writable by an untrusted principal.");
        }
    }
    FileStream OpenChecked(string root,Entry entry)
    {
        if(string.IsNullOrWhiteSpace(entry.Name)||entry.Name!=Path.GetFileName(entry.Name)||entry.Name.IndexOfAny(new[]{':','/','\\'})>=0||!Hash(entry.Sha256))throw new InvalidDataException("Invalid native catalog entry.");
        string path=Path.Combine(root,entry.Name);CheckAcl(new FileInfo(path));
        var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);leases.Add(file);
        if(file.Length is <1 or >268435456||!Convert.ToHexString(SHA256.HashData(file)).Equals(entry.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Native component digest mismatch.");
        file.Position=0;return file;
    }
    internal static NativeTrustedBundle Open(string directory,string trustedCatalogHash,string catalogName="native-catalog.json")
    {
        if(!OperatingSystem.IsWindows()||!Hash(trustedCatalogHash))throw new InvalidDataException("Invalid native trust root.");
        var lease=new NativeTrustedBundle();
        try
        {
            string root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));var current=new DirectoryInfo(root);CheckAcl(current);
            for(var parent=current.Parent;parent!=null;parent=parent.Parent)CheckAcl(parent,true);
            if(catalogName!="native-catalog.json"&&!System.Text.RegularExpressions.Regex.IsMatch(catalogName,@"\Acatalog-[a-f0-9]{32}\.json\z"))throw new InvalidDataException("Invalid native catalog name.");
            lease.CatalogName=catalogName;
            var catalogFile=lease.OpenChecked(root,new(catalogName,trustedCatalogHash));
            if(catalogFile.Length>8192)throw new InvalidDataException("Oversized native catalog.");
            var catalog=JsonSerializer.Deserialize<Catalog>(catalogFile,new JsonSerializerOptions{UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow})??throw new InvalidDataException("Empty native catalog.");
            if(catalog.Schema!=1||catalog.Owner==null||catalog.Core==null||catalog.Owner.Name.Equals(catalog.Core.Name,StringComparison.OrdinalIgnoreCase)||!catalog.Owner.Name.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)||!catalog.Core.Name.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Invalid native catalog.");
            lease.OpenChecked(root,catalog.Owner);lease.OpenChecked(root,catalog.Core);
            lease.OwnerPath=Path.Combine(root,catalog.Owner.Name);lease.CorePath=Path.Combine(root,catalog.Core.Name);
            var extra=new[]{catalog.Guard,catalog.Policy,catalog.Profile,catalog.CoreConfig}.Where(x=>x!=null).Cast<Entry>().ToArray();
            if(extra.Select(x=>x.Name).Append(catalog.Owner.Name).Append(catalog.Core.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=extra.Length+2)throw new InvalidDataException("Duplicate native role.");
            foreach(var entry in extra)
            {
                var stream=lease.OpenChecked(root,entry);
                if(entry==catalog.Guard){if(!entry.Name.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Invalid guard role.");lease.GuardPath=Path.Combine(root,entry.Name);}
                else if(entry==catalog.CoreConfig)lease.CoreConfigPath=Path.Combine(root,entry.Name);
                else {if(stream.Length>65536)throw new InvalidDataException("Native document too large.");using var reader=new StreamReader(stream,leaveOpen:true);string text=reader.ReadToEnd();if(entry==catalog.Policy)lease.PolicyText=text;else lease.ProfileText=text;}
            }
            lease.ExpectedExitHash=catalog.ExpectedExitHash;lease.ProfileId=catalog.Profile?.Sha256??"";
            if(!string.IsNullOrWhiteSpace(catalog.ProfileName))lease.ProfileName=new string(catalog.ProfileName.Where(c=>!char.IsControl(c)).Take(80).ToArray());return lease;
        }
        catch{lease.Dispose();throw;}
    }
    public void Dispose(){foreach(var file in leases)file.Dispose();leases.Clear();}
}
