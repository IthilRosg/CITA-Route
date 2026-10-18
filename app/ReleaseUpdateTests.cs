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
            var entries=new[]{"RoutePilot.exe","SmartRouting.ps1","Install-Agent.ps1","Configure-Connection.ps1","product.json"}.Select(name=>{
                string path=Path.Combine(payload,name);File.WriteAllText(path,"fixture");return new{path=name,bytes=new FileInfo(path).Length,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))};
            }).ToArray();
            File.WriteAllText(Path.Combine(payload,"manifest.json"),JsonSerializer.Serialize(new{version="0.4.0",architecture="win-x64",selfContained=true,files=entries}));
            ReleaseUpdates.VerifyPackage(payload,new Version(0,4,0));
            string validZip=Path.Combine(folder,"valid.zip");ZipFile.CreateFromDirectory(payload,validZip);
            byte[] bytes=File.ReadAllBytes(validZip);string digest=Convert.ToHexString(SHA256.HashData(bytes));
            using var http=new HttpClient(new FixtureHandler(bytes,digest));
            var update=new ReleaseUpdate(new Version(0,4,0),"v0.4.0",new Uri("https://github.com/IthilRosg/CITA-Route/releases/tag/v0.4.0"),new Uri("https://github.com/archive.zip"),new Uri("https://github.com/SHA256SUMS.txt"),"CITA-Route-0.4.0-win-x64.zip",bytes.Length);
            string downloaded=Task.Run(()=>new ReleaseUpdates(http).Download(update,folder,null,CancellationToken.None)).GetAwaiter().GetResult();
            Assert(File.Exists(Path.Combine(downloaded,"product.json")),"Verified download extracted");
            using var wrong=new HttpClient(new FixtureHandler(bytes,new string('0',64)));
            refused=false;try{Task.Run(()=>new ReleaseUpdates(wrong).Download(update,folder,null,CancellationToken.None)).GetAwaiter().GetResult();}catch(InvalidDataException){refused=true;}Assert(refused,"Bad SHA-256 cannot be installed");
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
