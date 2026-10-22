using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using RoutePilot.Resources;

namespace Cita.Diagnostics;

public static partial class SessionEvidence
{
    static string Allowed(string text,params string[] values)=>values.Contains(text)?text:"Unknown";
    static long? OptionalLong(JsonElement value,string key)=>Get(value,key).TryGetNonnegativeLong();
    static int? OptionalInt(JsonElement value,string key)=>Get(value,key).TryInt(out int number)?number:null;
    static string ResourceRole(string name)=>name switch{"RoutePilot"=>"GUI","RoutePilot.Agent"=>"Agent","smart-router"=>"Router","vpn-bridge"=>"Bridge","powershell"=>"Watcher",_=>"Component"};
    static ProcessOwnership SafeOwnership(JsonElement value,string key,string legacy)
    {
        var item=Get(value,key);string status=Allowed(Text(item,"status"),"Matched","Mismatch","Unavailable");
        if(status=="Unknown")return Flag(value,legacy)?new("Matched","LegacyConfirmation"):new("Unavailable","LegacyUnconfirmed");
        return new(status,Allowed(Text(item,"reason"),"Verified","MissingIdentity","Exited","ImageUnavailable","ImageMismatch","StartMismatch","AccessDenied","ReadFailed","ProcessChanged","Unsupported","LegacyConfirmation","LegacyUnconfirmed"),OptionalInt(item,"error"));
    }
    static object[] SafeAttempts(JsonElement values)=>values.ValueKind!=JsonValueKind.Array?Array.Empty<object>():values.EnumerateArray().Take(2).Select(v=>(object)new
    {
        provider=Allowed(Text(v,"provider"),"Cloudflare","Ipify","Custom"),
        stage=Allowed(Text(v,"stage"),"Complete","DNS","TCP","TLS","Proxy","HTTP","Body","Response","Request"),
        outcome=Allowed(Text(v,"outcome"),"Succeeded","Failed","Timeout","Cancelled"),
        milliseconds=Math.Clamp(Number(v,"milliseconds"),0,60000),deadline=Allowed(Text(v,"deadline"),"None","Caller","Attempt","Overall"),
        httpStatus=OptionalInt(v,"httpStatus") is >=100 and <=599?OptionalInt(v,"httpStatus"):null,
        socketError=OptionalInt(v,"socketError"),
        error=Allowed(Text(v,"error"),"None","Unknown","NameResolutionError","ConnectionError","SecureConnectionError","HttpProtocolError","ExtendedConnectNotSupported","VersionNegotiationError","UserAuthenticationError","ProxyTunnelError","InvalidResponse","ResponseEnded","ConfigurationLimitExceeded","Cancelled","IO","Unexpected")
    }).ToArray();

