using System.Text.Json;

namespace RoutePilot.Agent;

// Shared by the protected service and isolated acceptance runner. Destruction
// retains the persistent blocks. Only explicit Disconnect removes protection.
internal sealed class NativeGuardedSession:IAsyncDisposable
{
    readonly NativeProcessJob job=new();
    readonly NativeWfpGuard guard;
    readonly NativeCoreRecovery core;
    readonly NativeAwgProfiles profiles;
    bool released,retired;
    readonly string configPath;
    readonly NativeEndpointPermission? endpoint;
    internal string Phase{get;private set;}="Created";
    internal NativeGuardedSession(string nativeImage,string coreImage,string guardImage,string config,string owner,string run,JsonElement policy,string expectedExit,Func<CancellationToken,Task>? beforeRepair=null,Func<NativeCoreBinding,CancellationToken,Task>? afterStart=null,NativeEndpointPermission? endpoint=null)
    {
        configPath=Path.GetFullPath(config);
        this.endpoint=endpoint;guard=new NativeWfpGuard(guardImage,run,policy,job.Attach,endpoint);
        core=new NativeCoreRecovery(()=>new NativeCoreBinding(coreImage,config,nativeImage,owner,expectedExit,persistentControl:true,started:job.Attach),guard.VerifyAsync,afterStart:async(binding,stop)=>{await guard.BindAsync(stop);if(afterStart!=null)await afterStart(binding,stop);},beforeRepair:beforeRepair,persistentControl:true);
        profiles=new NativeAwgProfiles(nativeImage,binding:core,persistentControl:true,started:job.Attach);
    }
    internal NativeWfpGuard Guard=>guard;
    internal NativeCoreRecovery Core=>core;
    internal object Snapshot=>new{phase=Phase,coreState=core.State,corePid=core.ProcessId,repairs=core.Repairs,bindings=guard.Bindings,policyDigest=guard.Digest,guardFailure=guard.LastFailureStage,ipv6Mode="BlockProtected",profile=profiles.Snapshot()};
    internal async Task InitializeAsync(CancellationToken stop)
    {
        Phase="CompilingPolicy";await guard.PlanAsync(stop);
        // The generated core captures exactly the compiled IPv4 policy. IPv6
        // VPN prefixes remain blocked until IPv6 carriage has its acceptance.
        byte[] bytes=await File.ReadAllBytesAsync(configPath,stop);if(bytes.Length>65536||bytes.AsSpan().StartsWith(new byte[]{0xef,0xbb,0xbf}))throw new InvalidDataException("Native core JSON must be bounded UTF-8 without BOM.");
        using var options=JsonDocument.Parse(bytes);
        var inbounds=options.RootElement.GetProperty("inbounds").EnumerateArray().Where(x=>x.GetProperty("type").GetString()=="tun").ToArray();
        if(inbounds.Length!=1||!new HashSet<string>(inbounds[0].GetProperty("route_address").EnumerateArray().Select(x=>x.GetString()!)).SetEquals(guard.Prefixes.Where(x=>!x.Contains(':'))))throw new InvalidDataException("TUN and guard policy differ.");
        var exclusions=inbounds[0].TryGetProperty("route_exclude_address",out var excludes)?excludes.EnumerateArray().Select(x=>x.GetString()!).ToArray():[];
        if(!new HashSet<string>(exclusions).SetEquals(endpoint==null?[]:new[]{endpoint.Server+"/32"}))throw new InvalidDataException("TUN endpoint bypass differs from the protected profile.");
        Phase="ArmingGuard";await guard.ArmAsync(stop);await guard.CloseAsync(stop);
        if(!await guard.VerifyAsync(stop))throw new IOException("Native guard was not confirmed.");
        Phase="StartingCore";await core.StartControllerAsync(stop);Phase="Ready";
    }
    internal async Task<JsonElement> ConnectAsync(JsonElement spec,CancellationToken stop)
    {if(released)throw new InvalidOperationException("Native session was retired.");Phase="Connecting";if(guard.Luid==0)await guard.BindAsync(stop);var result=await profiles.SwitchAsync(spec,stop);Phase="Connected";return result;}
    internal async Task PauseAsync(string session,ulong generation,CancellationToken stop)
    {await profiles.StopAsync(session,generation);await guard.CloseAsync(stop);}
    internal async Task DisconnectAsync(CancellationToken stop)
    {await RetireAsync(stop);await guard.RemoveAsync(stop);}
    async Task RetireAsync(CancellationToken stop)
    {
        if(retired)return;released=true;
        try{await guard.CloseAsync(stop);}catch{}
        try{await profiles.DisposeAsync();}catch{}
        try{await core.DisposeAsync();}catch{}
        await job.TerminateAsync(stop);job.Dispose();retired=true;
    }
    public async ValueTask DisposeAsync()
    {using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await RetireAsync(deadline.Token);}
}
