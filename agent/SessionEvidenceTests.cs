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
            Directory.CreateDirectory(Path.Combine(root,"data"));
            File.WriteAllText(Path.Combine(root,"data","wiresock-maintenance.json"),JsonSerializer.Serialize(new{schema=1,phase="NeedsAttention",updatedUtc=now,
                PrivateKey="DO-NOT-COLLECT-DEPENDENCY-SECRET",url="https://example.com/private?token=private"}));
            string destination=Path.Combine(root,"report.zip");SessionEvidence.Export(root,destination,service);
            using(var zip=ZipFile.OpenRead(destination))
            {
                string all=string.Join("\n",zip.Entries.Select(e=>{using var reader=new StreamReader(e.Open());return reader.ReadToEnd();}));
                Check(!all.Contains("DO-NOT-COLLECT")&&!all.Contains(secret)&&!all.Contains("Bearer private")&&!all.Contains("token=private"),"The diagnostic archive disclosed private configuration, tokens or an unprojected journal.");
                Check(zip.Entries.All(e=>!e.FullName.Contains("private/")&&!e.FullName.Contains("controller")),"Private files must never be enumerated into the archive.");
                Check(zip.Entries.Any(e=>e.FullName=="history/ordinary.json"),"Export lost persisted observations.");
                Check(zip.Entries.Any(e=>e.FullName=="checks/resource.json"),"Export lost the projected resource-body diagnostic.");
                using(var reportReader=new StreamReader(zip.GetEntry("report.json")!.Open()))
                using(var report=JsonDocument.Parse(reportReader.ReadToEnd()))
                    Check(report.RootElement.GetProperty("dependencies").GetProperty("wireSockMaintenance").GetProperty("phase").GetString()=="NeedsAttention","Dependency maintenance state must be exported without arbitrary fields.");
                Check(zip.Entries.All(e=>e.Length<1500000),"Export entries must be bounded snapshots.");
            }
            bool overwrite=false;try{SessionEvidence.Export(root,destination,service);}catch(IOException){overwrite=true;}
            Check(overwrite,"Diagnostic collection must not overwrite an existing destination without the UI save flow.");
            string nativeZip=Path.Combine(root,"native-report.zip");var fake=JsonSerializer.SerializeToElement(new{phase="Connected",desired=true,generation=7,errorStage="Connecting",profileName="DO-NOT-COLLECT-NATIVE-PROFILE",PrivateKey="DO-NOT-COLLECT-NATIVE-KEY",runtime=new{phase="Connected",coreState="CoreUpdateRequired",repairs=2,automaticRouting="CheckingZapret",zapretState="Available",zapretProbeReason="zapret-tls-certificate",zapretFailure=new{stage="verify",reason="io",extra="DO-NOT-COLLECT-NATIVE-FAILURE"},zapretConnector=new{scope="current-connector",accepted=3,rejected=1,extra="DO-NOT-COLLECT-NATIVE-CONNECTOR"},calibrationChecks=9,calibrationPassed=5,calibrationFailed=3,calibrationErrors=1,validation=new{httpsAttempts=2,httpsFailure="Timeout",httpsPhase="TlsHandshake",udpVerified=false,udpAttempts=0,secret="DO-NOT-COLLECT-NATIVE-VALIDATION"},transportHealth=new{state="Reachable",https=true,udp=false,completedUnixMilliseconds=123L,socketRepairs=1,restarts=0,token="DO-NOT-COLLECT-NATIVE-TOKEN"}}});
            SessionEvidence.Export(root,nativeZip,service,nativeEdition:true,nativeSnapshot:fake);
            using(var nativeArchive=ZipFile.OpenRead(nativeZip))using(var reader=new StreamReader(nativeArchive.GetEntry("report.json")!.Open()))using(var report=JsonDocument.Parse(reader.ReadToEnd()))
            {string content=report.RootElement.GetRawText();Check(!content.Contains("DO-NOT-COLLECT-NATIVE"),"Native diagnostic must never export profiles, keys, tokens or arbitrary fields.");var native=report.RootElement.GetProperty("native");Check(report.RootElement.GetProperty("edition").GetString()=="native"&&native.GetProperty("phase").GetString()=="Connected"&&native.GetProperty("generation").GetInt64()==7&&native.GetProperty("errorStage").GetString()=="Connecting"&&native.GetProperty("runtime").GetProperty("coreState").GetString()=="CoreUpdateRequired"&&native.GetProperty("runtime").GetProperty("automaticRouting").GetString()=="CheckingZapret"&&native.GetProperty("runtime").GetProperty("zapretState").GetString()=="Available"&&native.GetProperty("runtime").GetProperty("zapretProbeReason").GetString()=="zapret-tls-certificate"&&native.GetProperty("runtime").GetProperty("zapretFailure").GetProperty("stage").GetString()=="verify"&&native.GetProperty("runtime").GetProperty("zapretConnector").GetProperty("accepted").GetInt64()==3&&native.GetProperty("runtime").GetProperty("calibrationChecks").GetInt64()==9&&native.GetProperty("runtime").GetProperty("calibrationFailed").GetInt64()==3&&native.GetProperty("runtime").GetProperty("validation").GetProperty("httpsPhase").GetString()=="TlsHandshake"&&native.GetProperty("runtime").GetProperty("transport").GetProperty("udp").ValueKind==JsonValueKind.False,"Native report must retain current state and protocol health.");}
            var hostile=JsonSerializer.SerializeToElement(new{phase="PRIVATEKEYSENTINEL",errorCode="PRIVATEKEYSENTINEL",profileName="PRIVATEKEYSENTINEL",runtime=new{zapretState="PRIVATEKEYSENTINEL",zapretProbeReason="PRIVATEKEYSENTINEL",zapretFailure=new{stage="PRIVATEKEYSENTINEL",reason="PRIVATEKEYSENTINEL"},zapretConnector=new{scope="PRIVATEKEYSENTINEL",accepted="PRIVATEKEYSENTINEL",rejected="PRIVATEKEYSENTINEL"}}});string hostileZip=Path.Combine(root,"hostile-report.zip");SessionEvidence.Export(root,hostileZip,service,nativeEdition:true,nativeSnapshot:hostile);using(var z=ZipFile.OpenRead(hostileZip))using(var reader=new StreamReader(z.GetEntry("report.json")!.Open()))Check(!reader.ReadToEnd().Contains("PRIVATEKEYSENTINEL"),"Unrecognised native codes must not be exported.");
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
