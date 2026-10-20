using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RoutePilot.Agent;

internal sealed class PolicyPulse
{
    public int Schema{get;set;}=1;
    public string InstanceId{get;set;}="";
    public string SessionId{get;set;}="";
    public int Pid{get;set;}
    public DateTime StartedUtc{get;set;}
    public string Phase{get;set;}="";
    public DateTime ProgressUtc{get;set;}
    public DateTime DeadlineUtc{get;set;}
    public long Sequence{get;set;}
}

internal sealed class PolicyProgress
{
    readonly string _root,_session,_instance=Guid.NewGuid().ToString("N");
    readonly ProcessIdentity _identity;
    readonly Stopwatch _since=Stopwatch.StartNew();
    long _sequence;
    public PolicyProgress(string root,string session){_root=root;_session=session;using var self=Process.GetCurrentProcess();_identity=ProcessIdentity.From(self);}
    // Only real worker checkpoints advance progress. There is no timer that
    // can report a blocked worker as healthy.
    public void Mark(string phase,bool force=false)
    {
        if(!force&&_since.ElapsedMilliseconds<2000)return;
        var current=ComponentLiveness.Read<PolicySession>(Path.Combine(_root,"runtime","session.json"));
        if(current.Id!=_session)return;
        var now=DateTime.UtcNow;
        Storage.Write(Path.Combine(_root,"runtime","policy-heartbeat.json"),new PolicyPulse{
            InstanceId=_instance,SessionId=_session,Pid=_identity.Pid,StartedUtc=_identity.StartedUtc,
            Phase=phase,ProgressUtc=now,DeadlineUtc=now.AddSeconds(phase is "Starting" or "Publishing"?45:phase=="Running"?15:10),Sequence=++_sequence});
        _since.Restart();
    }
}

internal sealed class PolicyOwner
{
    public int Pid{get;set;}
    public DateTime StartedUtc{get;set;}
    public string Path{get;set;}="";
    public string SessionId{get;set;}="";
}
internal sealed class OrdinarySession
{
    public string Id{get;set;}="";
    public bool Running{get;set;}
    public int BridgePid{get;set;}
    public int RouterPid{get;set;}
    public DateTime RouterStartedUtc{get;set;}
    public DateTime BridgeStartedUtc{get;set;}
    public int BridgeGeneration{get;set;}
    public bool BridgeReady{get;set;}
    public string VpnHoldReason{get;set;}="";
}
internal sealed class BridgeSafety
{
    public int Schema{get;set;}=1;
    public string SessionId{get;set;}="";
    public int BridgeGeneration{get;set;}
    public bool Hold{get;set;}
    public string Code{get;set;}="";
    public DateTime UpdatedUtc{get;set;}
}
internal sealed record ComponentAction(string Code,bool Stopped=false,bool HoldPersisted=true);

