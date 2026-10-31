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
    internal sealed record Catalog(int Schema,Entry Owner,Entry Core,Entry? Guard=null,Entry? Policy=null,Entry? Profile=null,Entry? CoreConfig=null,string ExpectedExitHash="",string ProfileName="",Entry? ZapretManifest=null,Entry? ForcedIntent=null);
    internal sealed record ZapretFile(string Name,string Sha256,long Bytes,string GitBlob);
    internal sealed record ZapretManifestDocument(int Schema,string Repository,string Commit,string Tree,ZapretFile[] Files,bool RedistributionReady);
    readonly List<FileStream> leases=new();
    NativeZapretTrust? zapretTrust;
    internal NativeZapretBundle? ZapretBundle{get;private set;}
    internal Entry? ZapretManifestEntry{get;private set;}
    internal string? ZapretIssue{get;private set;}
    internal string OwnerPath{get;private set;}="";
    internal string CorePath{get;private set;}="";
    internal string GuardPath{get;private set;}="";
    internal string PolicyText{get;private set;}="";
    internal string ProfileText{get;private set;}="";
    internal string CoreConfigPath{get;private set;}="";
    internal string ForcedIntentPath{get;private set;}="";
    internal Entry? ForcedIntentEntry{get;private set;}
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
    internal static ZapretManifestDocument ParseZapretManifest(string text)
    {
        using var document=JsonDocument.Parse(text,new JsonDocumentOptions{MaxDepth=5});
        static void Unique(JsonElement node)
        {
            if(node.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var property in node.EnumerateObject()){if(!names.Add(property.Name))throw new InvalidDataException("Duplicate Zapret manifest field.");Unique(property.Value);}}
            else if(node.ValueKind==JsonValueKind.Array)foreach(var item in node.EnumerateArray())Unique(item);
        }
        Unique(document.RootElement);
        static void ExactFields(JsonElement node,params string[] expected)
        {
            if(node.ValueKind!=JsonValueKind.Object||!node.EnumerateObject().Select(p=>p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected))throw new InvalidDataException("Incomplete Zapret manifest fields.");
        }
        ExactFields(document.RootElement,"Schema","Repository","Commit","Tree","Files","RedistributionReady");
        if(!document.RootElement.TryGetProperty("Files",out var inventory)||inventory.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("Zapret manifest inventory missing.");
        foreach(var file in inventory.EnumerateArray())ExactFields(file,"Name","Sha256","Bytes","GitBlob");
        var manifest=JsonSerializer.Deserialize<ZapretManifestDocument>(document.RootElement,new JsonSerializerOptions{UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow})??throw new InvalidDataException("Zapret manifest missing.");
        if(manifest.Schema!=1||manifest.Repository!="bol-van/zapret-win-bundle"||manifest.Commit!="4840909b1a26389cb72c46be5301d610b3768928"||manifest.Tree!="1c4bcc0bd1294e8b06b59c775c4783fbdce22b89"||manifest.Files==null||manifest.Files.Length!=NativeZapretBundle.RequiredFiles.Length||manifest.RedistributionReady)throw new InvalidDataException("Zapret upstream identity refused.");
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var file in manifest.Files)
            if(file==null||!NativeZapretBundle.RequiredFiles.Contains(file.Name,StringComparer.Ordinal)||!Hash(file.Sha256)||file.Bytes is <1 or >8388608||file.GitBlob is not {Length:40}||!file.GitBlob.All(Uri.IsHexDigit)||!result.TryAdd(file.Name,file.Sha256))throw new InvalidDataException("Zapret manifest inventory refused.");
        return manifest;
    }
    static bool Trusted(string sid)=>sid is "S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    internal static void CheckAcl(FileSystemInfo item,bool ancestor=false)
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
    static void CheckMutableRole(string root,Entry entry)
    {
        if(entry.Name!="forced-intent.json"||!Hash(entry.Sha256))throw new InvalidDataException("Invalid native mutable role.");
        var file=new FileInfo(Path.Combine(root,entry.Name));CheckAcl(file);
        if(file.Length is <1 or >65536)throw new InvalidDataException("Native mutable document too large.");
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
            var extra=new[]{catalog.Guard,catalog.Policy,catalog.Profile,catalog.CoreConfig,catalog.ZapretManifest,catalog.ForcedIntent}.Where(x=>x!=null).Cast<Entry>().ToArray();
            if(extra.Select(x=>x.Name).Append(catalog.Owner.Name).Append(catalog.Core.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=extra.Length+2)throw new InvalidDataException("Duplicate native role.");
            foreach(var entry in extra)
            {
                if(entry==catalog.ZapretManifest)
                {
                    lease.ZapretManifestEntry=entry;
                    try
                    {
                        if(entry.Name!="zapret-manifest.json")throw new InvalidDataException("Zapret manifest path refused.");
                        var manifestStream=lease.OpenChecked(root,entry);if(manifestStream.Length>16384)throw new InvalidDataException("Zapret manifest too large.");
                        using var manifestReader=new StreamReader(manifestStream,leaveOpen:true);
                        var manifest=ParseZapretManifest(manifestReader.ReadToEnd());
                        var hashes=manifest.Files.ToDictionary(f=>f.Name,f=>f.Sha256,StringComparer.Ordinal);
                        var bundle=new NativeZapretBundle(Path.Combine(root,"zapret"),hashes);
                        var trust=NativeZapretTrust.Open(bundle);
                        try
                        {
                            foreach(var file in manifest.Files)
                            {
                                string path=Path.Combine(bundle.Root,file.Name.Replace('/',Path.DirectorySeparatorChar));
                                using var componentStream=File.OpenRead(path);if(componentStream.Length!=file.Bytes)throw new InvalidDataException("Zapret component size differs.");
                                using var sha=IncrementalHash.CreateHash(HashAlgorithmName.SHA1);sha.AppendData(System.Text.Encoding.ASCII.GetBytes("blob "+componentStream.Length+"\0"));
                                byte[] buffer=new byte[65536];int read;while((read=componentStream.Read(buffer))>0)sha.AppendData(buffer.AsSpan(0,read));
                                if(!Convert.ToHexString(sha.GetHashAndReset()).Equals(file.GitBlob,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Zapret component Git blob differs.");
                            }
                            lease.zapretTrust=trust;lease.ZapretBundle=bundle;
                        }
                        catch{trust.Dispose();throw;}
                    }
                    catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException){lease.ZapretIssue="ZapretBundleInvalid";}
                    continue;
                }
                if(entry==catalog.ForcedIntent)
                {
                    // This role is deliberately mutable while the service is
                    // disconnected. Its ACL and bounded schema are checked by
                    // NativeForcedIntentStore; do not pin a stale startup hash
                    // or keep a FileShare.Read lease that blocks File.Replace.
                    CheckMutableRole(root,entry);lease.ForcedIntentEntry=entry;lease.ForcedIntentPath=Path.Combine(root,entry.Name);continue;
                }
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
    public void Dispose(){zapretTrust?.Dispose();zapretTrust=null;foreach(var file in leases)file.Dispose();leases.Clear();}
}