    // Journals are independent of route recovery. None of these methods start,
    // stop, reconnect or learn a VPN exit. All imported records are re-projected.
    sealed class Incident
    {
        public string SessionId{get;set;}="";
        public int Generation{get;set;}
        public string Code{get;set;}="";
        public string Phase{get;set;}="Open";
        public DateTime FirstFailureUtc{get;set;}
        public DateTime DetectedUtc{get;set;}
        public DateTime? FinishedUtc{get;set;}
        public JsonElement? Before{get;set;}
        public JsonElement First{get;set;}
        public JsonElement Detected{get;set;}
        public JsonElement? Final{get;set;}
        public JsonElement Resources{get;set;}
        public Dictionary<string,string> Logs{get;set;}=new();
    }
    sealed class IncidentState
    {
        public int Schema{get;set;}=1;
        public string Scope{get;set;}="";
        public DateTime UpdatedUtc{get;set;}
        public DateTime? LastProbeUtc{get;set;}
        public JsonElement? LastHealthy{get;set;}
        public JsonElement? FirstFailure{get;set;}
        public DateTime? FirstFailureUtc{get;set;}
        public int DistinctProbes{get;set;}
        public List<Incident> Incidents{get;set;}=new();
    }
    static JsonElement Project(object value)=>JsonSerializer.SerializeToElement(value);
    static JsonElement Observation(JsonElement value)=>Project(SafeObservation(value));
    static void WriteJournal(string path,object value,int limit)
    {
        if(!Regular(path))return;
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(value);
        if(bytes.Length>limit)throw new InvalidDataException("Diagnostic journal exceeded its bounded schema.");
        string temporary=Path.Combine(Path.GetDirectoryName(path)!,"."+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes);output.Flush(true);}
            File.Move(temporary,path,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    static IncidentState ReadIncidents(string path,string root="")
    {
        try
        {
            var value=Read(path,1024*1024);
            if(value.ValueKind!=JsonValueKind.Object)return new();
            var state=value.Deserialize<IncidentState>(Json);
            if(state is not {Schema:1,Incidents:{Count:<=8}}||state.Scope==null||state.Scope.Length>100||state.DistinctProbes is <0 or >10000||state.Incidents.Any(i=>i==null))return new();
            if(state.LastHealthy is {} healthy)state.LastHealthy=Observation(healthy);
            if(state.FirstFailure is {} first)state.FirstFailure=Observation(first);
            state.Incidents=state.Incidents.Select(i=>Project(SafeIncident(i,root)).Deserialize<Incident>(Json)!).ToList();
            return state;
        }
        catch(JsonException){return new();}
    }
    static readonly string[] IncidentLogs={"watch-error.log","policy-error.log","bridge.log","router.log"};
    internal static void ObserveIncident(string root,string source,string directory,JsonElement snapshot,DateTime now)
    {
        if(source is not ("ordinary" or "startup")||!Regular(directory))return;
        Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"diagnostic-incidents-"+source+".json");
        if(!Regular(path)||!Regular(path+".lock"))return;
        using var lease=new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var state=ReadIncidents(path,root);string id=Id(snapshot,"sessionId");int generation=Number(snapshot,"bridgeGeneration");
        string scope=id+":"+generation;var health=Get(snapshot,"health");
        var active=state.Incidents.LastOrDefault(i=>i.Phase=="Open");
        bool running=Flag(snapshot,"running"),consistent=Flag(snapshot,"consistent");
        if(!consistent)return;
        if(state.UpdatedUtc!=default&&(now<state.UpdatedUtc||now-state.UpdatedUtc>TimeSpan.FromSeconds(90)))
        {
            if(active!=null){active.Phase="Interrupted";active.FinishedUtc=now;active.Final=Observation(snapshot);}
            state.LastProbeUtc=null;state.FirstFailure=null;state.FirstFailureUtc=null;state.DistinctProbes=0;
            state.LastHealthy=null;active=null;
        }
        if(!running||scope!=state.Scope)
        {
            if(!running&&scope==state.Scope&&active==null&&state.LastHealthy==null&&state.FirstFailure==null)return;
            if(active!=null){active.Phase=running?"Superseded":"Stopped";active.FinishedUtc=now;active.Final=Observation(snapshot);}
            state.Scope=scope;state.LastHealthy=null;state.FirstFailure=null;state.FirstFailureUtc=null;state.LastProbeUtc=null;state.DistinctProbes=0;
            state.UpdatedUtc=now;WriteJournal(path,state,1024*1024);
            if(!running||id.Length==0)return;
        }
        if(!Flag(health,"matchesGeneration")||!Flag(health,"fresh"))return;
        DateTime? probeAt=At(health,"updatedUtc");
        if(probeAt==null||(state.LastProbeUtc is {} lastProbe&&probeAt.Value<=lastProbe))return;
        state.LastProbeUtc=probeAt;
        if(Flag(health,"healthy"))
        {
            active=state.Incidents.LastOrDefault(i=>i.Phase=="Open");
            if(active!=null){active.Phase="Recovered";active.FinishedUtc=now;active.Final=Observation(snapshot);}
            state.LastHealthy=Observation(snapshot);state.FirstFailure=null;state.FirstFailureUtc=null;state.DistinctProbes=0;
        }
        else if(Code(health,"code") is "ControlHttpsUnavailable" or "StatusUnavailable" or "ProfileNotConnected" or "EgressMismatch" or "ProfileChanged" or "InvalidConfiguration" or "MissingExpectedEgress")
        {
            state.FirstFailure??=Observation(snapshot);state.FirstFailureUtc??=now;state.DistinctProbes=Math.Min(10000,state.DistinctProbes+1);
            if(state.DistinctProbes>=2&&now-state.FirstFailureUtc.Value>=TimeSpan.FromSeconds(20)&&!state.Incidents.Any(i=>i.Phase=="Open"))
            {
                var incident=new Incident{SessionId=id,Generation=generation,Code=Code(health,"code"),FirstFailureUtc=state.FirstFailureUtc.Value,
                    DetectedUtc=now,Before=state.LastHealthy,First=state.FirstFailure.Value,Detected=Observation(snapshot),
                    Resources=Project(ResourceSummary(Read(Path.Combine(directory,"resource-state.json"))))};
                foreach(string name in IncidentLogs)
                {
                    string log=Path.Combine(root,"logs",name);
                    if(!Regular(log)||!File.Exists(log))continue;
                    // Tail first (bounded read), then redact and cap. No configurations.
                    string text=Tail(log,root);incident.Logs[name]=Redact(text.Length>2048?text[^2048..]:text,root);
                }
                state.Incidents.Add(incident);state.Incidents=state.Incidents.TakeLast(8).ToList();
            }
        }
        state.UpdatedUtc=now;WriteJournal(path,state,1024*1024);
    }
    static object SafeIncident(Incident item,string root)=>new
    {
        sessionId=Guid.TryParse(item.SessionId,out var id)?id.ToString("N"):"",generation=Math.Max(0,item.Generation),
        code=Allowed(item.Code,"ControlHttpsUnavailable","StatusUnavailable","ProfileNotConnected","EgressMismatch","ProfileChanged","InvalidConfiguration","MissingExpectedEgress"),
        phase=Allowed(item.Phase,"Open","Recovered","Stopped","Superseded","Interrupted"),item.FirstFailureUtc,item.DetectedUtc,item.FinishedUtc,
        before=item.Before is {} before?SafeObservation(before):null,first=SafeObservation(item.First),detected=SafeObservation(item.Detected),
        final=item.Final is {} final?SafeObservation(final):null,
        resources=SafeResourceRow(item.Resources),
        logs=IncidentLogs.Where(name=>item.Logs?.ContainsKey(name)==true).ToDictionary(name=>name,name=>
        {string text=item.Logs[name]??"";return Redact(text.Length>2048?text[^2048..]:text,root);})
    };

