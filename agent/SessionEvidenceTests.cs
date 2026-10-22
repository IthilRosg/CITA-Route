using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Cita.Diagnostics;

namespace RoutePilot.Agent;

internal static class SessionEvidenceTests
{
    internal static void Run(string? fixtureParent=null)
    {
        void Check(bool ok,string reason){if(!ok)throw new Exception(reason);}
        string root=Path.Combine(fixtureParent??Path.GetTempPath(),"CitaEvidence-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,"runtime"));Directory.CreateDirectory(Path.Combine(root,"logs"));Directory.CreateDirectory(Path.Combine(root,"private"));
        string service=Path.Combine(root,"service");Directory.CreateDirectory(service);
        string sessionPath=Path.Combine(root,"runtime","session.json"),healthPath=Path.Combine(root,"runtime","session-health.json");
        var now=DateTime.UtcNow;string id=Guid.NewGuid().ToString("N"),next=Guid.NewGuid().ToString("N");
        try
        {
            void Session(string identifier,int generation,bool running=true)=>File.WriteAllText(sessionPath,JsonSerializer.Serialize(new{id=identifier,bridgeGeneration=generation,running,bridgeReady=running,PrivateKey="DO-NOT-COLLECT-SESSION-SECRET"}));
            Session(id,1);File.WriteAllText(healthPath,JsonSerializer.Serialize(new{sessionId=id,bridgeGeneration=1,healthy=true,code="Verified",updatedUtc=now}));
            SessionEvidence.Observe(root,"ordinary",service,now);
            SessionEvidence.Observe(root,"ordinary",service,now.AddSeconds(5));
            string history=Path.Combine(service,"session-evidence-ordinary.jsonl");
            Check(File.ReadAllLines(history).Length==1,"Unchanged observation must not write a full journal every sample.");
            Session(id,2);SessionEvidence.Observe(root,"ordinary",service,now.AddSeconds(10));
            using(var document=JsonDocument.Parse(JsonSerializer.Serialize(SessionEvidence.Snapshot(root,"ordinary",now))))
                Check(!document.RootElement.GetProperty("health").GetProperty("healthy").GetBoolean(),"Old-generation HTTPS must not prove the new bridge healthy.");
            Session(next,1);SessionEvidence.Observe(root,"ordinary",service,now.AddSeconds(15));
            Session(next,1,false);SessionEvidence.Observe(root,"ordinary",service,now.AddSeconds(20));
            Check(File.ReadAllLines(history).Length==4,"Reconnect and manual Stop must retain earlier session observations.");
            string secret=new('A',43);secret+="=";
            File.WriteAllText(Path.Combine(root,"private","profile.conf"),"DO-NOT-COLLECT-CONFIGURATION");
            File.WriteAllText(Path.Combine(root,"runtime","controller-token.txt"),"DO-NOT-COLLECT-TOKEN");
            File.WriteAllText(Path.Combine(root,"logs","watch-error.log"),"PrivateKey = "+secret+"\nAuthorization: Bearer private\nhttps://example.org/?token=private\nopaque "+secret+"\n",Encoding.UTF8);
            File.WriteAllText(Path.Combine(root,"logs","last-action.log"),new string('X',150000)+"\nPresharedKey = "+secret,Encoding.Unicode);
            File.WriteAllText(Path.Combine(root,"logs","resource-check-last.json"),JsonSerializer.Serialize(new{domain="cdn.example.com",pathHash="0123456789AB",sessionId=id,bridgeGeneration=1,finishedUtc=now,
                PrivateKey="DO-NOT-COLLECT-RESOURCE-SECRET",url="https://example.com/private?token=private",attempts=new[]{new{route="vpn",status=200,bytes=123,complete=true,token="DO-NOT-COLLECT-ATTEMPT-SECRET"}}}));
            File.AppendAllText(history,"{\"observedUtc\":\""+now.ToString("O")+"\",\"source\":\"ordinary\",\"PrivateKey\":\"DO-NOT-COLLECT-JOURNAL-SECRET\"}\n{incomplete");
            string destination=Path.Combine(root,"report.zip");SessionEvidence.Export(root,destination,service);
            using(var zip=ZipFile.OpenRead(destination))
            {
                string all=string.Join("\n",zip.Entries.Select(e=>{using var reader=new StreamReader(e.Open());return reader.ReadToEnd();}));
                Check(!all.Contains("DO-NOT-COLLECT")&&!all.Contains(secret)&&!all.Contains("Bearer private")&&!all.Contains("token=private"),"The diagnostic archive disclosed private configuration, tokens or an unprojected journal.");
                Check(zip.Entries.All(e=>!e.FullName.Contains("private/")&&!e.FullName.Contains("controller")),"Private files must never be enumerated into the archive.");
                Check(zip.Entries.Any(e=>e.FullName=="history/ordinary.json"),"Export lost persisted observations.");
                Check(zip.Entries.Any(e=>e.FullName=="checks/resource.json"),"Export lost the projected resource-body diagnostic.");
                Check(zip.Entries.All(e=>e.Length<1500000),"Export entries must be bounded snapshots.");
            }
            bool overwrite=false;try{SessionEvidence.Export(root,destination,service);}catch(IOException){overwrite=true;}
            Check(overwrite,"Diagnostic collection must not overwrite an existing destination without the UI save flow.");
            File.WriteAllText(history,new string('x',4*1024*1024+8192));
            Session(next,2);SessionEvidence.Observe(root,"ordinary",service,now.AddSeconds(25));
            Check(new FileInfo(history).Length<4*1024*1024,"Oversized or corrupted history must remain bounded after the next append.");
        }
        finally
        {
            string parent=Path.GetFullPath(fixtureParent??Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!Path.GetFullPath(root).StartsWith(parent,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe diagnostic fixture cleanup.");
            Directory.Delete(root,true);
        }
    }
}
