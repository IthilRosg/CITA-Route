using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RoutePilot;

internal static class ReleaseUpdateTests
{
    static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    static string Release(string owner="IthilRosg",bool pre=false)=>JsonSerializer.Serialize(new{draft=false,prerelease=pre,tag_name="v0.4.0",assets=new[]{
        new{name="CITA-Route-0.4.0-win-x64.zip",size=100,browser_download_url="https://github.com/"+owner+"/CITA-Route/releases/download/v0.4.0/CITA-Route-0.4.0-win-x64.zip"},
        new{name="SHA256SUMS.txt",size=100,browser_download_url="https://github.com/IthilRosg/CITA-Route/releases/download/v0.4.0/SHA256SUMS.txt"}}});
    public static void Run(string root)
    {
        Assert(ReleaseUpdates.Compare(new Version(0,3,0),new Version(0,3,0,0))==0,"Release and assembly versions must compare equally");
        using(var both=JsonDocument.Parse(JsonSerializer.Serialize(new{draft=false,prerelease=false,tag_name="v9.0.0",assets=new[]{
            new{name="CITA-Route-9.0.0-win-x64.zip",size=200L,browser_download_url="https://github.com/IthilRosg/CITA-Route/releases/download/v9.0.0/CITA-Route-9.0.0-win-x64.zip"},
            new{name="CITA-Route-9.0.0-win-x64-framework.zip",size=100L,browser_download_url="https://github.com/IthilRosg/CITA-Route/releases/download/v9.0.0/CITA-Route-9.0.0-win-x64-framework.zip"},
            new{name="SHA256SUMS.txt",size=100L,browser_download_url="https://github.com/IthilRosg/CITA-Route/releases/download/v9.0.0/SHA256SUMS.txt"}}})))
        {
            Assert(ReleaseUpdates.Parse(both.RootElement,new Version(0,6,0),true)!.FileName.EndsWith("-framework.zip"),"A machine runtime allows the smaller archive.");
            Assert(ReleaseUpdates.Parse(both.RootElement,new Version(0,6,0),false)!.FileName.EndsWith("-win-x64.zip"),"A runtime-free machine retains the self-contained archive.");
            Assert(ReleaseUpdates.Parse(both.RootElement,new Version(0,6,0),true,"CITA-Route-9.0.0-win-x64.zip")!.Bytes==200,"A staged transaction retains its original flavor even if the runtime becomes available.");
            bool rejected=false;try{ReleaseUpdates.Parse(both.RootElement,new Version(0,6,0),false,"CITA-Route-9.0.0-win-x64-framework.zip");}catch(InvalidDataException){rejected=true;}
            Assert(rejected,"Removing the runtime cannot silently change a staged framework transaction.");
        }
        string dotnetFixture=Path.Combine(root,"runtime","runtime-detection-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dotnetFixture);File.WriteAllText(Path.Combine(dotnetFixture,"dotnet.exe"),"fixture");
            Assert(!DesktopRuntime.At(dotnetFixture),"Host alone is not a Desktop runtime.");
            foreach(string family in new[]{"Microsoft.NETCore.App","Microsoft.WindowsDesktop.App"})
            foreach(string version in new[]{"8.0.32","10.0.12","10.0.13-preview.1","11.0.0"})
            {
                string path=Path.Combine(dotnetFixture,"shared",family,version);Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path,family=="Microsoft.NETCore.App"?"System.Private.CoreLib.dll":"Microsoft.WindowsDesktop.App.deps.json"),"fixture");
            }
            Assert(!DesktopRuntime.At(dotnetFixture),"An older patch, preview, older or newer major is not an adequate .NET 10 runtime.");
            foreach(string family in new[]{"Microsoft.NETCore.App","Microsoft.WindowsDesktop.App"})
            {
                string path=Path.Combine(dotnetFixture,"shared",family,"10.0.13");Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path,family=="Microsoft.NETCore.App"?"System.Private.CoreLib.dll":"Microsoft.WindowsDesktop.App.deps.json"),"fixture");
                if(family=="Microsoft.NETCore.App")Assert(!DesktopRuntime.At(dotnetFixture),"Core alone cannot certify WPF readiness.");
            }
            Assert(DesktopRuntime.At(dotnetFixture),"Both stable runtime families are required.");
            Assert(!DesktopRuntime.At(dotnetFixture,new Version(10,0,14)),"A prepared package's higher minimum patch must be respected.");
        }
        finally{if(Directory.Exists(dotnetFixture))Directory.Delete(dotnetFixture,true);}
        using(var release=JsonDocument.Parse(Release()))Assert(ReleaseUpdates.Parse(release.RootElement,new Version(0,3,0))?.Version==new Version(0,4,0),"New stable version is offered");
        using(var release=JsonDocument.Parse(Release(pre:true)))Assert(ReleaseUpdates.Parse(release.RootElement,new Version(0,3,0))==null,"Prerelease must not enter stable channel");
        bool refused=false;try{using var release=JsonDocument.Parse(Release("other-owner"));ReleaseUpdates.Parse(release.RootElement,new Version(0,3,0));}catch(InvalidDataException){refused=true;}Assert(refused,"Foreign release asset rejected");
        string hash=new string('a',64);
        Assert(ReleaseUpdates.ExpectedHash(hash+"  client.zip\n","client.zip")==hash.ToUpperInvariant(),"Checksum parsing");
        refused=false;try{ReleaseUpdates.ExpectedHash(hash+"  client.zip\n"+hash+"  client.zip","client.zip");}catch(InvalidDataException){refused=true;}Assert(refused,"Ambiguous checksum rejected");
        string folder=Path.Combine(root,"runtime","release-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            foreach(string unsafeName in new[]{"../outside.txt","C:/outside.txt","folder/file.","folder/file:stream"})
            {
                string zip=Path.Combine(folder,Guid.NewGuid()+".zip");using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)){using var output=new StreamWriter(archive.CreateEntry(unsafeName).Open());output.Write("unsafe");}
                refused=false;try{ReleaseUpdates.Extract(zip,Path.Combine(folder,Guid.NewGuid().ToString()));}catch(InvalidDataException){refused=true;}Assert(refused,"Unsafe ZIP path rejected: "+unsafeName);
            }
            string payload=Path.Combine(folder,"payload");Directory.CreateDirectory(payload);
            var entries=new[]{"RoutePilot.exe","SmartRouting.ps1","Install-Agent.ps1","Configure-Connection.ps1","product.json","browser-extension/manifest.json"}.Select(name=>{
                string path=Path.Combine(payload,name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,"fixture");return new{path=name,bytes=new FileInfo(path).Length,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))};
            }).ToArray();
            File.WriteAllText(Path.Combine(payload,"manifest.json"),JsonSerializer.Serialize(new{version="0.4.0",architecture="win-x64",selfContained=true,files=entries}));
            ReleaseUpdates.VerifyPackage(payload,new Version(0,4,0));
            string validZip=Path.Combine(folder,"valid.zip");ZipFile.CreateFromDirectory(payload,validZip);
            byte[] bytes=File.ReadAllBytes(validZip);string digest=Convert.ToHexString(SHA256.HashData(bytes));
            using var http=new HttpClient(new FixtureHandler(bytes,digest));
            var update=new ReleaseUpdate(new Version(0,4,0),"v0.4.0",new Uri("https://github.com/IthilRosg/CITA-Route/releases/tag/v0.4.0"),new Uri("https://github.com/archive.zip"),new Uri("https://github.com/SHA256SUMS.txt"),"CITA-Route-0.4.0-win-x64.zip",bytes.Length);
            string downloaded=Task.Run(()=>new ReleaseUpdates(http).Download(update,folder,null,CancellationToken.None)).GetAwaiter().GetResult();
            Assert(File.Exists(Path.Combine(downloaded,"product.json")),"Verified download extracted");
            Assert(ReleaseUpdates.VerifyExtractedArchive(validZip,downloaded,new Version(0,4,0)).Length==64,"Restoring a flat package selects its root manifest, not the extension manifest.");
            string wrappedZip=Path.Combine(folder,"wrapped.zip");ZipFile.CreateFromDirectory(payload,wrappedZip,CompressionLevel.Optimal,true);
            byte[] wrappedBytes=File.ReadAllBytes(wrappedZip);
            using(var wrappedHttp=new HttpClient(new FixtureHandler(wrappedBytes,Convert.ToHexString(SHA256.HashData(wrappedBytes)))))
            {
                var wrappedUpdate=update with {Bytes=wrappedBytes.Length};
                string wrapped=Task.Run(()=>new ReleaseUpdates(wrappedHttp).Download(wrappedUpdate,folder,null,CancellationToken.None)).GetAwaiter().GetResult();
                Assert(Path.GetFileName(wrapped)=="payload","A wrapped release selects the package root.");
                Assert(ReleaseUpdates.VerifyExtractedArchive(wrappedZip,wrapped,new Version(0,4,0)).Length==64,"Restoring a wrapped release tolerates a nested extension manifest.");
            }
            string browserManifest=Path.Combine(downloaded,"browser-extension","manifest.json");File.WriteAllText(browserManifest,"changed");
            refused=false;try{ReleaseUpdates.VerifyExtractedArchive(validZip,downloaded,new Version(0,4,0));}catch(InvalidDataException){refused=true;}
            Assert(refused,"Ignoring a nested manifest as a root never bypasses its payload checksum.");File.WriteAllText(browserManifest,"fixture");
            File.AppendAllText(Path.Combine(downloaded,"manifest.json")," ");
            refused=false;try{ReleaseUpdates.VerifyExtractedArchive(validZip,downloaded,new Version(0,4,0));}catch(InvalidDataException){refused=true;}
            Assert(refused,"The selected root manifest remains bound to the archive bytes.");
            int acceptedFolders=Directory.GetDirectories(Path.Combine(folder,"runtime","updates")).Length;
            using var wrong=new HttpClient(new FixtureHandler(bytes,new string('0',64)));
            refused=false;try{Task.Run(()=>new ReleaseUpdates(wrong).Download(update,folder,null,CancellationToken.None)).GetAwaiter().GetResult();}catch(InvalidDataException){refused=true;}Assert(refused,"Bad SHA-256 cannot be installed");
            string ambiguousZip=Path.Combine(folder,"ambiguous.zip");
            using(var ambiguous=ZipFile.Open(ambiguousZip,ZipArchiveMode.Create))foreach(string name in new[]{"first/manifest.json","second/manifest.json"}){using var writer=new StreamWriter(ambiguous.CreateEntry(name).Open());writer.Write("{}");}
            byte[] ambiguousBytes=File.ReadAllBytes(ambiguousZip);
            using(var ambiguousHttp=new HttpClient(new FixtureHandler(ambiguousBytes,Convert.ToHexString(SHA256.HashData(ambiguousBytes)))))
            {
                refused=false;try{Task.Run(()=>new ReleaseUpdates(ambiguousHttp).Download(update with {Bytes=ambiguousBytes.Length},folder,null,CancellationToken.None)).GetAwaiter().GetResult();}catch(InvalidDataException){refused=true;}
                Assert(refused,"Two possible package roots remain ambiguous.");
            }
            Assert(Directory.GetDirectories(Path.Combine(folder,"runtime","updates")).Length==acceptedFolders,"Rejected archives leave no extracted update directory.");
            string? packagedArchive=Environment.GetEnvironmentVariable("CITA_RELEASE_ARCHIVE_TEST");
            if(!string.IsNullOrEmpty(packagedArchive))
            {
                string extracted=Path.Combine(folder,"packaged-release");ReleaseUpdates.Extract(packagedArchive,extracted);
                string packaged=ReleaseUpdates.PackageDirectory(packagedArchive,extracted);
                Assert(ReleaseUpdates.VerifyExtractedArchive(packagedArchive,packaged,ReleaseUpdates.Current).Length==64,"The actual release ZIP passes the GUI updater's manifest and payload verification.");
            }
            File.WriteAllText(Path.Combine(payload,"SmartRouting.ps1"),"changed");
            refused=false;try{ReleaseUpdates.VerifyPackage(payload,new Version(0,4,0));}catch(InvalidDataException){refused=true;}Assert(refused,"Changed payload rejected");
        }
        finally{Directory.Delete(folder,true);}
    }
    sealed class FixtureHandler(byte[] archive,string hash):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){RequestMessage=request,
            Content=request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt")?new ByteArrayContent(Encoding.UTF8.GetBytes(hash+"  CITA-Route-0.4.0-win-x64.zip\n")):new ByteArrayContent(archive)});
    }
}
