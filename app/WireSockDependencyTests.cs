using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RoutePilot;

internal static class WireSockDependencyTests
{
    sealed class Response(byte[] bytes,Uri? redirected=null,long? reported=null):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var content=new ByteArrayContent(bytes);content.Headers.ContentLength=reported??bytes.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=content,RequestMessage=redirected==null?request:new(HttpMethod.Get,redirected)});
        }
    }
    internal static async Task Run(string root)
    {
        string folder=Path.Combine(root,"runtime","dependency-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            byte[] bytes=Encoding.UTF8.GetBytes("controlled installer fixture");
            var release=WireSockDependency.Reviewed with{Sha256=Convert.ToHexString(SHA256.HashData(bytes)),Bytes=bytes.Length};
            string path=Path.Combine(folder,"installer.exe");
            using(var client=new HttpClient(new Response(bytes)))await WireSockDependency.Download(client,release,path,CancellationToken.None);
            if(!File.Exists(path))throw new Exception("Verified dependency was not staged.");
            File.Delete(path);
            async Task Reject(WireSockRelease spec,HttpMessageHandler handler)
            {
                using var client=new HttpClient(handler);bool rejected=false;
                try{await WireSockDependency.Download(client,spec,path,CancellationToken.None);}catch(InvalidDataException){rejected=true;}
                if(!rejected||File.Exists(path))throw new Exception("Rejected dependency remained executable.");
            }
            await Reject(release with{Sha256=new string('0',64)},new Response(bytes));
            await Reject(release,new Response(bytes,new Uri("https://example.com/installer.exe")));
            await Reject(release,new Response(bytes,reported:bytes.Length+1));
            await Reject(release with{Bytes=bytes.Length-1},new Response(bytes,reported:bytes.Length-1));
            await Reject(release with{Bytes=WireSockDependency.MaximumBytes+1},new Response(bytes));
            await Reject(release with{Url=new Uri("http://wiresock.net/installer.exe")},new Response(bytes));
            File.WriteAllText(path,"existing verified download");
            using(var client=new HttpClient(new Response(bytes)))
            {
                try{await WireSockDependency.Download(client,release,path,CancellationToken.None);throw new Exception("An existing download was overwritten.");}
                catch(IOException){}
            }
            if(File.ReadAllText(path)!="existing verified download")throw new Exception("A failed staging attempt deleted a pre-existing file.");
            foreach(string status in new[]{"Not connected","Нет подключения\r\n","Disconnected"})
                if(!WireSockDependency.Disconnected(status))throw new Exception("Disconnected state not recognized.");
            foreach(string status in new[]{"Connected","Not connected to profile x","error: Not connected",""})
                if(WireSockDependency.Disconnected(status))throw new Exception("Ambiguous status authorized dependency maintenance.");
        }
        finally{Directory.Delete(folder,true);}
    }
}
