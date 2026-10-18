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

public static class UpdateWorkflowTests
{
    static void Assert(bool value,string text){if(!value)throw new Exception(text);}
    public static async Task Run(string root)
    {
        string folder=Path.Combine(root,"runtime","update-workflow-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(folder,"runtime"));
        try
        {
            var history=new ProfileMemory(Path.Combine(folder,"user","profile-history.json"));
            string cli="C:\\Program Files\\WireSock Secure Connect\\command-line\\wiresock-connect-cli.exe";
            DateTime now=DateTime.UtcNow;
            history.Remember("Применённый",cli,now.AddMinutes(-3),false);history.Remember("Успешный",cli,now.AddMinutes(-1),true);
            var reopened=new ProfileMemory(Path.Combine(folder,"user","profile-history.json"));
            Assert(reopened.Select(cli,"Применённый","",new[]{"Применённый","Успешный"})=="Успешный","Last confirmed profile survives a new instance/folder.");
            Assert(reopened.Select(cli,"Применённый","Применённый",new[]{"Применённый","Успешный"})=="Применённый","A discovery result must not overwrite a current selection.");
            Assert(reopened.Select(cli,"Применённый","",new[]{"Применённый"})==null,"A deleted remembered profile must not silently select another.");
            reopened.Remember("Старый",cli,now.AddDays(-1),false);
            Assert(reopened.Preferred(cli,"")=="Успешный","An old folder cannot replace newer profile history.");
            reopened.Remember("Новый",cli,now,false);
            Assert(reopened.Preferred(cli,"")=="Новый","A newer explicitly applied profile wins over an older successful session.");
            Assert(reopened.Preferred("D:\\Other\\wiresock-connect-cli.exe","Другой")=="Другой","Different CLI installation must not inherit another profile.");
            Assert(UpdateJournal.BlockReason(folder,false,false,false,false)==null,"Disconnected fixture accepts an update.");
            Assert(UpdateJournal.BlockReason(folder,false,false,false,true)!=null,"Another installation's engine blocks update.");
            Assert(UpdateJournal.BlockReason(folder,false,false,true,false)!=null,"Diagnostics blocks update.");
            AppPaths.Write(Path.Combine(folder,"runtime","session.json"),new{running=true});
            Assert(UpdateJournal.BlockReason(folder,false,false,false,false)!=null,"Stale running=true does not authorize an update.");
            File.WriteAllText(Path.Combine(folder,"runtime","session.json"),"broken");
            Assert(UpdateJournal.BlockReason(folder,false,false,false,false)!=null,"Unreadable session fails closed.");
            File.Delete(Path.Combine(folder,"runtime","session.json"));
            using(var held=new FileStream(Path.Combine(folder,"runtime","network-control.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                Assert(UpdateJournal.BlockReason(folder,false,false,false,false)!=null,"Concurrent Connect/Stop reservation blocks update.");
            string payload=Path.Combine(folder,"payload");Directory.CreateDirectory(payload);
            var entries=new[]{"RoutePilot.exe","SmartRouting.ps1","Install-Agent.ps1","Configure-Connection.ps1","product.json"}.Select(name=>{
                string path=Path.Combine(payload,name);File.WriteAllText(path,"fixture");return new{path=name,bytes=new FileInfo(path).Length,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))};
            }).ToArray();
            File.WriteAllText(Path.Combine(payload,"manifest.json"),JsonSerializer.Serialize(new{version="9.0.0",architecture="win-x64",selfContained=true,files=entries}));
            string sourceZip=Path.Combine(folder,"source.zip");ZipFile.CreateFromDirectory(payload,sourceZip);
            byte[] bytes=File.ReadAllBytes(sourceZip);string digest=Convert.ToHexString(SHA256.HashData(bytes));
            using var http=new HttpClient(new Handler(bytes,digest));var releases=new ReleaseUpdates(http);
            using var metadata=JsonDocument.Parse(Handler.Metadata(bytes.Length));
            var release=ReleaseUpdates.Parse(metadata.RootElement,new Version(0,0,0))!;
            string package=await releases.Download(release,folder,null,CancellationToken.None);
            var prepared=UpdateJournal.Prepare(folder,release,package);
            UpdateJournal.Save(folder,new UpdateState{Phase="Verified",Prepared=prepared,NotifiedTag=release.Tag});
            var restored=UpdateJournal.Read(folder);
            Assert(restored.Prepared==prepared && restored.NotifiedTag==release.Tag,"Downloaded update and notification survive restart.");
            var retries=new UpdateState{Prepared=prepared};
            for(int attempt=0;attempt<3;attempt++)
            {
                UpdateJournal.DeferNetwork(retries,now);UpdateJournal.Save(folder,retries);retries=UpdateJournal.Read(folder);
                Assert(retries.Phase=="WaitingForNetwork" && retries.NetworkRetries==attempt+1 && retries.NextRetryUtc>now,"Network retry budget survives process restart.");
            }
            UpdateJournal.DeferNetwork(retries,now);Assert(retries.Phase=="Failed","Network errors cannot create an unlimited automatic install loop.");
            var verified=await releases.Restore(restored.Prepared!,folder,CancellationToken.None);
            Assert(verified.Package==package && verified.ManifestHash.Length==64,"Saved update revalidated against fresh trusted release metadata.");
            bool refused=false;try{UpdateJournal.StagedPath(folder,"../outside");}catch(InvalidDataException){refused=true;}Assert(refused,"Journal path traversal rejected.");
            // Editing both the extracted file and its manifest cannot bypass the
            // archive's freshly verified manifest identity.
            string manifest=Path.Combine(package,"manifest.json");
            File.WriteAllText(manifest,File.ReadAllText(manifest).Replace("fixture","edited"));
            File.AppendAllText(manifest," ");
            refused=false;try{await releases.Restore(prepared,folder,CancellationToken.None);}catch(InvalidDataException){refused=true;}Assert(refused,"Locally changed manifest rejected even when it parses.");
        }
        finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
    sealed class Handler(byte[] archive,string hash):HttpMessageHandler
    {
        public static string Metadata(int bytes)=>JsonSerializer.Serialize(new{draft=false,prerelease=false,tag_name="v9.0.0",body="Изменения <script>plain text</script>",assets=new[]{
            new{name="CITA-Route-9.0.0-win-x64.zip",size=bytes,browser_download_url="https://github.com/IthilRosg/CITA-Route/releases/download/v9.0.0/CITA-Route-9.0.0-win-x64.zip"},
            new{name="SHA256SUMS.txt",size=100,browser_download_url="https://github.com/IthilRosg/CITA-Route/releases/download/v9.0.0/SHA256SUMS.txt"}}});
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){RequestMessage=request,
            Content=request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt")?new ByteArrayContent(Encoding.UTF8.GetBytes(hash+"  CITA-Route-9.0.0-win-x64.zip\n")):
                request.RequestUri.Host=="api.github.com"?new StringContent(Metadata(archive.Length)):new ByteArrayContent(archive)});
    }
}
