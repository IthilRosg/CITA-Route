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
    string automaticStage="NotStarted",automaticFailureStage="",automaticFailureType="";
    int calibrationChecks,calibrationPassed,calibrationFailed,calibrationErrors;
    NativeRoutePolicyPublisher? publisher;
    readonly NativeZapretPolicyGate zapretPolicyGate=new();
    readonly NativeZapretSession? zapret;
    readonly NativeDecisionTrace decisionTrace=new();
    readonly NativeNetworkTelemetry networkTelemetry=new();
    readonly NativeEndpointPermission? endpoint;
    readonly NativeForcedIntentStore? forcedStore;
    internal string Phase{get;private set;}="Created";
    internal NativeGuardedSession(string nativeImage,string coreImage,string guardImage,string config,string owner,string run,JsonElement policy,string expectedExit,Func<CancellationToken,Task>? beforeRepair=null,Func<NativeCoreBinding,CancellationToken,Task>? afterStart=null,NativeEndpointPermission? endpoint=null,bool adaptive=false,NativeZapretBundle? zapretBundle=null,NativeForcedIntentStore? forcedStore=null)
    {
        configPath=Path.GetFullPath(config);this.coreImage=Path.GetFullPath(coreImage);this.adaptive=adaptive;this.forcedStore=forcedStore;
        this.endpoint=endpoint;guard=new NativeWfpGuard(guardImage,run,policy,job.Attach,endpoint);
        core=new NativeCoreRecovery(()=>new NativeCoreBinding(coreImage,config,nativeImage,owner,expectedExit,persistentControl:true,launchOwned:job.StartWithPipes),guard.VerifyAsync,afterStart:async(binding,stop)=>{zapretPolicyGate.Invalidate();zapret?.Invalidate();await guard.BindAsync(stop);if(afterStart!=null)await afterStart(binding,stop);},beforeRepair:beforeRepair,persistentControl:true);
        profiles=new NativeAwgProfiles(nativeImage,binding:core,persistentControl:true,started:job.Attach);
        core.CalibrationObserver=(eventName,observed,_,_)=>decisionTrace.Record(eventName,observed);
        if(zapretBundle!=null)zapret=new(zapretBundle,guard,core,zapretPolicyGate,configPath,endpoint,()=>!released&&!retired,policyStop.Token);
    }
    internal NativeWfpGuard Guard=>guard;
    internal NativeCoreRecovery Core=>core;
    internal bool PolicyRecoveryUnconfirmed=>publisher?.Phase=="RecoveryUnconfirmed";
#if CITA_DPI_LAB
    internal static bool EligibleDpiFailure(NativePathProbeResult? result)=>result is {Reachable:false,Reason:"dial-failed" or "dial-timeout" or "tls-failed" or "tls-timeout" or "http-write-failed" or "http-write-timeout" or "http-header-failed" or "http-header-timeout" or "http-body-failed" or "http-body-timeout"};
    // Set only by the isolated VM test controller; never compiled into the service.
    internal bool LabSimulateDirectTimeout{get;set;}
    internal async Task<bool> CalibrateDirectForLabAsync(NativeObservedDestination observed,CancellationToken stop,Func<bool>? evidenceCurrent=null)
    {
        if(Environment.GetEnvironmentVariable("CITA_NATIVE_DPI_LAB")!="1")throw new InvalidOperationException("DPI laboratory not enabled.");
        using var document=JsonDocument.Parse(await File.ReadAllTextAsync(configPath,stop));
        string name=document.RootElement.GetProperty("outbounds").EnumerateArray().Single(x=>x.GetProperty("tag").GetString()=="native-direct").GetProperty("bind_interface").GetString()!;
        var nic=NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.Name==name&&n.OperationalStatus==OperationalStatus.Up);
        publisher??=new NativeRoutePolicyPublisher(guard,core,coreImage,checked((uint)nic.GetIPProperties().GetIPv4Properties().Index),zapretPolicyGate,forcedStore);
        return await publisher.CalibrateAsync(observed,stop,evidenceCurrent);
    }
    internal async Task<bool> CalibrateDpiForLabAsync(NativeObservedDestination observed,CancellationToken stop,Func<bool>? evidenceCurrent=null)
    {
        if(Environment.GetEnvironmentVariable("CITA_NATIVE_DPI_LAB")!="1")throw new InvalidOperationException("DPI laboratory not enabled.");
        using var document=JsonDocument.Parse(await File.ReadAllTextAsync(configPath,stop));
        string name=document.RootElement.GetProperty("outbounds").EnumerateArray().Single(x=>x.GetProperty("tag").GetString()=="native-direct").GetProperty("bind_interface").GetString()!;
        var nic=NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.Name==name&&n.OperationalStatus==OperationalStatus.Up);
        publisher??=new NativeRoutePolicyPublisher(guard,core,coreImage,checked((uint)nic.GetIPProperties().GetIPv4Properties().Index),zapretPolicyGate,forcedStore);
        return await publisher.CalibrateDpiForLabAsync(observed,stop,evidenceCurrent);
    }
