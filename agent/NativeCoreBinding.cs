using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoutePilot.Agent;

internal sealed record NativePathProbeResult(bool Reachable,string Reason)
{
    internal static NativePathProbeResult Read(JsonElement reply)
    {
        bool present=reply.TryGetProperty("probeReachable",out var value);
        if(present&&value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new InvalidDataException("Invalid probe reachability.");
        bool reachable=present&&value.ValueKind==JsonValueKind.True;
        if(!reply.TryGetProperty("probeResult",out var field))return new(reachable,"legacy-unclassified");
        if(field.ValueKind!=JsonValueKind.String)throw new InvalidDataException("Invalid probe result category.");
        string? reason=field.GetString();
        if(reason is not ("reachable" or "dial-failed" or "dial-timeout" or "deadline-failed" or "deadline-timeout" or "tls-failed" or "tls-timeout" or "tls-certificate" or "http-write-failed" or "http-write-timeout" or "http-header-failed" or "http-header-timeout" or "http-refused" or "http-body-failed" or "http-body-timeout" or "http-body-empty"))throw new InvalidDataException("Unknown probe result category.");
        if(reachable!=(reason=="reachable"))throw new InvalidDataException("Inconsistent probe result.");
        return new(reachable,reason);
    }
}
internal sealed record NativeConnectorCounts(int Accepted,int Rejected);
internal sealed class NativeCorePolicyRefusedException : IOException;

internal interface INativeAwgBinding
{
    Task QuarantineAsync(JsonElement spec,CancellationToken stop);
    Task ActivateAsync(NativeAwgBackend candidate,JsonElement spec,JsonElement ready,CancellationToken stop);
    Task CloseAsync(CancellationToken stop);
}

// Isolated core candidate only. Its caller supplies trusted component/config
// paths. All mutable endpoint credentials stay on the inherited stdin pipe.
internal sealed class NativeCoreBinding : INativeAwgBinding,IAsyncDisposable
{
    readonly string coreImage,config,nativeImage,owner,expectedExit;
    readonly SemaphoreSlim gate=new(1);
    // A connector belongs to this exact core binding. A failed retirement
    // retains the same instance and prevents replacement until confirmed.
    readonly SemaphoreSlim connectorGate=new(1);
    readonly SemaphoreSlim lifecycleGate=new(1);
    NativeCoreConnector? ownedConnector;
    internal NativeConnectorCounts? ConnectorCounts
    {
        get{var selected=Volatile.Read(ref ownedConnector);if(selected==null)return null;var counts=new NativeConnectorCounts(selected.Accepted,selected.Rejected);return ReferenceEquals(selected,Volatile.Read(ref ownedConnector))?counts:null;}
    }
    bool connectorAdmission;
    bool disposedTerminal;
    readonly Func<JsonElement,CancellationToken,Task>? quarantined;
    readonly Func<NativeAwgBackend,JsonElement,CancellationToken,Task>? beforeValidation;
    readonly Func<JsonElement,string>? expectedEgress;
    readonly bool persistentControl;
    readonly Action<Process>? started;
    readonly Func<ProcessStartInfo,NativeChildProcess>? launchOwned;
    Process? process;
    NativeChildProcess? ownedIO;
    readonly List<(Process Process,NativeChildProcess? Owned)> retainedProbes=[];
    StreamWriter Input=>ownedIO?.Input??process!.StandardInput;
    StreamReader Output=>ownedIO?.Output??process!.StandardOutput;
    StreamReader Error=>ownedIO?.Error??process!.StandardError;
    string session="";
    ulong generation;
    bool faulted;
    internal JsonElement LastState{get;private set;}
    internal NativeValidationEvidence? LastValidation{get;private set;}
    internal bool SupportsPolicy{get;private set;}
    internal bool SupportsDirectScopes{get;private set;}
    internal bool SupportsZapretConnector{get;private set;}
    internal bool SupportsAutomaticProof{get;private set;}
    internal bool SupportsForcedPolicy{get;private set;}
    internal long PolicyDirectDials{get;private set;}
    internal int NativeDnsProtocol{get;private set;}
    internal long FakeIpVpnDials{get;private set;}
    internal long FakeIpVpnAttempts{get;private set;}
    internal long FakeIpRefused{get;private set;}
    internal long PolicyBlockedDials{get;private set;}
    internal string? StartupIssue{get;private set;}
    internal NativePathProbeResult? LastPathProbe{get;private set;}
    internal NativeObservedDestination[] Observations{get;private set;}=[];
    internal int ProcessId{get{try{return process?.Id??0;}catch(InvalidOperationException){return 0;}}}
    internal bool ProcessExitConfirmed{get{try{return process!=null&&process.HasExited;}catch(InvalidOperationException){return false;}}}
    internal NativeCoreBinding(string coreImage,string config,string nativeImage,string owner,string expectedExit,Func<JsonElement,CancellationToken,Task>? quarantined=null,Func<NativeAwgBackend,JsonElement,CancellationToken,Task>? beforeValidation=null,Func<JsonElement,string>? expectedEgress=null,bool persistentControl=false,Action<Process>? started=null,Func<ProcessStartInfo,NativeChildProcess>? launchOwned=null)
    {this.coreImage=Path.GetFullPath(coreImage);this.config=Path.GetFullPath(config);this.nativeImage=Path.GetFullPath(nativeImage);this.owner=owner;this.expectedExit=expectedExit;this.quarantined=quarantined;this.beforeValidation=beforeValidation;this.expectedEgress=expectedEgress;this.persistentControl=persistentControl;this.started=started;this.launchOwned=launchOwned;if(started!=null&&launchOwned!=null)throw new ArgumentException("Choose one core ownership strategy.");}
    internal async Task StartAsync(CancellationToken stop)
    {
        var info=new ProcessStartInfo(coreImage){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add("run");info.ArgumentList.Add("-c");info.ArgumentList.Add(config);
        info.Environment["CITA_NATIVE_BINDING_LAB"]="1";info.Environment["CITA_NATIVE_UDP_LIFETIME_LAB"]="1";
        if(persistentControl)info.Environment["CITA_NATIVE_PERSISTENT_CONTROL"]="1";
        if(launchOwned!=null){ownedIO=launchOwned(info);process=ownedIO.Process;}else{process=Process.Start(info)??throw new IOException("Binding core did not start.");started?.Invoke(process);}var errors=ReadStartupErrors(Error.BaseStream);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(10));
        JsonElement ready;try{ready=await NativeAwgBackend.ReadReply(Output,deadline.Token);}catch{try{string text=await errors.WaitAsync(TimeSpan.FromSeconds(1));var field=Regex.Match(text,@"unknown field\s+""([a-zA-Z0-9_]{1,64})""");StartupIssue=field.Success?"UnsupportedField:"+field.Groups[1].Value:text.Contains("interface",StringComparison.OrdinalIgnoreCase)?"InterfaceInitializationFailed":text.Contains("wintun",StringComparison.OrdinalIgnoreCase)?"TunDriverUnavailable":text.Contains("address already in use",StringComparison.OrdinalIgnoreCase)?"AddressInUse":"CoreStartupRejected";}catch{StartupIssue="CoreStartupUnconfirmed";}throw;}
        if(ready.GetProperty("stage").GetString()!="BindingControlReady"||ready.GetProperty("owner").GetString()!=owner||!ready.GetProperty("success").GetBoolean())throw new IOException("Core control readiness mismatch.");
        SupportsPolicy=ready.TryGetProperty("policyProtocol",out var protocol)&&protocol.GetInt32()==1;
        SupportsDirectScopes=ready.TryGetProperty("directScopeProtocol",out var scopeProtocol)&&scopeProtocol.GetInt32()==1;
        SupportsZapretConnector=ready.TryGetProperty("zapretConnectorProtocol",out var connectorProtocol)&&connectorProtocol.GetInt32()==1;
        SupportsAutomaticProof=ready.TryGetProperty("automaticProofProtocol",out var proofProtocol)&&proofProtocol.ValueKind==JsonValueKind.Number&&proofProtocol.TryGetInt32(out int proofVersion)&&proofVersion==1;
        SupportsForcedPolicy=ready.TryGetProperty("forcedPolicyProtocol",out var forcedProtocol)&&forcedProtocol.ValueKind==JsonValueKind.Number&&forcedProtocol.TryGetInt32(out int forcedVersion)&&forcedVersion==1;
    }
    static async Task<string> ReadStartupErrors(Stream input)
    {
        byte[] bytes=new byte[1024];using var captured=new MemoryStream();int count;while((count=await input.ReadAsync(bytes))>0){if(captured.Length<16384)captured.Write(bytes,0,(int)Math.Min(count,16384-captured.Length));}return System.Text.Encoding.UTF8.GetString(captured.ToArray());
    }
    internal async Task<JsonElement> CommandAsync(string action,string id,ulong gen,int port=0,string token="",bool requireSuccess=true,CancellationToken stop=default,object? policy=null,object? observation=null,object? scope=null,ulong revision=0,object? connector=null,ulong? expectedPolicyRevision=null,string? expectedPolicyDigest=null,ulong? expectedForcedRevision=null,NativeDirectPermission? expectedScope=null,Action? beforeWrite=null)
    {
        await gate.WaitAsync(stop);
        try
        {
            stop.ThrowIfCancellationRequested();if(process==null||faulted||process.HasExited)throw new IOException("Core control unavailable.");
            // Once written, consume the acknowledgement even if the initiating
            // user operation is cancelled. This keeps the private stream aligned.
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var command=connector!=null?JsonSerializer.Serialize(new{Owner=owner,Action=action,Session=id,Generation=gen,Port=port,Token=token,Connector=connector}):scope!=null?JsonSerializer.Serialize(new{Owner=owner,Action=action,Session=id,Generation=gen,Port=port,Token=token,Scope=scope,Revision=revision}):observation!=null?JsonSerializer.Serialize(new{Owner=owner,Action=action,Session=id,Generation=gen,Port=port,Token=token,Observation=observation}):policy==null?JsonSerializer.Serialize(new{Owner=owner,Action=action,Session=id,Generation=gen,Port=port,Token=token}):JsonSerializer.Serialize(new{Owner=owner,Action=action,Session=id,Generation=gen,Port=port,Token=token,Policy=policy});
            bool refused=false;
            try
            {
                beforeWrite?.Invoke();await Input.WriteLineAsync(command.AsMemory(),deadline.Token);await Input.FlushAsync(deadline.Token);
                // The bounded 128-entry observation table can exceed an 8 KiB
                // owner reply, especially with escaped Windows image paths.
                var reply=await NativeAwgBackend.ReadReply(Output,deadline.Token,maxChars:262144);
                if(reply.GetProperty("stage").GetString()!=action||reply.GetProperty("owner").GetString()!=owner||reply.GetProperty("session").GetString()!=id||reply.GetProperty("generation").GetUInt64()!=gen)throw new IOException("Core reply mismatch.");
                if(requireSuccess&&!reply.GetProperty("success").GetBoolean()){refused=true;if(action=="Policy")throw new NativeCorePolicyRefusedException();throw new InvalidDataException("Core binding command refused.");}
                if(expectedPolicyRevision.HasValue)VerifyPolicyAck(reply,expectedPolicyRevision.Value,expectedPolicyDigest!,expectedForcedRevision);
                if(expectedScope!=null)VerifyScopeAck(reply,revision,expectedScope,action=="RetireDirect");
                // Decode every field before publishing any of them: malformed
                // later fields must not leave a partially updated snapshot.
                var observations=action=="Snapshot"?(reply.TryGetProperty("observations",out var observed)?JsonSerializer.Deserialize<NativeObservedDestination[]>(observed.GetRawText())??[]:[]):Observations;
                long directValue=reply.TryGetProperty("policyDirectDials",out var direct)?direct.GetInt64():PolicyDirectDials;
                long blockedValue=reply.TryGetProperty("policyBlockedDials",out var blocked)?blocked.GetInt64():PolicyBlockedDials;
                bool hasDns=reply.TryGetProperty("nativeDnsProtocol",out var dns);int dnsValue=hasDns?dns.GetInt32():NativeDnsProtocol;
                long recoveredValue=FakeIpVpnDials,attemptsValue=FakeIpVpnAttempts,refusedValue=FakeIpRefused;
                if(hasDns&&dnsValue==1){if(reply.TryGetProperty("fakeIpVpnDials",out var recovered))recoveredValue=recovered.GetInt64();if(reply.TryGetProperty("fakeIpVpnAttempts",out var attempts))attemptsValue=attempts.GetInt64();if(reply.TryGetProperty("fakeIpRefused",out var fakeRefused))refusedValue=fakeRefused.GetInt64();}
                Observations=observations;PolicyDirectDials=directValue;PolicyBlockedDials=blockedValue;NativeDnsProtocol=dnsValue;FakeIpVpnDials=recoveredValue;FakeIpVpnAttempts=attemptsValue;FakeIpRefused=refusedValue;
                LastState=reply;
                return reply;
            }
            catch(NativeCorePolicyRefusedException)when(refused){throw;}
            catch(InvalidDataException)when(refused){throw;}
            catch{faulted=true;Input.Close();throw;}
        }
        finally{gate.Release();}
    }
    internal static void VerifyPolicyAck(JsonElement reply,ulong revision,string digest,ulong? forcedRevision=null)
    {
        if(!reply.TryGetProperty("policyRevision",out var seenRevision)||seenRevision.ValueKind!=JsonValueKind.Number||!seenRevision.TryGetUInt64(out var value)||value!=revision||
             !reply.TryGetProperty("policyDigest",out var seenDigest)||seenDigest.ValueKind!=JsonValueKind.String||!string.Equals(seenDigest.GetString(),digest,StringComparison.OrdinalIgnoreCase)||
             forcedRevision.HasValue&&(!reply.TryGetProperty("forcedIntentRevision",out var seenForced)||seenForced.ValueKind!=JsonValueKind.Number||!seenForced.TryGetUInt64(out var forcedValue)||forcedValue!=forcedRevision.Value))
            throw new IOException("Native policy acknowledgement differs.");
    }
    internal static void VerifyScopeAck(JsonElement reply,ulong revision,NativeDirectPermission permission,bool retirement=false)
    {
        if(!reply.TryGetProperty("policyRevision",out var seenRevision)||seenRevision.ValueKind!=JsonValueKind.Number||!seenRevision.TryGetUInt64(out var rev)||rev!=revision||
           !reply.TryGetProperty("scope",out var seen)||seen.ValueKind!=JsonValueKind.Object||
           !seen.TryGetProperty("Address",out var address)||address.ValueKind!=JsonValueKind.String||address.GetString()!=permission.Server||
           !seen.TryGetProperty("Network",out var network)||network.ValueKind!=JsonValueKind.String||network.GetString()!=permission.Protocol||
           !seen.TryGetProperty("Port",out var port)||port.ValueKind!=JsonValueKind.Number||!port.TryGetUInt16(out var value)||value!=permission.Port)
            throw new IOException("Native scope acknowledgement mismatch.");
        // The current core omits false due to JSON omitempty; only a malformed
        // present value is a protocol violation. Missing means not retired.
        if(retirement&&reply.TryGetProperty("scopeRetired",out var retired)&&retired.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new IOException("Native scope retirement acknowledgement malformed.");
    }
    public async Task QuarantineAsync(JsonElement spec,CancellationToken stop)
    {
        var id=NativeAwgBackend.ValidateStart(spec);
        await lifecycleGate.WaitAsync(stop);
        try
        {
            if(disposedTerminal)throw new ObjectDisposedException(nameof(NativeCoreBinding));
            await StopOwnedConnectorAsync(stop);
            var ack=await CommandAsync("Quarantine",id.Session,id.Generation,stop:stop);session=id.Session;generation=id.Generation;
            if(quarantined!=null)await quarantined(ack,stop);stop.ThrowIfCancellationRequested();
        }
        finally{lifecycleGate.Release();}
    }
    internal async Task<JsonElement> ProbeAsync(string mode,object spec,CancellationToken stop)
    {
        var info=new ProcessStartInfo(nativeImage){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add(mode);
        var child=launchOwned?.Invoke(info);
        var p=child?.Process??Process.Start(info)??throw new IOException("Candidate probe did not start.");
        var input=child?.Input??p.StandardInput;var output=child?.Output??p.StandardOutput;var error=child?.Error??p.StandardError;
        _=error.BaseStream.CopyToAsync(Stream.Null);
        try{await input.WriteLineAsync(JsonSerializer.Serialize(spec).AsMemory(),stop);await input.FlushAsync(stop);input.Close();var reply=await NativeAwgBackend.ReadReply(output,stop);await p.WaitForExitAsync(stop);return reply;}
        finally
        {
            try{await RetireProcessAsync(p,true);}
            catch{lock(retainedProbes)retainedProbes.Add((p,child));throw;}
            if(child!=null)child.Dispose();else p.Dispose();
        }
    }
    static async Task RetireProcessAsync(Process p,bool descendants=false)
    {
        if(!p.HasExited){try{p.Kill(descendants);}catch(InvalidOperationException)when(p.HasExited){}}
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if(!p.HasExited)throw new IOException("Native child retirement unconfirmed.");
    }
    public async Task ActivateAsync(NativeAwgBackend candidate,JsonElement spec,JsonElement ready,CancellationToken stop)
    {
        await lifecycleGate.WaitAsync(stop);
        try
        {
        if(disposedTerminal)throw new ObjectDisposedException(nameof(NativeCoreBinding));
        var id=NativeAwgBackend.ValidateStart(spec);int port=ready.GetProperty("port").GetInt32();string token=spec.GetProperty("Token").GetString()!;
        await StopOwnedConnectorAsync(stop);
        if(beforeValidation!=null)await beforeValidation(candidate,spec,stop);
        // The expectation belongs to the trusted controller, never to imported
        // configuration. Different server profiles can legitimately have different exits.
        string expected=expectedEgress?.Invoke(spec)??expectedExit;
        if(expected.Length!=64||!expected.All(Uri.IsHexDigit))throw new InvalidDataException("Candidate exit expectation unavailable.");
        LastValidation=null;int attempts=1;
        JsonElement Snapshot()=>JsonSerializer.SerializeToElement(new{Session=id.Session,Generation=id.Generation,Action="Snapshot"});
        var owner=await candidate.SendAsync(Snapshot(),stop);
        var https=await ProbeAsync("--proxy-client",new{Port=port,Token=token,LiteralIPv4=true},stop);
        string? failure=https.TryGetProperty("failureCode",out var failureValue)?failureValue.GetString():null;
        if(NativeProbePolicy.RetryAnonymousHttps(https,owner,attempts))
        {
            var current=await candidate.SendAsync(Snapshot(),stop);
            if(current.GetProperty("state").GetString()=="RunningUnverified"&&current.GetProperty("epoch").GetInt32()==owner.GetProperty("epoch").GetInt32())
            {
                // Retry only our anonymous, idempotent acceptance GET on the
                // same candidate epoch; never replay an application request.
                await Task.Delay(200,stop);attempts++;
                https=await ProbeAsync("--proxy-client",new{Port=port,Token=token,LiteralIPv4=true},stop);
            }
        }
        failure=https.TryGetProperty("failureCode",out failureValue)?failureValue.GetString():null;
        bool exitMatched=https.GetProperty("success").GetBoolean()&&string.Equals(https.GetProperty("egressHash").GetString(),expected,StringComparison.OrdinalIgnoreCase);
        LastValidation=new(attempts,exitMatched,failure,false,owner.GetProperty("epoch").GetInt32(),https.TryGetProperty("egressHash",out var seenExit)?seenExit.GetString():null,https.TryGetProperty("failurePhase",out var phase)?phase.GetString():null);
        if(!exitMatched)throw NativeCandidateValidationError.Create(https.GetProperty("success").GetBoolean()?"NativeEgressMismatch":"NativeHttpsUnavailable");
        // Configured DNS may be private inside the peer. The public-only SOCKS
        // acceptance client must never mistake that policy refusal for UDP loss.
        // Keep profile DNS for resolution; use the fixed public control route here.
        int udpAttempts=1;var udp=await ProbeAsync("--proxy-udp-client",new{Port=port,Token=token,Server="1.1.1.1"},stop);
        if(NativeProbePolicy.RetryAnonymousUdp(udp,owner,1))
        {
            var current=await candidate.SendAsync(Snapshot(),stop);
            if(current.GetProperty("state").GetString()=="RunningUnverified"&&current.GetProperty("epoch").GetInt32()==owner.GetProperty("epoch").GetInt32())
            {
                // Retry only the fixed anonymous DNS acceptance test, never an
                // application datagram or an unauthenticated protocol failure.
                await Task.Delay(200,stop);udpAttempts++;
                udp=await ProbeAsync("--proxy-udp-client",new{Port=port,Token=token,Server="1.1.1.1"},stop);
            }
        }
        string? udpReason=udp.TryGetProperty("dnsFailure",out var udpFailure)?udpFailure.GetString():null;
        string? udpStage=udp.TryGetProperty("stage",out var udpStageValue)?udpStageValue.GetString():null;
        bool UdpFlag(string name)=>udp.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.True;
        int udpQueries=udp.TryGetProperty("queries",out var count)&&count.TryGetInt32(out int number)?Math.Clamp(number,0,3):0;
        LastValidation=LastValidation with {UdpAttempts=udpAttempts,UdpFailure=udpReason,UdpStage=udpStage,UdpQueries=udpQueries,UdpWrongSourceNoReply=UdpFlag("wrongSourceNoReply"),UdpInvalidFramesNoReply=UdpFlag("invalidFramesNoReply"),UdpNoReplyAfterClose=UdpFlag("noReplyAfterClose")};
        if(!udp.GetProperty("success").GetBoolean())throw NativeCandidateValidationError.Create(udpReason=="ReadTimeout"?"NativeUdpTimeout":"NativeUdpUnavailable");
        LastValidation=LastValidation with {UdpVerified=true};
        stop.ThrowIfCancellationRequested();await CommandAsync("Activate",id.Session,id.Generation,port,token,stop:stop);stop.ThrowIfCancellationRequested();
        await connectorGate.WaitAsync(stop);
        try{connectorAdmission=true;}finally{connectorGate.Release();}
        }
        finally{lifecycleGate.Release();}
    }
    public async Task CloseAsync(CancellationToken stop)
    {
        await lifecycleGate.WaitAsync(stop);
        try{await StopOwnedConnectorAsync(stop);if(generation!=0)await CommandAsync("Close",session,generation,stop:stop);}
        finally{lifecycleGate.Release();}
    }
    internal async Task PublishPolicyAsync(ulong revision,string document,string guardDigest,CancellationToken stop,Action? beforeWrite=null,ulong forcedIntentRevision=0)
    {
        byte[] bytes=System.Text.Encoding.UTF8.GetBytes(document);if(bytes.Length>65536)throw new InvalidDataException("Native policy document too large.");
        string digest=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        await CommandAsync("Policy",session,generation,stop:stop,policy:new{Revision=revision,Document=Convert.ToBase64String(bytes),GuardDigest=guardDigest},expectedPolicyRevision:revision,expectedPolicyDigest:digest,expectedForcedRevision:forcedIntentRevision>0?forcedIntentRevision:null,beforeWrite:beforeWrite);
    }
    internal async Task ConfigureZapretConnectorAsync(NativeConnectorEndpoint endpoint,CancellationToken stop)
    {
        if(!SupportsZapretConnector||generation==0||endpoint.ServerPid!=Environment.ProcessId)throw new InvalidOperationException("Zapret connector has no compatible active owned core session.");
        await CommandAsync("ZapretConnector",session,generation,stop:stop,connector:endpoint);
    }
    // Caller supplies an independently fenced authorization snapshot and a
    // real guarded connection factory. This method neither publishes routing
    // policy nor treats the presence of an IPC listener as a usable path.
    internal async Task StartOwnedConnectorAsync(string epoch,Func<bool> current,Func<NativeConnectorRequest,bool> authorized,
        Func<NativeConnectorRequest,CancellationToken,Task<INativeConnectorConnection>> connect,CancellationToken sessionStop,CancellationToken stop,NativeConnectorAdmission? admission=null,bool replace=false)
    {
        ArgumentNullException.ThrowIfNull(current);ArgumentNullException.ThrowIfNull(authorized);ArgumentNullException.ThrowIfNull(connect);
        await lifecycleGate.WaitAsync(stop);
        try
        {
            await connectorGate.WaitAsync(stop);
            try
            {
            stop.ThrowIfCancellationRequested();
            if(admission==null||disposedTerminal||!connectorAdmission||ownedConnector!=null&&!replace||process==null||process.HasExited||faulted||!SupportsZapretConnector||generation==0||!current())throw new InvalidOperationException("Connector ownership unavailable.");
            if(ownedConnector is {} previous)
            {
                // A changed uplink may replace this exact owner only after its
                // streams, socket leases and workers have actually retired.
                await previous.DisposeAsync();ownedConnector=null;
                stop.ThrowIfCancellationRequested();
                if(!current()||process.HasExited||faulted)throw new IOException("Connector generation changed during retirement.");
            }
            var selected=process;var candidate=new NativeCoreConnector(selected,epoch,current,authorized,connect,sessionStop,admission);ownedConnector=candidate;
            try
            {
                candidate.Start();stop.ThrowIfCancellationRequested();
                await ConfigureZapretConnectorAsync(candidate.Endpoint,stop);
                stop.ThrowIfCancellationRequested();if(!current()||candidate.Phase!="Listening")throw new IOException("Connector identity changed during activation.");
            }
            catch
            {
                // A failed or uncertain ACK never grants a replacement until
                // this same candidate's pending cleanup has been observed.
                try{await candidate.DisposeAsync();ownedConnector=null;}catch{ /* retain ownership for a later cleanup attempt */ }
                throw;
            }
            }
            finally{connectorGate.Release();}
        }
        finally{lifecycleGate.Release();}
    }
    internal async Task StopOwnedConnectorAsync(CancellationToken stop)
    {
        await connectorGate.WaitAsync(stop);
        try{connectorAdmission=false;if(ownedConnector is {} selected){await selected.DisposeAsync();ownedConnector=null;}}
        finally{connectorGate.Release();}
    }
    internal bool IsOwnedConnectorReady(string epoch)
    {var selected=Volatile.Read(ref ownedConnector);return selected!=null&&selected.Endpoint.Epoch==epoch&&selected.IsListening&&ReferenceEquals(selected,Volatile.Read(ref ownedConnector));}
    internal async Task<bool> ProbeDirectAsync(NativeObservedDestination observed,CancellationToken stop)
    {var reply=await CommandAsync("ProbeDirect",session,generation,stop:stop,observation:observed);LastPathProbe=NativePathProbeResult.Read(reply);return LastPathProbe.Reachable;}
#if CITA_DPI_LAB
    internal async Task<bool> ProbeDpiForLabAsync(NativeObservedDestination observed,CancellationToken stop)
    {var reply=await CommandAsync("ProbeDPI",session,generation,stop:stop,observation:observed);LastPathProbe=NativePathProbeResult.Read(reply);return LastPathProbe.Reachable;}
#endif
    internal async Task<bool> DirectScopeAsync(NativeDirectPermission permission,bool enable,ulong revision,CancellationToken stop)
    {
        if(!SupportsDirectScopes)throw new IOException("Native scope retirement protocol unavailable.");
        var scope=new{Address=permission.Server,Network=permission.Protocol,Port=permission.Port};
        var reply=await CommandAsync(enable?"EnableDirect":"RetireDirect",session,generation,stop:stop,scope:scope,revision:revision,expectedScope:permission);
        return enable||reply.TryGetProperty("scopeRetired",out var retired)&&retired.GetBoolean();
    }
    public async ValueTask DisposeAsync()
    {
        await lifecycleGate.WaitAsync();
        try
        {
            disposedTerminal=true;
            await StopOwnedConnectorAsync(CancellationToken.None);
            await gate.WaitAsync();
            try
            {
                (Process Process,NativeChildProcess? Owned)[] probes;lock(retainedProbes)probes=retainedProbes.ToArray();
                foreach(var probe in probes)
                {
                    await RetireProcessAsync(probe.Process,true);
                    if(probe.Owned!=null)probe.Owned.Dispose();else probe.Process.Dispose();
                    lock(retainedProbes)retainedProbes.Remove(probe);
                }
                if(process!=null)
                {
                    // Close can flush pending data; retire the process first so
                    // a stalled pipe cannot block cleanup indefinitely.
                    await RetireProcessAsync(process);
                    try{Input.Close();}catch(IOException){}
                    if(ownedIO!=null){ownedIO.Dispose();ownedIO=null;}else process.Dispose();process=null;
                }
            }
            finally{gate.Release();}
        }
        finally{lifecycleGate.Release();}
    }
}
