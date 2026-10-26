using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace RoutePilot;

internal static class NativeComponentBundleTests
{
    internal static void Run(string root)
    {
        string folder=Path.Combine(root,"runtime","native-bundle-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder,"runtime"));
        try
        {
            string product=Path.Combine(folder,"product.json"),archive=Path.Combine(folder,"runtime","native-components.zip");
            File.WriteAllText(product,"{}");
            if(NativeComponentDownload.Bundled(folder,CancellationToken.None)!=null)throw new Exception("Legacy distribution must keep its existing download path.");
            File.WriteAllText(archive,"synthetic component fixture");
            string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)));
            File.WriteAllText(product,JsonSerializer.Serialize(new{distribution=new{nativeComponentSha256=hash}}));
            var result=NativeComponentDownload.Bundled(folder,CancellationToken.None);
            if(result?.Path!=archive||result.Hash!=hash)throw new Exception("Bundled components must retain their verified path and digest.");
            bool cancelled=false;try{NativeComponentDownload.Bundled(folder,new CancellationToken(true));}catch(OperationCanceledException){cancelled=true;}
            if(!cancelled)throw new Exception("Cancelled component preparation must not continue.");
            File.AppendAllText(archive,"tampered");Reject();
            File.Delete(archive);Reject();
            File.WriteAllText(product,"{\"distribution\":{\"nativeComponentSha256\":\"invalid\"}}");Reject();
            void Reject(){try{NativeComponentDownload.Bundled(folder,CancellationToken.None);}catch(IOException){return;}throw new Exception("Invalid declared bundle must fail closed, without silently using another source.");}
        }
        finally{Directory.Delete(folder,true);}
    }
}
