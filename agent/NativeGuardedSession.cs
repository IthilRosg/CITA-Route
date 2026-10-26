using System.Text.Json;
using System.Net.NetworkInformation;

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
    readonly string coreImage;
    readonly SemaphoreSlim policyGate=new(1);
    readonly CancellationTokenSource policyStop=new();
    readonly CancellationTokenSource automaticStop=new();
    readonly bool adaptive;
    Task? automaticTask;
    Task? scopeMaintenance;
    string automaticState="Disabled";
    int calibrated;
    NativeRoutePolicyPublisher? publisher;
    readonly NativeEndpointPermission? endpoint;
    internal string Phase{get;private set;}="Created";
    internal NativeGuardedSession(string nativeImage,string coreImage,string guardImage,string config,string owner,string run,JsonElement policy,string expectedExit,Func<CancellationToken,Task>? beforeRepair=null,Func<NativeCoreBinding,CancellationToken,Task>? afterStart=null,NativeEndpointPermission? endpoint=null,bool adaptive=false)
    {
        configPath=Path.GetFullPath(config);this.coreImage=Path.GetFullPath(coreImage);this.adaptive=adaptive;
        this.endpoint=endpoint;guard=new NativeWfpGuard(guardImage,run,policy,job.Attach,endpoint);
        core=new NativeCoreRecovery(()=>new NativeCoreBinding(coreImage,config,nativeImage,owner,expectedExit,persistentControl:true,started:job.Attach),guard.VerifyAsync,afterStart:async(binding,stop)=>{await guard.BindAsync(stop);if(afterStart!=null)await afterStart(binding,stop);},beforeRepair:beforeRepair,persistentControl:true);
        profiles=new NativeAwgProfiles(nativeImage,binding:core,persistentControl:true,started:job.Attach);
    }
    internal NativeWfpGuard Guard=>guard;
    internal NativeCoreRecovery Core=>core;
    internal object Snapshot=>new{phase=Phase,coreState=core.State,coreStartupIssue=core.StartupIssue,corePid=core.ProcessId,repairs=core.Repairs,bindings=guard.Bindings,policyDigest=guard.Digest,guardFailure=guard.LastFailureStage,ipv6Mode="BlockProtected",profile=profiles.Snapshot(),domainPolicyRevision=publisher?.Revision??0,domainPolicyPhase=publisher?.Phase??"Unavailable",policyDirectDials=core.PolicyDirectDials,policyBlockedDials=core.PolicyBlockedDials,retiredDirectScopes=publisher?.RetiredScopes??0,automaticRouting=automaticState,calibratedDomains=publisher?.AutomaticRuleCount??0,calibrationChecks=calibrated};
    internal async Task PublishPolicyAsync(NativePolicyRequest request,CancellationToken stop)
    {
        automaticStop.Cancel();if(automaticTask!=null)await automaticTask.WaitAsync(stop);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(stop,policyStop.Token);stop=linked.Token;
        await policyGate.WaitAsync(stop);
        try
        {
            if(released||retired||!core.SupportsPolicy)throw new InvalidOperationException("Native policy controller unavailable.");
            using var document=JsonDocument.Parse(await File.ReadAllTextAsync(configPath,stop));
            var routes=request.Rules.Where(r=>r.Route is "direct" or "dpi").Select(r=>r.Route).Distinct().ToArray();
            string? physical=null;
            foreach(string route in routes){var outbound=document.RootElement.GetProperty("outbounds").EnumerateArray().Single(x=>x.GetProperty("tag").GetString()=="native-"+route);string? name=outbound.GetProperty("bind_interface").GetString();if(outbound.GetProperty("type").GetString()!="direct"||string.IsNullOrEmpty(name)||physical!=null&&physical!=name)throw new InvalidDataException("Native Direct outbound is not pinned.");physical=name;}
            var nic=physical!=null?NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.Name==physical&&n.OperationalStatus==OperationalStatus.Up):NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.OperationalStatus==OperationalStatus.Up&&n.GetIPProperties().GatewayAddresses.Any(g=>g.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork));
            publisher??=new NativeRoutePolicyPublisher(guard,core,coreImage,checked((uint)nic.GetIPProperties().GetIPv4Properties().Index));
            await publisher.ApplyAsync(request.Epoch,request.Rules,stop);
        }
        finally{policyGate.Release();}
    }
    internal async Task InitializeAsync(CancellationToken stop)
    {
        Phase="CompilingPolicy";await guard.PlanAsync(stop);
        // The generated core captures exactly the compiled IPv4 policy. IPv6
        // VPN prefixes remain blocked until IPv6 carriage has its acceptance.
        byte[] bytes=await File.ReadAllBytesAsync(configPath,stop);if(bytes.Length>65536||bytes.AsSpan().StartsWith(new byte[]{0xef,0xbb,0xbf}))throw new InvalidDataException("Native core JSON must be bounded UTF-8 without BOM.");
        using var options=JsonDocument.Parse(bytes);
        var inbounds=options.RootElement.GetProperty("inbounds").EnumerateArray().Where(x=>x.GetProperty("type").GetString()=="tun").ToArray();
        if(inbounds.Length!=1||!new HashSet<string>(inbounds[0].GetProperty("route_address").EnumerateArray().Select(x=>x.GetString()!)).SetEquals(guard.Prefixes.Where(x=>!x.Contains(':'))))throw new InvalidDataException("TUN and guard policy differ.");
        string? tunnel=inbounds[0].GetProperty("interface_name").GetString();if(tunnel is not ("Cita-NativeLab" or "Cita-Route" or "Cita-Native"))throw new InvalidDataException("Native tunnel name refused.");guard.ExpectedTunnelName=tunnel;
        var exclusions=inbounds[0].TryGetProperty("route_exclude_address",out var excludes)?excludes.EnumerateArray().Select(x=>x.GetString()!).ToArray():[];
        if(!new HashSet<string>(exclusions).SetEquals(endpoint==null?[]:new[]{endpoint.Server+"/32"}))throw new InvalidDataException("TUN endpoint bypass differs from the protected profile.");
        Phase="ArmingGuard";await guard.ArmAsync(stop);await guard.CloseAsync(stop);
        if(!await guard.VerifyAsync(stop))throw new IOException("Native guard was not confirmed.");
        Phase="StartingCore";await core.StartControllerAsync(stop);Phase="Ready";
    }
    internal async Task<JsonElement> ConnectAsync(JsonElement spec,CancellationToken stop)
    {if(released)throw new InvalidOperationException("Native session was retired.");Phase="Connecting";if(guard.Luid==0)await guard.BindAsync(stop);var result=await profiles.SwitchAsync(spec,stop);Phase="Connected";scopeMaintenance??=MaintainScopes();if(adaptive&&automaticTask==null)automaticTask=Calibrate();return result;}
    internal async Task SweepScopesAsync(CancellationToken stop){if(publisher!=null)await publisher.MaintainAsync(stop);}
    async Task MaintainScopes()
    {
        try{while(!policyStop.IsCancellationRequested){await Task.Delay(5000,policyStop.Token);if(core.State is not ("Active" or "TransportDegraded"))continue;try{await SweepScopesAsync(policyStop.Token);}catch(OperationCanceledException)when(policyStop.IsCancellationRequested){throw;}catch{/* Retry without revoking unconfirmed permissions. */}}}
        catch(OperationCanceledException)when(policyStop.IsCancellationRequested){}
    }
    async Task Calibrate()
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(policyStop.Token,automaticStop.Token);var stop=linked.Token;
        try
        {
            using var document=JsonDocument.Parse(await File.ReadAllTextAsync(configPath,stop));
            var outbound=document.RootElement.GetProperty("outbounds").EnumerateArray().SingleOrDefault(x=>x.GetProperty("tag").GetString()=="native-direct");
            if(outbound.ValueKind==JsonValueKind.Undefined){automaticState="NotConfigured";return;}
            if(outbound.GetProperty("type").GetString()!="direct")throw new InvalidDataException("Adaptive Direct outbound refused.");
            string name=outbound.GetProperty("bind_interface").GetString()!;var nic=NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.Name==name&&n.OperationalStatus==OperationalStatus.Up);
            string NetworkStamp()=>string.Join(";",NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.Id==nic.Id).SelectMany(n=>n.GetIPProperties().UnicastAddresses.Select(a=>a.Address.ToString()).Append(n.OperationalStatus.ToString())).OrderBy(x=>x,StringComparer.Ordinal));
            string networkStamp=NetworkStamp();
            publisher??=new NativeRoutePolicyPublisher(guard,core,coreImage,checked((uint)nic.GetIPProperties().GetIPv4Properties().Index));
            var attempted=new NativeCalibrationSchedule();var budget=new NativeCalibrationBudget();int pid=core.ProcessId;automaticState="Observing";
            while(!stop.IsCancellationRequested)
            {
                await Task.Delay(1000,stop);if(core.State!="Active")continue;
                if(core.ProcessId!=pid){int nextPid=core.ProcessId;await publisher.ResetForCoreAsync(nextPid,stop);pid=nextPid;attempted.Clear();automaticState="Observing";continue;}
                string currentStamp=NetworkStamp();if(currentStamp!=networkStamp){await publisher.ResetForNetworkAsync(stop);networkStamp=currentStamp;attempted.Clear();automaticState="Observing";}
                long now=Environment.TickCount64;var selected=attempted.Select(core.Observations,now);if(selected==null)continue;
                if(!budget.TryAcquire(now)){automaticState="Throttled";continue;}
                attempted.Mark(selected,now);automaticState="CheckingDirect";
                try{using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(10));if(await publisher.CalibrateAsync(selected,deadline.Token))Interlocked.Increment(ref calibrated);}
                catch(OperationCanceledException)when(stop.IsCancellationRequested){throw;}
                catch{automaticState="VpnFallback";}
                automaticState="Observing";
            }
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){automaticState="Stopped";}
        catch{automaticState="Unavailable";}
    }
    internal async Task PauseAsync(string session,ulong generation,CancellationToken stop)
    {await profiles.StopAsync(session,generation);await guard.CloseAsync(stop);}
    internal async Task DisconnectAsync(CancellationToken stop)
    {await RetireAsync(stop);await guard.RemoveAsync(stop);}
    async Task RetireAsync(CancellationToken stop)
    {
        if(retired)return;released=true;policyStop.Cancel();if(automaticTask!=null)await automaticTask.WaitAsync(stop);if(scopeMaintenance!=null)await scopeMaintenance.WaitAsync(stop);await policyGate.WaitAsync(stop);policyGate.Release();
        try{await guard.CloseAsync(stop);}catch{}
        try{await profiles.DisposeAsync();}catch{}
        try{await core.DisposeAsync();}catch{}
        await job.TerminateAsync(stop);job.Dispose();retired=true;
    }
    public async ValueTask DisposeAsync()
    {using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await RetireAsync(deadline.Token);}
}
