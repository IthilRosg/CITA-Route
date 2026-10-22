using System.ComponentModel;
using System.IO.Compression;
using System.Text.Json;
using Cita.Diagnostics;
using RoutePilot.Resources;

namespace RoutePilot.Agent;

internal static class DiagnosticJournalTests
{
    static void Check(bool value,string reason){if(!value)throw new Exception(reason);}
    internal static void Run(string? fixtureParent=null)
    {
        string root=Path.Combine(fixtureParent??Path.GetTempPath(),"CitaDiagnosticJournal-"+Guid.NewGuid().ToString("N"));
        string service=Path.Combine(root,"service");Directory.CreateDirectory(service);Directory.CreateDirectory(Path.Combine(root,"logs"));
        var now=DateTime.UtcNow.AddHours(-1);string id=Guid.NewGuid().ToString("N");
        JsonElement Snapshot(DateTime at,bool healthy,int generation=1,bool running=true,DateTime? probe=null,bool matches=true)=>JsonSerializer.SerializeToElement(new
        {
            source="ordinary",observedUtc=at,sessionId=id,bridgeGeneration=generation,running,consistent=true,bridgeReady=true,
            health=new{healthy,fresh=true,matchesGeneration=matches,code=healthy?"Verified":"ControlHttpsUnavailable",updatedUtc=probe??at,
                attempts=new[]{new{Provider="https://secret.invalid/?token=DO-NOT-COLLECT",Stage="TCP",Outcome="Failed",Milliseconds=15,Deadline="None",Error="DO-NOT-COLLECT",PrivateKey="DO-NOT-COLLECT"}}}
        });
        string path=Path.Combine(service,"diagnostic-incidents-ordinary.json");
        JsonElement State(){using var doc=JsonDocument.Parse(File.ReadAllText(path));return doc.RootElement.Clone();}
        void Observe(JsonElement value,DateTime at)=>SessionEvidence.ObserveIncident(root,"ordinary",service,value,at);
        try
        {
            var matched=SessionEvidence.InspectProcess(42,"expected",now,()=> ("EXPECTED",now,false));
            var denied=SessionEvidence.InspectProcess(42,"expected",now,()=>throw new Win32Exception(5,"DO-NOT-COLLECT"));
            var reused=SessionEvidence.InspectProcess(42,"expected",now,()=> ("expected",now.AddSeconds(1),false));
            Check(matched is {Status:"Matched",Owned:true}&&denied is {Status:"Unavailable",Owned:null,Reason:"AccessDenied",Error:5}&&reused is {Status:"Mismatch",Owned:false,Reason:"StartMismatch"},"Access denial, PID reuse and a verified process must remain distinct.");
            Directory.CreateDirectory(Path.Combine(root,"runtime"));
            File.WriteAllText(Path.Combine(root,"runtime","session.json"),JsonSerializer.Serialize(new{id,running=true}));
            var watchIdentity=new WatchResourceIdentity.Identity{Schema=1,Pid=42,Path=WatchResourceIdentity.Image,StartedUtc=now,SessionId=id};
            string watchFile=Path.Combine(root,"runtime","watch-process.json");File.WriteAllText(watchFile,JsonSerializer.Serialize(watchIdentity));
            Check(WatchResourceIdentity.Load(root,out bool limited)!=null&&!limited&&WatchResourceIdentity.Matches(watchIdentity,42,now,WatchResourceIdentity.Image),"Valid watcher metadata must identify counters without inspecting command lines.");
            Check(!WatchResourceIdentity.Matches(watchIdentity,42,now.AddSeconds(1),WatchResourceIdentity.Image)&&!WatchResourceIdentity.Matches(watchIdentity,42,now,"other.exe"),"A reused PID or different image cannot be counted as the watcher.");
            watchIdentity.SessionId=Guid.NewGuid().ToString("N");File.WriteAllText(watchFile,JsonSerializer.Serialize(watchIdentity));
            Check(WatchResourceIdentity.Load(root,out limited)==null&&limited,"Old-session watcher counters must stay unavailable.");
            File.WriteAllText(Path.Combine(root,"logs","bridge.log"),"A preserved failure\nPrivateKey = DO-NOT-COLLECT\n");
            Observe(Snapshot(now,true),now);
            Observe(Snapshot(now.AddSeconds(1),false),now.AddSeconds(1));
            Observe(Snapshot(now.AddSeconds(25),false,probe:now.AddSeconds(1)),now.AddSeconds(25));
            Observe(Snapshot(now.AddSeconds(26),false,probe:now),now.AddSeconds(26));
            Check(State().GetProperty("Incidents").GetArrayLength()==0,"Re-reading one failed probe cannot create a sustained incident.");
            Observe(Snapshot(now.AddSeconds(31),false),now.AddSeconds(31));
            Check(State().GetProperty("Incidents").GetArrayLength()==1,"Two distinct current-generation failures must persist a bounded incident.");
            File.WriteAllText(Path.Combine(root,"logs","bridge.log"),"Rotated away");
            Observe(Snapshot(now.AddSeconds(35),false,generation:2,matches:false),now.AddSeconds(35));
            Check(State().GetProperty("Incidents")[0].GetProperty("Phase").GetString()=="Superseded","A new bridge generation must close the old incident rather than borrow its failures.");
            Check(State().GetProperty("Incidents")[0].GetProperty("Logs").GetProperty("bridge.log").GetString()!.Contains("A preserved failure"),"Incident evidence must survive subsequent log rotation.");
            Observe(Snapshot(now.AddSeconds(36),false,generation:2),now.AddSeconds(36));
            Check(State().GetProperty("Incidents").GetArrayLength()==1,"A new generation requires a new confirmation period.");
            Observe(Snapshot(now.AddSeconds(60),true,generation:2),now.AddSeconds(60));
            Observe(Snapshot(now.AddSeconds(61),false,generation:2),now.AddSeconds(61));
            Observe(Snapshot(now.AddSeconds(91),false,generation:2),now.AddSeconds(91));
            Observe(Snapshot(now.AddSeconds(100),true,generation:2),now.AddSeconds(100));
            var recovered=State().GetProperty("Incidents")[1];
            Check(recovered.GetProperty("Phase").GetString()=="Recovered"&&recovered.GetProperty("Before").GetProperty("health").GetProperty("healthy").GetBoolean(),"Recovery must retain the prior healthy snapshot and incident evidence.");
            foreach(string name in new[]{"watch-error.log","policy-error.log","bridge.log","router.log"})File.WriteAllText(Path.Combine(root,"logs",name),new string('\u0001',8192));
            for(int i=0;i<10;i++)
            {
                var at=now.AddMinutes(3+i);Observe(Snapshot(at,false,generation:2),at);Observe(Snapshot(at.AddSeconds(25),false,generation:2),at.AddSeconds(25));Observe(Snapshot(at.AddSeconds(30),true,generation:2),at.AddSeconds(30));
            }
            Check(State().GetProperty("Incidents").GetArrayLength()==8,"Repeated failures must not grow incident storage indefinitely.");
            Check(new FileInfo(path).Length<1024*1024,"Worst-case JSON escaping in all four log tails must fit the incident budget.");
            var stopped=now.AddMinutes(14);Observe(Snapshot(stopped,false,generation:2),stopped);Observe(Snapshot(stopped.AddSeconds(25),false,generation:2),stopped.AddSeconds(25));
            Observe(Snapshot(stopped.AddSeconds(30),false,generation:2,running:false),stopped.AddSeconds(30));
            Check(State().GetProperty("Incidents")[7].GetProperty("Phase").GetString()=="Stopped","Intentional Stop is not a recovery or a new outage.");
            string gapService=Path.Combine(root,"gap");Directory.CreateDirectory(gapService);
            void Gap(DateTime at)=>SessionEvidence.ObserveIncident(root,"ordinary",gapService,Snapshot(at,false),at);
            Gap(now);Gap(now.AddMinutes(10));
            string gapPath=Path.Combine(gapService,"diagnostic-incidents-ordinary.json");
            using(var value=JsonDocument.Parse(File.ReadAllText(gapPath)))Check(value.RootElement.GetProperty("Incidents").GetArrayLength()==0,"A long observation gap cannot certify continuous failure.");
            Gap(now.AddMinutes(10).AddSeconds(30));Gap(now.AddMinutes(10).AddSeconds(20));
            using(var value=JsonDocument.Parse(File.ReadAllText(gapPath)))Check(value.RootElement.GetProperty("Incidents")[0].GetProperty("Phase").GetString()=="Interrupted","Clock rollback must interrupt the observation period without reconnecting anything.");

            ResourceState Resources(DateTime at,DateTime started,long cpu)=>new(){UpdatedUtc=at,Sample=new(){ObservedUtc=at,ProcessesObservedUtc=at,Processes=new(){new()
            {Pid=42,StartedUtc=started,ObservedUtc=at,Name="smart-router",CitaComponent=true,PrivateBytes=1000,WorkingSetBytes=2000,CpuTotalMilliseconds=cpu,Handles=20,UdpPorts=3},new(){Pid=43,Name="DO-NOT-COLLECT",CitaComponent=false}}}};
            SessionEvidence.ObserveResources(service,Resources(now,now.AddMinutes(-1),100));
            SessionEvidence.ObserveResources(service,Resources(now.AddSeconds(10),now.AddMinutes(-1),200));
            SessionEvidence.ObserveResources(service,Resources(now.AddMinutes(1),now.AddMinutes(-1),700));
            SessionEvidence.ObserveResources(service,Resources(now.AddMinutes(2),now.AddMinutes(2),100));
            string history=Path.Combine(service,"resource-history.jsonl");var lines=File.ReadAllLines(history);
            Check(lines.Length==3,"Minute history must not repeat every monitor sample.");
            using(var a=JsonDocument.Parse(lines[1]))using(var b=JsonDocument.Parse(lines[2]))
            {
                var components=a.RootElement.GetProperty("components");Check(components.GetArrayLength()==1,"Unrelated applications must not enter exported resource history.");
                Check(Math.Abs(components[0].GetProperty("machineCpuPercent").GetDouble()-1d/Environment.ProcessorCount)<0.001,"CPU must use the same PID/start and actual elapsed interval.");
                Check(b.RootElement.GetProperty("components")[0].GetProperty("machineCpuPercent").ValueKind==JsonValueKind.Null,"PID reuse must reset CPU evidence rather than produce false growth.");
            }
            File.AppendAllText(history,"{\"observedUtc\":\""+DateTime.UtcNow.ToString("O")+"\",\"privateKey\":\"DO-NOT-COLLECT\",\"components\":[{\"role\":\"DO-NOT-COLLECT\"}]}\n");
            // Export re-projects even an externally corrupted incident journal.
            File.WriteAllText(path,"{bad");Observe(Snapshot(stopped.AddMinutes(1),true),stopped.AddMinutes(1));
            File.WriteAllText(Path.Combine(root,"logs","bridge.log"),"A preserved failure\nPrivateKey = DO-NOT-COLLECT\n");
            Observe(Snapshot(stopped.AddMinutes(2),false),stopped.AddMinutes(2));Observe(Snapshot(stopped.AddMinutes(2).AddSeconds(25),false),stopped.AddMinutes(2).AddSeconds(25));
            string zipPath=Path.Combine(root,"export.zip");SessionEvidence.Export(root,zipPath,service);
            using(var zip=ZipFile.OpenRead(zipPath))
            {
                Check(zip.GetEntry("history/resources.json")!=null&&zip.GetEntry("history/incidents-ordinary.json")!=null,"Export must include bounded resource and incident histories.");
                string text=string.Join("\n",zip.Entries.Select(e=>{using var reader=new StreamReader(e.Open());return reader.ReadToEnd();}));
                Check(!text.Contains("DO-NOT-COLLECT")&&!text.Contains("secret.invalid"),"History export leaked arbitrary fields, endpoint identity or exception text.");
            }
            using(var held=new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                SessionEvidence.Observe(root,"ordinary",service,DateTime.UtcNow);
            Check(File.Exists(Path.Combine(service,"session-evidence-ordinary.jsonl")),"Incident lock contention must not disable the original session history.");
            File.WriteAllText(history,new string('x',8*1024*1024+1));
            SessionEvidence.ObserveResources(service,Resources(now.AddMinutes(3),now.AddMinutes(2),200));
            Check(new FileInfo(history).Length<8*1024*1024,"Corrupt or oversized resource history must compact before appending.");
        }
        finally
        {
            string parent=Path.GetFullPath(fixtureParent??Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!Path.GetFullPath(root).StartsWith(parent,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe diagnostic fixture cleanup.");
            Directory.Delete(root,true);
        }
    }
}
