using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoutePilot.Agent;

public static class Storage
{
    public static readonly JsonSerializerOptions Json = new(){PropertyNameCaseInsensitive=true,WriteIndented=true,Converters={new JsonStringEnumConverter()}};
    public static readonly JsonSerializerOptions Wire = new(Json){WriteIndented=false};
    public static T Read<T>(string path)
    {using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);return JsonSerializer.Deserialize<T>(stream,Json)??throw new InvalidDataException("Empty state file.");}
    public static void Write<T>(string path,T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {byte[] bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,Json));stream.Write(bytes);stream.Flush(true);}
            if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}

public enum RunPhase { Idle, Armed, Testing, Restoring, Restored, RecoveryFailed }
public enum TestKind { DryRun, VpnusRestart, WireSockProxy, SystemRouting }
public sealed record ProcessIdentity(int Pid,DateTime StartedUtc,string Path)
{
    public static ProcessIdentity From(Process process) => new(process.Id,process.StartTime.ToUniversalTime(),process.MainModule?.FileName??throw new InvalidDataException("Missing process image."));
    public bool Matches()
    {
        try
        {
            using var process=Process.GetProcessById(Pid);
            return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime()-StartedUtc).TotalMilliseconds)<100 &&
                string.Equals(process.MainModule?.FileName,Path,StringComparison.OrdinalIgnoreCase);
        }
        catch(Exception ex)when(ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception){return false;}
    }
}
public sealed class RunState
{
    public string Id{get;set;}="";
    public long Generation{get;set;}
    public RunPhase Phase{get;set;}
    public TestKind Kind{get;set;}
    public DateTime StartedUtc{get;set;}
    public DateTime WorkDeadlineUtc{get;set;}
    public DateTime ReturnDeadlineUtc{get;set;}
    public int UserSessionId{get;set;}
    public bool VpnusTouched{get;set;}
    public bool VpnusInitiallyUp{get;set;}
    public bool SystemIsolated{get;set;}
    public string NetworkSignature{get;set;}="";
    public string LastMessage{get;set;}="";
    public List<ProcessIdentity> Processes{get;set;}=new();
    public List<ProbeResult> Results{get;set;}=new();
    public int PhysicalInterfaceIndex{get;set;}
    public OwnedRoute? Route{get;set;}
    public bool Active=>Phase is RunPhase.Armed or RunPhase.Testing or RunPhase.Restoring;
}
public sealed record ProbeResult(string Stage,bool Success,string Detail,long ElapsedMs=0,bool Informational=false);
public sealed class AgentConfig
{
    public string UserSid{get;set;}="";
    public string UserProfile{get;set;}="";
    public string DataDirectory{get;set;}="";
    public string AppRoot{get;set;}="";
    public string WireSockSourceProfile{get;set;}="";
    public string WireSockCliPath{get;set;}="";
    public string SourceEndpoint{get;set;}="";
    public string ExpectedEgressIp{get;set;}="";
    public string EndpointIp{get;set;}="";
    public int EndpointPort{get;set;}
}
public sealed record OwnedRoute(string Prefix,int InterfaceIndex,string Gateway,int Metric);
public sealed record AgentRequest(string Command,string? RunId=null);
public sealed record AgentReply(bool Success,string Message,RunState? State=null);

public sealed class RunStore
{
    readonly string _path;
    public RunState State{get;private set;}
    public RunStore(string directory)
    {
        _path=Path.Combine(directory,"run.json");
        State=File.Exists(_path)?Storage.Read<RunState>(_path):new();
    }
    public void Begin(TestKind kind,int sessionId,int workSeconds=90,int restoreSeconds=60)
    {
        if(State.Active || State.Phase==RunPhase.RecoveryFailed)throw new InvalidOperationException("A previous run must be recovered first.");
        if(workSeconds is <1 or >120 || restoreSeconds is <5 or >90)throw new ArgumentOutOfRangeException(nameof(workSeconds));
        var now=DateTime.UtcNow;
        State=new(){Id=Guid.NewGuid().ToString("N"),Generation=State.Generation+1,Phase=RunPhase.Armed,Kind=kind,
            UserSessionId=sessionId,StartedUtc=now,WorkDeadlineUtc=now.AddSeconds(workSeconds),ReturnDeadlineUtc=now.AddSeconds(workSeconds+restoreSeconds)};
        Save();
    }
    public bool Transition(string id,RunPhase phase,string message)
    {
        if(State.Id!=id)return false;
        bool allowed=(State.Phase,phase) switch
        {
            (RunPhase.Armed,RunPhase.Testing)=>true,
            (RunPhase.Armed or RunPhase.Testing or RunPhase.RecoveryFailed,RunPhase.Restoring)=>true,
            (RunPhase.Restoring,RunPhase.Restored or RunPhase.RecoveryFailed)=>true,
            _=>false
        };
        if(!allowed)return false;
        State.Phase=phase;State.LastMessage=message;Save();return true;
    }
    public void Save()=>Storage.Write(_path,State);
}