internal static class ComponentLiveness
{
    internal static T Read<T>(string path,int maximum=16384)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(stream.Length>maximum)throw new InvalidDataException("Component state exceeds limit.");
        return System.Text.Json.JsonSerializer.Deserialize<T>(stream,Storage.Json)??throw new InvalidDataException("Missing component state.");
    }
    internal static string PulseState(PolicyPulse? pulse,PolicyOwner owner,string session,DateTime now)
    {
        if(pulse==null)return now-owner.StartedUtc<=TimeSpan.FromSeconds(15)&&owner.StartedUtc<=now.AddSeconds(2)?"Starting":"Stale";
        if(pulse.Schema!=1||!Guid.TryParseExact(pulse.InstanceId,"N",out _)||pulse.Sequence<1||pulse.SessionId!=session||pulse.Pid!=owner.Pid||
            Math.Abs((pulse.StartedUtc-owner.StartedUtc).TotalMilliseconds)>=100||pulse.ProgressUtc<owner.StartedUtc.AddSeconds(-2)||pulse.ProgressUtc>now.AddSeconds(2))return "Unknown";
        int budget=pulse.Phase is "Starting" or "Publishing"?45:pulse.Phase=="Running"?15:pulse.Phase is "Stopped" or "Error" or "NetworkChanged"?10:0;
        if(budget==0||Math.Abs((pulse.DeadlineUtc-pulse.ProgressUtc).TotalSeconds-budget)>0.1)return "Unknown";
        return now<=pulse.DeadlineUtc?pulse.Phase is "Starting" or "Publishing"?"Starting":"Healthy":"Stale";
    }
    static OrdinarySession? Current(string root,string id,int? generation=null)
    {
        try
        {
            if(File.Exists(Path.Combine(root,"runtime","stop.request")))return null;
            var value=Read<OrdinarySession>(Path.Combine(root,"runtime","session.json"));
            return value.Running&&value.Id==id&&(!generation.HasValue||value.BridgeGeneration==generation)?value:null;
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException){return null;}
    }
    internal static ComponentAction StopStalePolicy(string root,string id,string image,DateTime? testClock=null)
    {
        if(Current(root,id)==null)return new("StaleSession");
        var owner=Read<PolicyOwner>(Path.Combine(root,"runtime","policy-process.json"));
        if(owner.SessionId!=id||!Path.GetFullPath(owner.Path).Equals(Path.GetFullPath(image),StringComparison.OrdinalIgnoreCase))return new("IdentityUnavailable");
        PolicyPulse? Pulse(){try{return Read<PolicyPulse>(Path.Combine(root,"runtime","policy-heartbeat.json"));}catch(FileNotFoundException){return null;}}
        if(PulseState(Pulse(),owner,id,testClock??DateTime.UtcNow)!="Stale")return new("ProgressCurrent");
        using var process=CheckedProcess.Open(new(owner.Pid,owner.StartedUtc,owner.Path));
        if(process==null)return new("IdentityUnavailable");
        if(Current(root,id)==null||PulseState(Pulse(),owner,id,testClock??DateTime.UtcNow)!="Stale")return new("ProgressCurrent");
        var fresh=Read<PolicyOwner>(Path.Combine(root,"runtime","policy-process.json"));
        if(fresh.Pid!=owner.Pid||fresh.StartedUtc!=owner.StartedUtc||fresh.SessionId!=id||fresh.Path!=owner.Path)return new("StaleSession");
        return process.Stop()?new("PolicyStopped",true):new("StopUnconfirmed");
    }
    internal static async Task<ComponentAction> PauseBridge(string root,string id,int generation,string image,
        Func<Task<Telemetry.WireSockSample>> status)
    {
        var session=Current(root,id,generation);if(session==null)return new("StaleSession");
        bool hold=!string.IsNullOrEmpty(session.VpnHoldReason);string holdCode=hold&&session.VpnHoldReason!="EgressMismatch"?"SafetyStateUnavailable":"EgressMismatch";
        try
        {
            var safety=Read<BridgeSafety>(Path.Combine(root,"runtime","bridge-safety.json"));
            if(safety.Schema!=1||string.IsNullOrEmpty(safety.SessionId)||!safety.Hold){hold=true;holdCode="SafetyStateUnavailable";}
            else if(safety.SessionId==id){hold=true;holdCode=safety.Code=="EgressMismatch"?"EgressMismatch":"SafetyStateUnavailable";}
        }catch(FileNotFoundException){}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException){hold=true;holdCode="SafetyStateUnavailable";}
        try
        {
            var health=Read<BridgeHealth>(Path.Combine(root,"runtime","session-health.json"));
            if(health.SessionId==id&&health.BridgeGeneration==generation&&!health.Healthy&&health.Code=="EgressMismatch"&&
                health.UpdatedUtc<=DateTime.UtcNow.AddSeconds(2)&&health.UpdatedUtc>=DateTime.UtcNow.AddSeconds(-90)){hold=true;holdCode="EgressMismatch";}
        }catch(Exception ex)when(ex is IOException or System.Text.Json.JsonException){}
        var state=await status();
        if(!hold&&state.Profile.Equals("SmartRouting-AWG2",StringComparison.OrdinalIgnoreCase)&&state.ConnectionState==3&&state.Code is "NativeStatistics" or "NoStatistics")return new("VpnConfirmed");
        if(Current(root,id,generation)==null)return new("StaleSession");
        bool persisted=true;
        if(hold)try{Storage.Write(Path.Combine(root,"runtime","bridge-safety.json"),new BridgeSafety{SessionId=id,BridgeGeneration=generation,Hold=true,Code=holdCode,UpdatedUtc=DateTime.UtcNow});}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){persisted=false;}
        using var process=CheckedProcess.Open(new(session.BridgePid,session.BridgeStartedUtc,image));
        if(process==null)return new("IdentityUnavailable",HoldPersisted:persisted);
        var fresh=Current(root,id,generation);
        if(fresh==null||fresh.BridgePid!=session.BridgePid||fresh.BridgeStartedUtc!=session.BridgeStartedUtc)return new("StaleSession");
        // A separate per-session hold is safe to publish without overwriting
        // session.json owned by Start/Stop. A late record cannot hold a new ID.
        return process.Stop()?new("BridgePaused",true,persisted):new("StopUnconfirmed",HoldPersisted:persisted);
    }
    internal static AgentConfig Configured(string root)
    {
        var config=Read<AgentConfig>(Path.Combine(AppContext.BaseDirectory,"agent-config.json"));
        if(Path.GetFullPath(root).TrimEnd('\\').Equals(Cita.Startup.StartupPaths.Client,StringComparison.OrdinalIgnoreCase)&&BootRouting.OwnsSnapshot(config.AppRoot))config.AppRoot=Cita.Startup.StartupPaths.Client;
        if(!Path.GetFullPath(config.AppRoot).TrimEnd('\\').Equals(Path.GetFullPath(root).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("This client does not own the protected installation.");
        string run=Path.Combine(config.DataDirectory,"run.json");
        if(File.Exists(run))
        {
            var state=Read<RunState>(run,131072);
            if(state.Active||state.Phase==RunPhase.RecoveryFailed)throw new InvalidOperationException("Independent diagnostics own the installation.");
        }
        return config;
    }
    sealed class BridgeHealth
    {
        public string SessionId{get;set;}="";public int BridgeGeneration{get;set;}public bool Healthy{get;set;}
        public string Code{get;set;}="";public DateTime UpdatedUtc{get;set;}
    }
}

// Identity checks and termination use the SAME kernel handle. A recycled PID
// cannot turn a validated helper into another process between check and stop.
internal sealed class CheckedProcess : IDisposable
{
    readonly SafeProcessHandle _handle;
    CheckedProcess(SafeProcessHandle handle)=>_handle=handle;
    [DllImport("kernel32.dll",SetLastError=true)]static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetProcessTimes(SafeProcessHandle process,out long creation,out long exit,out long kernel,out long user);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryFullProcessImageName(SafeProcessHandle process,uint flags,StringBuilder name,ref int size);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateProcess(SafeProcessHandle process,uint code);
    [DllImport("kernel32.dll")]static extern uint WaitForSingleObject(SafeProcessHandle handle,uint milliseconds);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool OpenProcessToken(SafeProcessHandle process,uint access,out SafeAccessTokenHandle token);
    internal static CheckedProcess? Open(ProcessIdentity identity)
    {
        if(identity.Pid<=0||identity.Pid==Environment.ProcessId||identity.StartedUtc==default)return null;
        var handle=OpenProcess(0x1000|0x100000|1,false,identity.Pid);
        try
        {
            if(handle.IsInvalid||WaitForSingleObject(handle,0)!=258||!GetProcessTimes(handle,out long created,out _,out _,out _)||
                DateTime.FromFileTimeUtc(created)!=identity.StartedUtc.ToUniversalTime())return null;
            var name=new StringBuilder(32768);int length=name.Capacity;
            if(!QueryFullProcessImageName(handle,0,name,ref length)||!name.ToString().Equals(identity.Path,StringComparison.OrdinalIgnoreCase))return null;
            if(!OpenProcessToken(handle,8,out var token))return null;
            using(token)using(var owner=new WindowsIdentity(token.DangerousGetHandle()))using(var caller=WindowsIdentity.GetCurrent())
                if(owner.User!=caller.User)return null;
            var result=new CheckedProcess(handle);handle=null!;return result;
        }
        finally{handle?.Dispose();}
    }
    internal bool Stop()=>WaitForSingleObject(_handle,0)==0||(TerminateProcess(_handle,71)&&WaitForSingleObject(_handle,1000)==0);
    public void Dispose()=>_handle.Dispose();
}