#endif
    internal void EnableDecisionTrace(bool value)=>decisionTrace.Enable(value);
    internal (bool Enabled,NativeTraceEntry[] Entries) DecisionTrace=>decisionTrace.Snapshot();
    internal NativeJobUsage ResourceUsage=>job.ReadUsage();
    internal object Snapshot=>new{phase=Phase,coreState=core.State,coreStartupIssue=core.StartupIssue,corePid=core.ProcessId,transportHealth=core.TransportHealth,validation=core.ValidationSummary,nativeDnsProtocol=core.NativeDnsProtocol,fakeIpVpnDials=core.FakeIpVpnDials,fakeIpRefused=core.FakeIpRefused,repairs=core.Repairs,bindings=guard.Bindings,policyDigest=guard.Digest,guardFailure=guard.LastFailureStage,ipv6Mode="BlockProtected",profile=profiles.Snapshot(),domainPolicyRevision=publisher?.Revision??0,domainPolicyPhase=publisher?.Phase??"Unavailable",policyDirectDials=core.PolicyDirectDials,policyBlockedDials=core.PolicyBlockedDials,retiredDirectScopes=publisher?.RetiredScopes??0,automaticRouting=automaticState,automaticFailureStage=automaticFailureStage,automaticFailureType=automaticFailureType,zapretState=zapret?.State??"NotConfigured",zapretProbeReason=zapret?.LastProbe?.Reason,zapretFailure=zapret?.Failure is {} failure?new{stage=failure.Stage,reason=failure.Reason}:null,zapretConnector=core.ConnectorCounts is {} counts?new{scope="current-connector",accepted=counts.Accepted,rejected=counts.Rejected}:null,networkEvents=networkTelemetry.Snapshot(),lastPathProbeReason=core.LastPathProbe?.Reason,observedDestinations=core.Observations.Length,identifiedDestinations=core.Observations.Count(o=>!string.IsNullOrWhiteSpace(o.Application)),calibratedDomains=publisher?.AutomaticRuleCount??0,calibrationChecks=Volatile.Read(ref calibrationChecks),calibrationPassed=Volatile.Read(ref calibrationPassed),calibrationFailed=Volatile.Read(ref calibrationFailed),calibrationErrors=Volatile.Read(ref calibrationErrors)};
    internal async Task PublishPolicyAsync(NativePolicyRequest request,CancellationToken stop)
    {
        // Refuse malformed or already-cancelled requests before stopping the
        // automatic controller; manual and automatic tables are not yet merged.
        stop.ThrowIfCancellationRequested();
        request=NativeRoutePolicyPublisher.SnapshotRequest(request);
        stop.ThrowIfCancellationRequested();
        // Resolve all read-only prerequisites before stopping calibration. A
        // missing core/config/interface must not disable a working auto mode.
        if(released||retired||!core.SupportsPolicy)throw new InvalidOperationException("Native policy controller unavailable.");
        using var document=JsonDocument.Parse(await File.ReadAllTextAsync(configPath,stop));
        var routes=request.Rules.Where(r=>r.Route is "direct" or "dpi").Select(r=>r.Route).Distinct().ToArray();
        string? physical=null;
        foreach(string route in routes){var outbound=document.RootElement.GetProperty("outbounds").EnumerateArray().Single(x=>x.GetProperty("tag").GetString()=="native-"+route);string? name=outbound.GetProperty("bind_interface").GetString();if(outbound.GetProperty("type").GetString()!="direct"||string.IsNullOrEmpty(name)||physical!=null&&physical!=name)throw new InvalidDataException("Native Direct outbound is not pinned.");physical=name;}
        var nic=physical!=null?NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.Name==physical&&n.OperationalStatus==OperationalStatus.Up):NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.OperationalStatus==OperationalStatus.Up&&n.GetIPProperties().GatewayAddresses.Any(g=>g.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork));
        uint interfaceIndex=checked((uint)nic.GetIPProperties().GetIPv4Properties().Index);
        if(publisher is { } existing&&existing.PhysicalInterfaceIndex!=interfaceIndex)throw new IOException("Native policy publisher interface changed.");
        stop.ThrowIfCancellationRequested();
        // Serialize and revalidate before disturbing the automatic controller.
        // A cancelled lock wait or changed interface cannot have sent a manual
        // policy, so it must not stop the currently working automatic session.
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(stop,policyStop.Token);stop=linked.Token;
        await policyGate.WaitAsync(stop);
        try
        {
            if(released||retired||!core.SupportsPolicy)throw new InvalidOperationException("Native policy controller unavailable.");
            var currentNic=NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.Id==nic.Id&&n.OperationalStatus==OperationalStatus.Up);
            if(checked((uint)currentNic.GetIPProperties().GetIPv4Properties().Index)!=interfaceIndex||
               physical==null&&!currentNic.GetIPProperties().GatewayAddresses.Any(g=>g.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork))throw new IOException("Native policy interface changed.");
            if(publisher is { } activePublisher&&activePublisher.PhysicalInterfaceIndex!=interfaceIndex)throw new IOException("Native policy publisher interface changed.");
            stop.ThrowIfCancellationRequested();
            automaticStop.Cancel();if(automaticTask!=null)await automaticTask.WaitAsync(stop);
            publisher??=new NativeRoutePolicyPublisher(guard,core,coreImage,interfaceIndex,zapretPolicyGate,forcedStore);
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
    async Task ApplyInitialForcedPolicyAsync(CancellationToken stop)
    {
        if(forcedStore==null)return;
        var intent=forcedStore.Read(1);if(intent.Rules.Length==0)return;
        if(intent.Rules.Any(rule=>rule.Route=="zapret")){if(zapret==null)throw new IOException("Forced Zapret requires a protected Zapret bundle.");await zapret.EnsureReadyAsync(stop);}
        using var document=JsonDocument.Parse(await File.ReadAllTextAsync(configPath,stop));
        var outbound=document.RootElement.GetProperty("outbounds").EnumerateArray().SingleOrDefault(x=>x.GetProperty("tag").GetString()=="native-direct");
        if(outbound.ValueKind==JsonValueKind.Undefined||outbound.GetProperty("type").GetString()!="direct")throw new InvalidDataException("Forced policy requires a pinned Direct outbound.");
        string name=outbound.GetProperty("bind_interface").GetString()!;var nic=NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.Name==name&&n.OperationalStatus==OperationalStatus.Up);
        publisher??=new NativeRoutePolicyPublisher(guard,core,coreImage,checked((uint)nic.GetIPProperties().GetIPv4Properties().Index),zapretPolicyGate,forcedStore);
        await publisher.ApplyAsync(Guid.NewGuid().ToString("N"),[],stop);
    }
    internal async Task<JsonElement> ConnectAsync(JsonElement spec,CancellationToken stop)
    {
        if(released)throw new InvalidOperationException("Native session was retired.");Phase="Connecting";if(guard.Luid==0)await guard.BindAsync(stop);var result=await profiles.SwitchAsync(spec,stop);
        try{await ApplyInitialForcedPolicyAsync(stop);}
        catch
        {
            Phase="PolicyBlocked";
            try{using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));await core.FailClosedPolicyAsync(core.ProcessId,guard.CloseAsync,timeout.Token);}catch{}
            throw;
        }
        Phase="Connected";scopeMaintenance??=MaintainScopes();if(adaptive&&automaticTask==null)automaticTask=Calibrate();return result;
    }
    internal async Task SweepScopesAsync(CancellationToken stop){if(publisher!=null)await publisher.MaintainAsync(stop);}
    async Task MaintainScopes()
    {
        try{while(!policyStop.IsCancellationRequested){await Task.Delay(5000,policyStop.Token);if(core.State is not ("Active" or "TransportDegraded"))continue;try{await SweepScopesAsync(policyStop.Token);}catch(OperationCanceledException)when(policyStop.IsCancellationRequested){throw;}catch{/* Retry without revoking unconfirmed permissions. */}}}
        catch(OperationCanceledException)when(policyStop.IsCancellationRequested){}
    }
    async Task Calibrate()
    {
        automaticStage="Initializing";
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(policyStop.Token,automaticStop.Token);var stop=linked.Token;
        try
        {
            using var document=JsonDocument.Parse(await File.ReadAllTextAsync(configPath,stop));
            var outbound=document.RootElement.GetProperty("outbounds").EnumerateArray().SingleOrDefault(x=>x.GetProperty("tag").GetString()=="native-direct");
            if(outbound.ValueKind==JsonValueKind.Undefined){automaticState="NotConfigured";return;}
            if(outbound.GetProperty("type").GetString()!="direct")throw new InvalidDataException("Adaptive Direct outbound refused.");
            string name=outbound.GetProperty("bind_interface").GetString()!;var nic=NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.Name==name&&n.OperationalStatus==OperationalStatus.Up);
            string NetworkStamp()=>string.Join(";",NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.Id==nic.Id).SelectMany(n=>{
                var p=n.GetIPProperties();return p.UnicastAddresses.Select(a=>"address:"+a.Address)
                    .Concat(p.GatewayAddresses.Select(a=>"gateway:"+a.Address)).Concat(p.DnsAddresses.Select(a=>"dns:"+a))
                    .Append("status:"+n.OperationalStatus).Append("index:"+(p.GetIPv4Properties()?.Index.ToString()??"none"));
            }).OrderBy(x=>x,StringComparer.Ordinal));
            string networkStamp=NetworkStamp();var networkGeneration=new NativeNetworkGeneration();
            NativeNetworkEvidence? EvidenceFromStamp(string stamp){if(string.IsNullOrEmpty(stamp))return null;string routes=NativeIpv4Routes.ReadStable();return NetworkStamp()==stamp?new(stamp,routes):null;}
            NativeNetworkEvidence? ReadEvidence()=>EvidenceFromStamp(NetworkStamp());
            NativeNetworkEvidence? TryEvidence(){try{return ReadEvidence();}catch{return null;}}
            NativeNetworkEvidence? appliedEvidence=TryEvidence();var networkEvidenceGate=new NativeNetworkEvidenceGate(appliedEvidence);
            void InvalidateNetwork(){if(zapret!=null)zapret.Invalidate();else zapretPolicyGate.Invalidate();networkGeneration.Changed();core.NotifyNetworkChanged();}
            void NetworkChanged(bool address){networkTelemetry.Event(address);networkEvidenceGate.Observe(!address,()=>{string stamp;try{stamp=NetworkStamp();}catch{networkTelemetry.Stamp(null);return null;}networkTelemetry.Stamp(string.IsNullOrEmpty(stamp)?null:stamp!=Volatile.Read(ref networkStamp));return EvidenceFromStamp(stamp);},InvalidateNetwork);}
            NetworkAddressChangedEventHandler addressChanged=(_,_)=>NetworkChanged(true);
            NetworkAvailabilityChangedEventHandler availabilityChanged=(_,_)=>NetworkChanged(false);
            NetworkChange.NetworkAddressChanged+=addressChanged;
            try{
            NetworkChange.NetworkAvailabilityChanged+=availabilityChanged;
            try{
            long networkVersion=networkGeneration.Current;
            publisher??=new NativeRoutePolicyPublisher(guard,core,coreImage,checked((uint)nic.GetIPProperties().GetIPv4Properties().Index),zapretPolicyGate,forcedStore);
            var attempted=new NativeCalibrationSchedule();var budget=new NativeCalibrationBudget();int pid=core.ProcessId;automaticState="Observing";
            while(!stop.IsCancellationRequested)
            {
                try{
                automaticStage="Waiting";await core.WaitForObservationsAsync(stop);if(core.State!="Active")continue;
                if(!core.SupportsAutomaticProof){automaticState="CoreUpdateRequired";continue;}
                if(core.ProcessId!=pid){automaticStage="CoreReset";int nextPid=core.ProcessId;if(!await publisher.ResetForCoreAsync(nextPid,stop))continue;networkTelemetry.Reset(true);pid=nextPid;attempted.Clear();automaticState="Observing";continue;}
                automaticStage="NetworkEvidence";NativeNetworkEvidence? currentEvidence=null;networkEvidenceGate.Observe(false,()=>currentEvidence=TryEvidence(),InvalidateNetwork);if(currentEvidence==null){automaticState="Observing";continue;}
                long currentVersion=networkGeneration.Current;if(currentEvidence!=appliedEvidence||currentVersion!=networkVersion){automaticStage="NetworkReset";await publisher.ResetForNetworkAsync(stop);networkTelemetry.Reset(false);appliedEvidence=currentEvidence;Volatile.Write(ref networkStamp,currentEvidence.Nic);networkVersion=currentVersion;attempted.Clear();automaticState="Observing";}
                long now=Environment.TickCount64;var selected=attempted.Select(core.Observations,now);if(selected==null)continue;
                if(!budget.TryAcquire(now)){automaticState="Throttled";continue;}
                attempted.Mark(selected,now);automaticState="CheckingDirect";
                Interlocked.Increment(ref calibrationChecks);
                try{using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(zapret==null?10:15));
                    bool Current()=>core.ProcessId==pid&&core.State=="Active"&&networkVersion==networkGeneration.Current&&networkEvidenceGate.Matches(appliedEvidence,ReadEvidence)&&networkVersion==networkGeneration.Current;
                    var previousProbe=core.LastPathProbe;
#if CITA_DPI_LAB
                    bool simulate=LabSimulateDirectTimeout&&Environment.GetEnvironmentVariable("CITA_NATIVE_DPI_LAB")=="1"&&selected.Domain=="cloudflare-dns.com";
                    bool success;
                    if(simulate){core.TraceCalibration("started",selected);success=false;}
                    else success=await publisher.CalibrateAsync(selected,deadline.Token,Current);
#else
                    bool success=await publisher.CalibrateAsync(selected,deadline.Token,Current);
#endif
                    var directProbe=core.LastPathProbe;
#if CITA_DPI_LAB
                    if(simulate)directProbe=new NativePathProbeResult(false,"dial-timeout");
                    if(!success&&Environment.GetEnvironmentVariable("CITA_NATIVE_DPI_LAB")=="1"&&(simulate||!ReferenceEquals(previousProbe,directProbe))&&EligibleDpiFailure(directProbe)&&Current()){
                        automaticState="CheckingDpi";success=await publisher.CalibrateDpiForLabAsync(selected,deadline.Token,Current);
                    }
#endif
                    if(!success&&zapret!=null&&!ReferenceEquals(previousProbe,directProbe)&&NativeZapretSession.EligibleFailure(directProbe)&&Current())
                    {
                        automaticState="CheckingZapret";
                        success=await publisher.CalibrateZapretAsync(selected,token=>zapret.ProbeAsync(selected,token),deadline.Token,Current);
                    }
                    if(success){Interlocked.Increment(ref calibrationPassed);automaticState="Observing";}else{Interlocked.Increment(ref calibrationFailed);automaticState="VpnFallback";}}
                catch(OperationCanceledException)when(stop.IsCancellationRequested){throw;}
                catch{Interlocked.Increment(ref calibrationErrors);automaticState="VpnFallback";}
                }catch(NativePolicyRepairRequiredException){automaticState="Observing";}
            }
            }finally{NetworkChange.NetworkAvailabilityChanged-=availabilityChanged;}
            }finally{NetworkChange.NetworkAddressChanged-=addressChanged;}
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){automaticState="Stopped";}
        catch(Exception error){automaticFailureStage=automaticStage;automaticFailureType=error switch{IOException=>"Io",InvalidOperationException=>"InvalidState",OperationCanceledException=>"Canceled",_=>"Other"};automaticState="Unavailable";}
    }
    internal async Task PauseAsync(string session,ulong generation,CancellationToken stop)
    {await profiles.StopAsync(session,generation);await guard.CloseAsync(stop);}
    internal async Task DisconnectAsync(CancellationToken stop)
    {await RetireAsync(stop);await guard.RemoveAsync(stop);}
    async Task RetireAsync(CancellationToken stop)
    {
        if(retired)return;if(zapret!=null)zapret.Invalidate();else zapretPolicyGate.Invalidate();decisionTrace.Enable(false);released=true;policyStop.Cancel();if(automaticTask!=null)await automaticTask.WaitAsync(stop);if(scopeMaintenance!=null)await scopeMaintenance.WaitAsync(stop);await policyGate.WaitAsync(stop);policyGate.Release();
        // An owned connector may still retain a socket or worker when its
        // retirement cannot be confirmed. Keep the Job intact and permit a
        // retry rather than masking that failure and terminating its owner.
        await core.DisposeAsync();
        try{await guard.CloseAsync(stop);}catch{}
        try{await profiles.DisposeAsync();}catch{}
        await job.TerminateAsync(stop);job.Dispose();retired=true;
    }
    public async ValueTask DisposeAsync()
    {using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await RetireAsync(deadline.Token);}
}