    static object SafeResourceRow(JsonElement value)
    {
        var components=Get(value,"components");
        return new{observedUtc=At(value,"observedUtc")??At(value,"updatedUtc"),coverage=Allowed(Text(value,"coverage"),"MeasuredComponents","Limited","Unavailable"),
            processorCount=Math.Clamp(Number(value,"processorCount"),0,4096),memoryAvailable=Flag(value,"memoryAvailable"),
            physicalAvailable=OptionalLong(value,"physicalAvailable"),commitTotal=OptionalLong(value,"commitTotal"),commitLimit=OptionalLong(value,"commitLimit"),
            components=components.ValueKind!=JsonValueKind.Array?Array.Empty<object>():components.EnumerateArray().Take(16).Select(p=>(object)new
            {
                role=Allowed(Text(p,"role"),"GUI","Agent","Router","Bridge","Watcher","Component"),pid=Math.Max(0,Number(p,"pid")),startedUtc=At(p,"startedUtc"),observedUtc=At(p,"observedUtc"),
                privateBytes=OptionalLong(p,"privateBytes"),workingSetBytes=OptionalLong(p,"workingSetBytes"),cpuTotalMilliseconds=OptionalLong(p,"cpuTotalMilliseconds"),
                machineCpuPercent=Get(p,"machineCpuPercent").ValueKind==JsonValueKind.Number&&Get(p,"machineCpuPercent").TryGetDouble(out double cpu)&&double.IsFinite(cpu)&&cpu is >=0 and <=100?(double?)cpu:null,
                handles=Math.Max(0,Number(p,"handles")),udpPorts=Math.Clamp(Number(p,"udpPorts"),0,131072)
            }).ToArray()};
    }
    static List<JsonElement> ResourceRows(string path,DateTime now)
    {
        var rows=new List<JsonElement>();
        if(!Regular(path))return rows;
        try
        {
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            int count=(int)Math.Min(input.Length,8*1024*1024);long offset=input.Length-count;input.Position=offset;
            byte[] bytes=new byte[count];input.ReadExactly(bytes);
            foreach(string line in Encoding.UTF8.GetString(bytes).Split('\n').Skip(offset>0?1:0).TakeLast(1536))
            {
                if(line.Length==0||line.Length>8192)continue;
                try{using var doc=JsonDocument.Parse(line);if(At(doc.RootElement,"observedUtc") is {} at&&at>now.AddHours(-48)&&at<=now.AddSeconds(2))rows.Add(doc.RootElement.Clone());}catch(JsonException){}
            }
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
        return rows;
    }
    static readonly Dictionary<string,JsonElement> LastResource=new(StringComparer.OrdinalIgnoreCase);
    public static void ObserveResources(string directory,ResourceState state)
    {
        var sample=state.Sample;var now=sample.ObservedUtc;
        string path=Path.Combine(directory,"resource-history.jsonl");
        if(!Regular(directory)||!Regular(path))return;
        lock(LastResource)
        {
            if(!LastResource.TryGetValue(path,out var previous))previous=ResourceRows(path,now).LastOrDefault();
            if(At(previous,"observedUtc") is {} at&&now>=at&&now-at<TimeSpan.FromSeconds(60))return;
            var previousComponents=Get(previous,"components");
            var components=sample.Processes.Where(p=>p.CitaComponent).Take(16).Select(p=>
            {
                double? cpu=null;
                if(previousComponents.ValueKind==JsonValueKind.Array&&p.CpuTotalMilliseconds is {} total&&p.ObservedUtc!=default)
                {
                    var before=previousComponents.EnumerateArray().FirstOrDefault(b=>Number(b,"pid")==p.Pid&&At(b,"startedUtc")==p.StartedUtc);
                    if(OptionalLong(before,"cpuTotalMilliseconds") is {} old&&At(before,"observedUtc") is {} prior&&p.ObservedUtc>prior&&total>=old)
                        cpu=Math.Clamp(100d*(total-old)/(p.ObservedUtc-prior).TotalMilliseconds/Math.Max(1,Environment.ProcessorCount),0,100);
                }
                return new{role=ResourceRole(p.Name),pid=p.Pid,startedUtc=p.StartedUtc,observedUtc=p.ObservedUtc==default?(DateTime?)null:p.ObservedUtc,
                    privateBytes=p.PrivateBytes,workingSetBytes=p.WorkingSetBytes,cpuTotalMilliseconds=p.CpuTotalMilliseconds,machineCpuPercent=cpu,
                    handles=p.Handles,udpPorts=p.UdpPorts};
            }).ToArray();
            var row=Project(new{observedUtc=now,processorCount=Environment.ProcessorCount,
                coverage=sample.Unavailable.Any(x=>x is "CitaProcessCoverage" or "ProcessCoverageLimited" or "SamplerUnavailable")?"Limited":"MeasuredComponents",
                sample.MemoryAvailable,sample.PhysicalAvailable,sample.CommitTotal,sample.CommitLimit,components});
            Directory.CreateDirectory(directory);
            if(File.Exists(path)&&new FileInfo(path).Length>8*1024*1024)
            {
                string temp=Path.Combine(directory,"."+Guid.NewGuid().ToString("N")+".tmp");
                try{File.WriteAllLines(temp,ResourceRows(path,now).TakeLast(768).Select(r=>JsonSerializer.Serialize(SafeResourceRow(r))),new UTF8Encoding(false));File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}
            }
            string prefix="";
            if(File.Exists(path)){using var tail=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);if(tail.Length>0){tail.Position=tail.Length-1;if(tail.ReadByte()!=10)prefix="\n";}}
            File.AppendAllText(path,prefix+JsonSerializer.Serialize(SafeResourceRow(row))+"\n",new UTF8Encoding(false));
            if(LastResource.Count>=16)LastResource.Clear();LastResource[path]=row;
        }
    }
    static void ExportJournals(Action<string,string> entry,string root,string directory,DateTime now)
    {
        foreach(string source in new[]{"ordinary","startup"})
        {
            var state=ReadIncidents(Path.Combine(directory,"diagnostic-incidents-"+source+".json"),root);
            var items=state.Incidents.Where(i=>i.DetectedUtc>now.AddHours(-48)&&i.DetectedUtc<=now.AddSeconds(2)).TakeLast(8).Select(i=>SafeIncident(i,root)).ToArray();
            if(items.Length>0)entry("history/incidents-"+source+".json",JsonSerializer.Serialize(items));
        }
        var rows=ResourceRows(Path.Combine(directory,"resource-history.jsonl"),now);
        if(rows.Count>0)entry("history/resources.json",JsonSerializer.Serialize(rows.Select(SafeResourceRow)));
    }
}

internal static class EvidenceJsonNumbers
{
    internal static long? TryGetNonnegativeLong(this JsonElement value)=>value.ValueKind==JsonValueKind.Number&&value.TryGetInt64(out long n)&&n>=0?n:null;
}
