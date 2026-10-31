using System.Text.Json;

namespace RoutePilot.Agent;

internal static class NativeZapretManifestTests
{
    internal static Task Run()
    {
        var files=NativeZapretBundle.RequiredFiles.Select(name=>new NativeTrustedBundle.ZapretFile(name,new string('a',64),1,new string('b',40))).ToArray();
        var manifest=new NativeTrustedBundle.ZapretManifestDocument(1,"bol-van/zapret-win-bundle","4840909b1a26389cb72c46be5301d610b3768928","1c4bcc0bd1294e8b06b59c775c4783fbdce22b89",files,false);
        string good=JsonSerializer.Serialize(manifest);
        if(NativeTrustedBundle.ParseZapretManifest(good).Files.Length!=6)throw new IOException("Valid pinned manifest refused.");
        var role=new NativeTrustedBundle.Entry("zapret-manifest.json",new string('a',64));
        var catalog=new NativeTrustedBundle.Catalog(1,new("owner.exe",new string('b',64)),new("core.exe",new string('c',64)),ZapretManifest:role);
        if(JsonSerializer.Deserialize<NativeTrustedBundle.Catalog>(JsonSerializer.Serialize(catalog))?.ZapretManifest!=role)throw new IOException("Zapret catalog role was not preserved.");
        static bool Refused(string json){try{NativeTrustedBundle.ParseZapretManifest(json);return false;}catch(Exception e)when(e is JsonException or InvalidDataException){return true;}}
        var dup=files.ToArray();dup[1]=dup[0];
        var traversal=files.ToArray();traversal[0]=traversal[0] with{Name="../winws2.exe"};
        var hash=files.ToArray();hash[0]=hash[0] with{Sha256="bad"};
        if(!Refused(JsonSerializer.Serialize(manifest with{Files=dup}))||!Refused(JsonSerializer.Serialize(manifest with{Files=traversal}))||!Refused(JsonSerializer.Serialize(manifest with{Files=hash}))||!Refused(JsonSerializer.Serialize(manifest with{Schema=2}))||!Refused(JsonSerializer.Serialize(manifest with{RedistributionReady=true}))||!Refused(good.Insert(1,"\"Schema\":1,"))||!Refused(good.Replace(",\"RedistributionReady\":false","")))throw new IOException("Invalid pinned manifest accepted.");
        return Task.CompletedTask;
    }
}
