using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RoutePilot;

internal static class RuntimePrerequisiteTests
{
    static void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
    internal static async Task Run(string root)
    {
        byte[] payload=Encoding.UTF8.GetBytes("a prerequisite fixture, never executed");
        string hash=Convert.ToHexString(SHA512.HashData(payload));
        const string url="https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.13/windowsdesktop-runtime-10.0.13-win-x64.exe";
        using var valid=JsonDocument.Parse(JsonSerializer.Serialize(new{version="10.0.13",url,sha512=hash}));
        var spec=RuntimePrerequisite.Parse(valid.RootElement,new Version(10,0,13));
        foreach(string address in new[]{"http"+url[5..],url+"?download=1",url.Replace("builds.dotnet.microsoft.com","example.org"),url.Replace("win-x64","win-x86")})
        {
            using var bad=JsonDocument.Parse(JsonSerializer.Serialize(new{version="10.0.13",url=address,sha512=hash}));
            bool refused=false;try{RuntimePrerequisite.Parse(bad.RootElement,new Version(10,0,13));}catch(InvalidDataException){refused=true;}
            Assert(refused,"An unknown runtime URL or architecture is rejected.");
        }
        bool obsolete=false;try{RuntimePrerequisite.Parse(valid.RootElement,new Version(10,0,14));}catch(InvalidDataException){obsolete=true;}
        Assert(obsolete,"A prerequisite cannot satisfy a higher package minimum with an older patch.");
        string folder=Path.Combine(root,"runtime","runtime-download-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            using var good=new HttpClient(new Handler(payload));
            string path=Path.Combine(folder,"good.exe");await RuntimePrerequisite.Download(good,spec,path,CancellationToken.None);
            Assert(File.ReadAllBytes(path).Length==payload.Length,"The separately downloaded runtime is checked before it can be executed.");
            using var corrupt=new HttpClient(new Handler(Encoding.UTF8.GetBytes("corrupt")));
            bool refused=false;try{await RuntimePrerequisite.Download(corrupt,spec,Path.Combine(folder,"bad.exe"),CancellationToken.None);}catch(InvalidDataException){refused=true;}
            Assert(refused,"A corrupt runtime fails SHA-512.");
            using var redirect=new HttpClient(new Handler(payload,true));
            refused=false;try{await RuntimePrerequisite.Download(redirect,spec,Path.Combine(folder,"redirect.exe"),CancellationToken.None);}catch(InvalidDataException){refused=true;}
            Assert(refused,"A runtime redirect outside the pinned Microsoft URL is rejected.");
        }
        finally{Directory.Delete(folder,true);}
    }
    sealed class Handler(byte[] bytes,bool redirect=false):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {RequestMessage=redirect?new HttpRequestMessage(HttpMethod.Get,"https://example.org/runtime.exe"):request,Content=new ByteArrayContent(bytes)});
    }
}
