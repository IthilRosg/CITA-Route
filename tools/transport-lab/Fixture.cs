using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cita.TransportLab;

internal sealed record Fixture(string Root,ulong LoopbackLuid,ulong OtherLuid)
{
    internal string Id=>Path.GetFileName(Root)["transport-lab-".Length..];
    internal string Image=>Path.Combine(Root,"guarded","Cita.TransportLab.exe");
    internal string Label=>"CITA Route transport lab "+Id;
    internal Guid SubLayer=>Key("sublayer");
    internal Guid[] FilterKeys=>Enumerable.Range(0,4).Select(i=>Key("filter"+i)).ToArray();
    internal Guid Key(string role)=>new(SHA256.HashData(Encoding.UTF8.GetBytes("CITA Route transport lab/v1/"+Id+"/"+role)).AsSpan(0,16));
    internal static string RuntimeRoot
    {
        get
        {
            string folder=Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            if(Path.GetFileName(folder)!="transport-lab-build" || Path.GetFileName(Path.GetDirectoryName(folder))!="runtime")
                throw new IOException("Build the lab in runtime/transport-lab-build before applying fixture filters.");
            return Path.GetDirectoryName(folder)!;
        }
    }
    internal static void ValidatePath(string root,string runtime)
    {
        string full=Path.GetFullPath(root),parent=Path.GetDirectoryName(full)!;
        if(!full.Equals(root,StringComparison.OrdinalIgnoreCase) || !parent.Equals(Path.GetFullPath(runtime),StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(full),@"^transport-lab-[0-9a-f]{32}$"))
            throw new IOException("Only a generated transport-lab fixture directory is permitted.");
        for(DirectoryInfo? dir=new(full);dir!=null;dir=dir.Parent)
            if(dir.Exists && (dir.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Fixture ancestors cannot be reparse points.");
    }
    internal void Validate()
    {
        ValidatePath(Root,RuntimeRoot);
        if(LoopbackLuid==0 || OtherLuid==0 || LoopbackLuid==OtherLuid)throw new IOException("Missing distinct interface identities.");
        foreach(string name in new[]{"Cita.TransportLab.exe","Cita.TransportLab.dll"})
        {
            string expected=Path.Combine(AppContext.BaseDirectory,name),copy=Path.Combine(Root,"guarded",name);
            if((File.GetAttributes(copy)&FileAttributes.ReparsePoint)!=0 ||
                !SHA256.HashData(File.ReadAllBytes(expected)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(copy))))
                throw new IOException("Only the exact private copy of this lab executable is permitted.");
        }
        if((File.GetAttributes(Path.Combine(Root,"guarded"))&FileAttributes.ReparsePoint)!=0)
            throw new IOException("The guarded directory cannot be a reparse point.");
    }
    internal static Fixture Load(string root)
    {
        ValidatePath(root,RuntimeRoot);
        string file=Path.Combine(root,"fixture.json");
        if(new FileInfo(file).Length>4096)throw new IOException("Fixture ledger is too large.");
        var value=JsonSerializer.Deserialize<Fixture>(File.ReadAllText(file))??throw new IOException("Missing fixture ledger.");
        if(!value.Root.Equals(root,StringComparison.OrdinalIgnoreCase))throw new IOException("Fixture root changed.");
        value.Validate();return value;
    }
    internal static Fixture Create(ulong luid,ulong otherLuid)
    {
        string root=Path.Combine(RuntimeRoot,"transport-lab-"+Guid.NewGuid().ToString("N"));
        ValidatePath(root,RuntimeRoot);Directory.CreateDirectory(Path.Combine(root,"guarded"));
        foreach(string name in new[]{"Cita.TransportLab.exe","Cita.TransportLab.dll","Cita.TransportLab.deps.json","Cita.TransportLab.runtimeconfig.json"})
            File.Copy(Path.Combine(AppContext.BaseDirectory,name),Path.Combine(root,"guarded",name));
        var value=new Fixture(root,luid,otherLuid);
        using(var stream=new FileStream(Path.Combine(root,"fixture.json"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))
        {JsonSerializer.Serialize(stream,value);stream.Flush(true);}
        value.Validate();return value;
    }
}
