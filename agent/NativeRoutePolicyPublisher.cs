using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoutePilot.Agent;

public sealed record NativeDomainRule(string Domain,string Application,string Network,string Route,ushort Port,string[] Addresses,long ExpiresUnixMilliseconds)
{
    // Omitted for existing automatic decisions. An older core rejects the
    // unknown field when true instead of silently ignoring a forced policy.
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Forced {get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CalibrationId {get;init;}
}
public sealed record NativePolicyRequest(string Epoch,NativeDomainRule[] Rules);
internal sealed record NativeObservedDestination(string Domain,string Application,string Address,ushort Port);
internal sealed class NativeStaleEvidenceException : IOException;
internal sealed class NativeNetworkGeneration
{
    long value;
    internal long Current=>Interlocked.Read(ref value);
    internal void Changed()=>Interlocked.Increment(ref value);
}
internal sealed class NativeObservationWakeup
{
    readonly object sync=new();readonly HashSet<NativeObservedDestination> known=new();readonly SemaphoreSlim signal=new(0,1);
    internal int Pending=>signal.CurrentCount;
    internal void Wake(){try{signal.Release();}catch(SemaphoreFullException){}}
    internal bool Observe(IReadOnlyCollection<NativeObservedDestination> observations,Action<NativeObservedDestination>? onNew=null)
    {
        if(observations.Count>128)throw new InvalidDataException("Native observation signal budget exceeded.");
        lock(sync){bool changed=false;foreach(var observation in observations)if(!known.Contains(observation)){changed=true;onNew?.Invoke(observation);}known.Clear();known.UnionWith(observations);if(changed){try{signal.Release();}catch(SemaphoreFullException){}}return changed;}
    }
    internal void Reset(){lock(sync){known.Clear();while(signal.Wait(0)){}}}
    internal Task<bool> WaitAsync(CancellationToken stop,TimeSpan? fallback=null)=>signal.WaitAsync(fallback??TimeSpan.FromSeconds(1),stop);
}
internal sealed class NativeCalibrationSchedule
{
    readonly Dictionary<NativeObservedDestination,long> attempted=new();
    internal int Count=>attempted.Count;
    internal void Clear()=>attempted.Clear();
    internal NativeObservedDestination? Select(IReadOnlyCollection<NativeObservedDestination> observations,long now)
    {
        if(observations.Count>128)throw new InvalidDataException("Native observation window exceeded.");
        var current=observations.ToHashSet();
        foreach(var key in attempted.Keys.Where(k=>!current.Contains(k)).ToArray())attempted.Remove(key);
        // An empty process identity would create a domain-wide rule. On shared
        // CDN addresses this could silently change another application's path.
        return observations.Where(o=>!string.IsNullOrWhiteSpace(o.Application))
            .Where(o=>!attempted.TryGetValue(o,out long due)||due<=now)
            .OrderBy(o=>attempted.GetValueOrDefault(o,long.MinValue)).ThenBy(o=>o.Domain,StringComparer.Ordinal)
            .ThenBy(o=>o.Application,StringComparer.Ordinal).ThenBy(o=>o.Address,StringComparer.Ordinal).FirstOrDefault();
    }
    internal void Mark(NativeObservedDestination observation,long now)=>attempted[observation]=checked(now+60000);
}
internal sealed class NativeCalibrationBudget
{
    long window=long.MinValue;int used;
    internal bool TryAcquire(long now)
    {
        if(window!=long.MinValue&&now<window)return false;
        if(window==long.MinValue||now-window>=60000){window=now;used=0;}
        if(used>=32)return false;used++;return true;
    }
}

// Make-before-break applies to rules, not application payloads: acknowledge new
// routing before announcing it. Already admitted flows keep their chosen route.
// Exact permissions retire only after the core atomically disables new admission
// and confirms no policy, pending dial, probe or live socket still uses them.
internal sealed class NativeAutomaticPolicyState
{
    internal bool ResetRequired{get;private set;}
    internal bool RepairRequired{get;private set;}
    internal int PossibleCorePid{get;private set;}
    bool previousReset,possibleWrite,acknowledged,inProgress;
    internal void Begin(bool isReset)
    {
        if(inProgress||RepairRequired)throw new IOException("Automatic policy requires core repair.");
        if(ResetRequired&&!isReset)throw new IOException("Automatic policy requires empty reconciliation.");
        inProgress=true;previousReset=ResetRequired;ResetRequired=true;possibleWrite=false;acknowledged=false;
    }
    internal void BeforeWrite(int pid){if(!inProgress||pid<=0)throw new InvalidOperationException("Policy transaction identity missing.");possibleWrite=true;PossibleCorePid=pid;}
    internal void Acknowledged(){if(!inProgress||!possibleWrite)throw new InvalidOperationException("Policy write was not started.");acknowledged=true;}
    internal void Refused(){if(!inProgress||!possibleWrite)throw new InvalidOperationException("Policy refusal without write.");acknowledged=true;}
    internal void VerifyAcknowledged(){if(!inProgress||!acknowledged)throw new InvalidOperationException("Policy ACK not confirmed.");}
    internal void Commit(){if(!inProgress||!acknowledged)throw new InvalidOperationException("Policy ACK not confirmed.");inProgress=false;ResetRequired=false;PossibleCorePid=0;}
    internal bool Fail()
    {
        if(!inProgress)throw new InvalidOperationException("Policy transaction missing.");
        inProgress=false;ResetRequired=previousReset||possibleWrite;if(possibleWrite&&!acknowledged)RepairRequired=true;return possibleWrite;
    }
    internal void RequireReset()=>ResetRequired=true;
    internal void RequireRepair(){ResetRequired=true;RepairRequired=true;}
    internal void NewCore(){if(inProgress)throw new InvalidOperationException("Policy transaction active.");ResetRequired=false;RepairRequired=false;PossibleCorePid=0;}
}

internal sealed class NativePolicyRepairRequiredException(Exception cause):IOException("Policy owner repair required.",cause);
internal sealed class NativeRoutePolicyPublisher(NativeWfpGuard guard,NativeCoreRecovery core,string coreImage,uint physicalIndex,NativeZapretPolicyGate? zapret=null,NativeForcedIntentStore? forcedStore=null)
{
    readonly SemaphoreSlim gate=new(1);
    readonly NativeZapretPolicyGate zapretGate=zapret??new();
    readonly NativeForcedIntentStore? forcedStore=forcedStore;
    internal bool HasForcedIntent=>forcedStore!=null;
    readonly HashSet<NativeDirectPermission> scopes=new();
    readonly HashSet<NativeDirectPermission> admissions=new();
    readonly Dictionary<NativeDirectPermission,long> requiredUntil=new();
    ulong revision;
    int scopedPid;
    internal ulong Revision=>revision;
    internal uint PhysicalInterfaceIndex=>physicalIndex;
    string phase="Idle";
    internal string Phase{get=>Volatile.Read(ref phase);private set=>Volatile.Write(ref phase,value);}
    long forcedRefreshAt;
    readonly Dictionary<NativeObservedDestination,NativeDomainRule> automaticRules=new();
    readonly NativeAutomaticPolicyState automaticState=new();
    string automaticEpoch=Guid.NewGuid().ToString("N");
    int automaticCount;
    internal int AutomaticRuleCount=>Volatile.Read(ref automaticCount);
    // The native calibration path currently handles TCP only; port is part
    // of the published rule identity and must not be lost during CDN rotation.
    internal static bool SameAutomaticScope(NativeObservedDestination left,NativeObservedDestination right)=>
        left.Port==right.Port&&string.Equals(left.Domain.TrimEnd('.'),right.Domain.TrimEnd('.'),StringComparison.OrdinalIgnoreCase)&&
        string.Equals(left.Application,right.Application,StringComparison.OrdinalIgnoreCase);
    int retiredScopes;
    internal int RetiredScopes=>Volatile.Read(ref retiredScopes);
    internal async Task MaintainAsync(CancellationToken stop)
    {
        await gate.WaitAsync(stop);
        try
        {
            if(forcedStore!=null&&core.State=="Active"&&Environment.TickCount64>=Volatile.Read(ref forcedRefreshAt))
            {
                try
                {
                    var current=forcedStore.Read(1);
                    if(current.Rules.Length>0)await ApplyConfirmed(automaticEpoch,automaticRules.Values.ToArray(),stop);
                    Volatile.Write(ref forcedRefreshAt,Environment.TickCount64+TimeSpan.FromMinutes(4).Ticks/TimeSpan.TicksPerMillisecond);
                }
                catch
                {
                    Volatile.Write(ref forcedRefreshAt,Environment.TickCount64+30000);
                    Phase="ApplyFailed";throw;
                }
            }
            if(scopedPid>0&&core.ProcessId==scopedPid)await CollectScopes(stop);
        }
        finally{gate.Release();}
    }
    async Task CollectScopes(CancellationToken stop,HashSet<NativeDirectPermission>? preserve=null)
    {
        if(!core.SupportsDirectScopes)throw new IOException("Native scope collection unavailable.");
        foreach(var scope in scopes.ToArray())
        {
            if(preserve?.Contains(scope)==true)continue;
            if(requiredUntil.TryGetValue(scope,out long until)&&until>Environment.TickCount64)continue;
            if(!await core.DirectScopeAsync(scope,false,revision,scopedPid,stop))continue;
            admissions.Remove(scope);
            await guard.SetDirectAsync(scope,false,stop);scopes.Remove(scope);requiredUntil.Remove(scope);Interlocked.Increment(ref retiredScopes);
        }
    }
    async Task EnableScope(NativeDirectPermission scope,int pid,CancellationToken stop)
    {
        if(scopes.Contains(scope)&&admissions.Contains(scope))return;
        if(!scopes.Contains(scope)){await guard.SetDirectAsync(scope,true,stop);scopes.Add(scope);}
        if(!await guard.VerifyAsync(stop))throw new IOException("Direct scope not independently confirmed.");
        await core.DirectScopeAsync(scope,true,revision,pid,stop);
        admissions.Add(scope);
    }
    internal async Task ResetForNetworkAsync(CancellationToken stop)
    {
        await gate.WaitAsync(stop);
        try{automaticState.RequireReset();await ResetAutomaticSafelyAsync(stop);}
        finally{gate.Release();}
    }
    // Caller holds the publisher gate. A failed or cancelled publication never
    // makes its candidate the base of the next automatic policy.
    internal static async Task CommitAutomaticAsync(Dictionary<NativeObservedDestination,NativeDomainRule> confirmed,
        Dictionary<NativeObservedDestination,NativeDomainRule> candidate,Func<Task> publish,CancellationToken stop,NativeAutomaticPolicyState state,Action closeAdmission,Func<bool>? evidenceCurrent=null,bool isReset=false)
    {
        if(isReset&&candidate.Count!=0)throw new InvalidDataException("Reconciliation must commit an empty automatic table.");
        state.Begin(isReset);
        try
        {
            try{await publish();}catch(NativeCorePolicyRefusedException){state.Refused();throw;}stop.ThrowIfCancellationRequested();if(evidenceCurrent!=null&&!EvidenceStillCurrent(evidenceCurrent))throw new NativeStaleEvidenceException();
            state.VerifyAcknowledged();confirmed.Clear();foreach(var pair in candidate)confirmed.Add(pair.Key,pair.Value);state.Commit();
        }
        catch(Exception error){if(state.Fail())try{closeAdmission();}catch(Exception closeError){error.Data["AdmissionCloseFailure"]=closeError.GetType().Name;}throw;}
    }
    internal static async Task RecoverAutomaticFailureAsync(NativeAutomaticPolicyState state,Func<Task> reset,Func<Task> failClose)
    {
        if(!state.ResetRequired)return;
        if(!state.RepairRequired)
        {
            try{await reset();if(!state.ResetRequired)return;}catch{/* Fall through to guarded closure. */}
        }
        state.RequireRepair();await failClose();
    }
    async Task CommitPolicyAsync(Dictionary<NativeObservedDestination,NativeDomainRule> candidate,Func<Task> publish,CancellationToken stop,Func<bool>? evidenceCurrent=null)
    {
        try{await CommitAutomaticAsync(automaticRules,candidate,publish,stop,automaticState,zapretGate.Invalidate,evidenceCurrent);}
        catch(Exception error)
        {
            try
            {
                await RecoverAutomaticFailureAsync(automaticState,async()=>{using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));await ResetAutomaticAsync(timeout.Token);},()=>FailClosedAutomaticAsync(automaticState.PossibleCorePid!=0?automaticState.PossibleCorePid:scopedPid));
                if(automaticState.RepairRequired)Phase="RepairRequired";
            }
            catch(Exception cleanup){Phase="RecoveryUnconfirmed";error.Data["PolicyRecoveryFailure"]=cleanup.GetType().Name;}
            throw;
        }
    }
    internal static Dictionary<NativeObservedDestination,NativeDomainRule> StageAutomatic(Dictionary<NativeObservedDestination,NativeDomainRule> confirmed,NativeObservedDestination observed,NativeDomainRule rule,long now)
    {
        var candidate=new Dictionary<NativeObservedDestination,NativeDomainRule>(confirmed);
        foreach(var key in candidate.Where(x=>x.Value.ExpiresUnixMilliseconds<=now||SameAutomaticScope(x.Key,observed)).Select(x=>x.Key).ToArray())candidate.Remove(key);
        if(candidate.Count>=128){var oldest=candidate.OrderBy(x=>x.Value.ExpiresUnixMilliseconds).ThenBy(x=>x.Key.Domain,StringComparer.Ordinal).First().Key;candidate.Remove(oldest);}
        candidate[observed]=rule;return candidate;
    }
    async Task ResetAutomaticAsync(CancellationToken stop)
    {
        string epoch=Guid.NewGuid().ToString("N");var empty=new Dictionary<NativeObservedDestination,NativeDomainRule>();
        await CommitAutomaticAsync(automaticRules,empty,()=>ApplyConfirmed(epoch,[],stop),stop,automaticState,zapretGate.Invalidate,isReset:true);
        automaticEpoch=epoch;Volatile.Write(ref automaticCount,0);
    }
    async Task FailClosedAutomaticAsync(int expectedPid)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await core.FailClosedPolicyAsync(expectedPid,guard.CloseAsync,timeout.Token);
    }
    async Task ResetAutomaticSafelyAsync(CancellationToken stop)
    {
        int pid=core.ProcessId;
        try{await ResetAutomaticAsync(stop);}
        catch(Exception error)
        {
            automaticState.RequireRepair();
            try{await FailClosedAutomaticAsync(automaticState.PossibleCorePid!=0?automaticState.PossibleCorePid:pid!=0?pid:scopedPid);}
            catch(Exception cleanup){Phase="RecoveryUnconfirmed";error.Data["PolicyRecoveryFailure"]=cleanup.GetType().Name;}
            if(Phase!="RecoveryUnconfirmed"){Phase="RepairRequired";throw new NativePolicyRepairRequiredException(error);}
            throw;
        }
    }
    async Task RemoveAutomaticAsync(NativeObservedDestination observed,CancellationToken stop,Func<bool>? evidenceCurrent)
    {
        var candidate=new Dictionary<NativeObservedDestination,NativeDomainRule>(automaticRules);
        if(!candidate.Remove(observed))return;
        long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach(var key in candidate.Where(x=>x.Value.ExpiresUnixMilliseconds<=now).Select(x=>x.Key).ToArray())candidate.Remove(key);
        await CommitPolicyAsync(candidate,()=>ApplyConfirmed(automaticEpoch,candidate.Values.ToArray(),stop,evidenceCurrent),stop,evidenceCurrent);
        Volatile.Write(ref automaticCount,automaticRules.Count);
    }
    internal async Task<bool> ResetForCoreAsync(int expectedPid,CancellationToken stop)
    {
        zapretGate.Invalidate();
        await gate.WaitAsync(stop);
        try{
            // State and PID observations are not atomic with recovery. A stale
            // scheduler observation authorizes no mutation and must be retried.
            if(expectedPid<=0||core.ProcessId!=expectedPid||core.State!="Active")return false;
            // The previous process has been retired by NativeCoreRecovery, so
            // none of its admitted Direct flows can still need these scopes.
            foreach(var scope in scopes.ToArray()){await guard.SetDirectAsync(scope,false,stop);scopes.Remove(scope);}
            // A late empty reconciliation may already have reached this new
            // binding. Preserve its acknowledged revision instead of treating
            // that harmless race as a foreign process or resetting its floor.
            bool alreadyBound=scopedPid==expectedPid;
            automaticRules.Clear();automaticState.NewCore();admissions.Clear();requiredUntil.Clear();Volatile.Write(ref automaticCount,0);if(!alreadyBound)revision=0;scopedPid=expectedPid;
            // The replacement must explicitly acknowledge an empty automatic
            // table before the scheduler resumes and reports recovery.
            await ResetAutomaticSafelyAsync(stop);
            return core.ProcessId==expectedPid&&core.State=="Active";
        }finally{gate.Release();}
    }
    internal static bool ValidCalibrationId(string value)=>value.Length==32&&value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static NativeDomainRule? WithFreshCalibrationId(NativeDomainRule rule,bool successful,Func<bool> evidenceCurrent)
    {
        if(rule.CalibrationId!=null||rule.Forced||string.IsNullOrWhiteSpace(rule.Application)||rule.Route is not ("direct" or "dpi" or "zapret"))throw new InvalidDataException("Invalid calibration candidate.");
        if(!successful||!EvidenceStillCurrent(evidenceCurrent))return null;
        return rule with{CalibrationId=Guid.NewGuid().ToString("N")};
    }
    internal static void Validate(IReadOnlyCollection<NativeDomainRule> rules,bool allowZapret=false,bool allowPrivateCalibrationId=false,bool allowForced=false)
    {
        if(rules==null)throw new InvalidDataException("Native rules missing.");
        if(rules.Count>128)throw new InvalidDataException("Native route budget exceeded.");var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach(var rule in rules)
        {
            if(rule==null||rule.Domain==null||rule.Application==null||rule.Network==null||rule.Route==null||rule.Addresses==null)throw new InvalidDataException("Native route fields missing.");
            if(rule.Domain.Length>254)throw new InvalidDataException("Native domain budget exceeded.");
            // Forced rules need a durable policy owner and restart fencing.
            // Do not expose a temporary in-memory guarantee to user RPC.
            if(rule.Forced&&!allowForced)throw new InvalidDataException("Forced native routing is not yet available.");
            if(rule.CalibrationId!=null&&(!allowPrivateCalibrationId||!ValidCalibrationId(rule.CalibrationId)||string.IsNullOrWhiteSpace(rule.Application)||rule.Route is not ("direct" or "dpi" or "zapret")))throw new InvalidDataException("Private calibration identity refused.");
            if(rule.Route=="zapret"&&(!allowZapret||rule.Network!="tcp"||rule.Port!=443||string.IsNullOrEmpty(rule.Application)))throw new InvalidDataException("Zapret routing requires a private calibrated connector.");
            // Native policy can name a DPI outbound, but this engine does not
            // yet apply TLS fragmentation to that path. Never silently publish
            // a plain Direct connection as a verified DPI bypass.
            if(rule.Route=="dpi")
            {
#if CITA_DPI_LAB
                if(Environment.GetEnvironmentVariable("CITA_NATIVE_DPI_LAB")!="1"||rule.Network!="tcp"||rule.Port!=443)throw new InvalidDataException("Native DPI lab scope refused.");
#else
                throw new InvalidDataException("Native DPI routing is not implemented.");
#endif
            }
            string domain=rule.Domain.TrimEnd('.').ToLowerInvariant();
            if(domain.Length is <1 or >253||IPAddress.TryParse(domain,out _)||domain.Split('.').Any(label=>!Regex.IsMatch(label,@"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z"))||rule.Network is not ("tcp" or "udp")||rule.Port==0||rule.ExpiresUnixMilliseconds<=now||rule.ExpiresUnixMilliseconds>now+600000)throw new InvalidDataException("Native route scope refused.");
            if(rule.Application.Length>512||rule.Application.Any(c=>c<32||c==127)||rule.Application.Length>0&&!Regex.IsMatch(rule.Application,@"\A[a-zA-Z]:\\"))throw new InvalidDataException("Native application scope refused.");
            if(!keys.Add(domain+"\0"+rule.Application+"\0"+rule.Network+"\0"+rule.Port))throw new InvalidDataException("Duplicate native route.");
            if(rule.Route is "direct" or "dpi" or "zapret")
            {
                if(rule.Addresses.Length is <1 or >4||rule.Addresses.Distinct().Count()!=rule.Addresses.Length)throw new InvalidDataException("Native DNS scope refused.");
                foreach(string text in rule.Addresses){if(!IPAddress.TryParse(text,out var ip)||ip.AddressFamily!=AddressFamily.InterNetwork||ip.ToString()!=text||!Public(ip))throw new InvalidDataException("Native public IPv4 scope required.");}
            }
            else if(rule.Route is not ("vpn" or "blocked")||rule.Addresses.Length!=0)throw new InvalidDataException("Native route kind refused.");
        }
    }
    static bool Public(IPAddress ip){var b=ip.GetAddressBytes();return b[0]!=0&&b[0]!=10&&b[0]!=127&&b[0]<224&&!(b[0]==198&&(b[1]==18||b[1]==19))&&!(b[0]==169&&b[1]==254)&&!(b[0]==172&&b[1]>=16&&b[1]<=31)&&!(b[0]==192&&b[1]==168)&&!(b[0]==100&&b[1]>=64&&b[1]<=127);}
    internal static void ValidateRequest(NativePolicyRequest request)
    {
        if(request==null||request.Epoch==null||request.Epoch.Length!=32||!request.Epoch.All(Uri.IsHexDigit))throw new InvalidDataException("Native network epoch missing.");
        Validate(request.Rules);
    }
    internal static NativePolicyRequest SnapshotRequest(NativePolicyRequest request)
    {
        if(request==null||request.Rules==null)throw new InvalidDataException("Native rules missing.");
        // Reject oversized untrusted input before allocating a deep copy.
        if(request.Rules.Length>128)throw new InvalidDataException("Native route budget exceeded.");
        // Freeze the bounded outer array first; re-reading it after checking
        // lengths would let a concurrent caller substitute a huge rule.
        var rules=request.Rules.ToArray();
        for(int i=0;i<rules.Length;i++)
        {
            var rule=rules[i];if(rule==null)continue;
            var addresses=rule.Addresses;if(addresses is {Length:>4})throw new InvalidDataException("Native route budget exceeded.");
            rules[i]=rule with{Addresses=addresses?.ToArray()!};
        }
        var snapshot=new NativePolicyRequest(request.Epoch,rules);ValidateRequest(snapshot);return snapshot;
    }
    internal async Task ApplyAsync(string epoch,IReadOnlyCollection<NativeDomainRule> rules,CancellationToken stop)
    {
        ValidateRequest(new NativePolicyRequest(epoch,rules.ToArray()));
        await gate.WaitAsync(stop);
        try
        {
            if(automaticState.ResetRequired)await ResetAutomaticSafelyAsync(stop);
            var empty=new Dictionary<NativeObservedDestination,NativeDomainRule>();
            await CommitPolicyAsync(empty,()=>ApplyConfirmed(epoch,rules,stop),stop);
            Volatile.Write(ref automaticCount,0);
        }
        catch{if(Phase is not ("RepairRequired" or "RecoveryUnconfirmed"))Phase="ApplyFailed";throw;}
        finally{gate.Release();}
    }
    static bool PublicIpv4(IPAddress address)
    {
        if(address.AddressFamily!=AddressFamily.InterNetwork)return false;var b=address.GetAddressBytes();return b[0]!=0&&b[0]!=10&&b[0]!=127&&b[0]<224&&!(b[0]==169&&b[1]==254)&&!(b[0]==172&&b[1]>=16&&b[1]<=31)&&!(b[0]==192&&b[1]==168)&&!(b[0]==100&&b[1]>=64&&b[1]<=127)&&!(b[0]==198&&(b[1]==18||b[1]==19));
    }
    async Task<(NativeDomainRule[] Rules,ulong Revision)> ResolveForcedRulesAsync(CancellationToken stop)
    {
        if(forcedStore==null)return ([],0);
        var document=forcedStore.Read(1);var rules=new List<NativeDomainRule>(document.Rules.Length);long expiry=DateTimeOffset.UtcNow.AddMinutes(9).ToUnixTimeMilliseconds();
        foreach(var durable in document.Rules)
        {
            stop.ThrowIfCancellationRequested();string[] addresses=[];
            if(durable.Route is "direct" or "zapret")
            {
                var resolved=await Dns.GetHostAddressesAsync(durable.Domain,stop);addresses=resolved.Where(PublicIpv4).Select(x=>x.ToString()).Distinct(StringComparer.Ordinal).Take(4).ToArray();if(addresses.Length==0)throw new InvalidDataException("Forced route has no public IPv4 proof.");
            }
            rules.Add(new NativeDomainRule(durable.Domain,durable.Application,durable.Network,durable.Route,durable.Port,addresses,expiry){Forced=true});
        }
        return (rules.ToArray(),document.Revision);
    }
    static bool EvidenceStillCurrent(Func<bool> check){try{return check();}catch{return false;}}
    async Task ApplyConfirmed(string epoch,IReadOnlyCollection<NativeDomainRule> rules,CancellationToken stop,Func<bool>? evidenceCurrent=null)
    {
            if(!core.SupportsPolicy||!core.SupportsDirectScopes)throw new IOException("Native core does not support acknowledged scope retirement.");
            var resolved=await ResolveForcedRulesAsync(stop);var forced=resolved.Rules;var forcedRevision=resolved.Revision;
            if(forced.Length>0&&!core.SupportsForcedPolicy)throw new IOException("Native core does not support durable forced policy.");
            var combined=NativePolicyComposition.Compose(forced,rules.ToArray(),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),allowForced:true);
            Validate(combined,allowZapret:true,allowPrivateCalibrationId:true,allowForced:true);
            if(combined.Any(r=>r.CalibrationId!=null)&&!core.SupportsAutomaticProof)throw new IOException("Native automatic proof protocol unavailable.");
            object policyDocument=forced.Length>0?new{Epoch=epoch,Rules=combined,ForcedIntentRevision=forcedRevision}:new{Epoch=epoch,Rules=combined};
            string document=JsonSerializer.Serialize(policyDocument);if(System.Text.Encoding.UTF8.GetByteCount(document)>65536)throw new InvalidDataException("Native policy document budget exceeded.");
            Phase="GrantingScopes";int pid=core.ProcessId;
            if(pid!=scopedPid){if(guard.DirectPermits!=0)throw new IOException("Native core changed without scope retirement.");scopes.Clear();admissions.Clear();requiredUntil.Clear();scopedPid=pid;}
            var needed=combined.Where(r=>r.Route is "direct" or "dpi").SelectMany(r=>r.Addresses.Select(ip=>new NativeDirectPermission(coreImage,ip,r.Port,r.Network,physicalIndex))).ToHashSet();
            await CollectScopes(stop,needed);
            foreach(var scope in needed)
            {
                await EnableScope(scope,pid,stop);
            }
            if(!await guard.VerifyAsync(stop))throw new IOException("Direct scopes not independently confirmed.");
            if(evidenceCurrent!=null&&!EvidenceStillCurrent(evidenceCurrent))throw new NativeStaleEvidenceException();
            Phase="Publishing";ulong next=checked(revision+1);
            string permissionDigest=guard.DirectDigest;
            // Zapret has exact per-socket dynamic permissions instead of a
            // destination-wide Direct grant. Attest the independently checked
            // base guard when this document contains no Direct exceptions.
            if(permissionDigest.Length==0&&combined.Any(r=>r.Route=="zapret"))permissionDigest=guard.Digest;
            await zapretGate.PublishAsync(next,document,async token=>{await core.PublishPolicyAsync(next,document,permissionDigest,pid,token,()=>automaticState.BeforeWrite(pid),forced.Length>0?forcedRevision:0);if(forced.Length>0&&forcedStore!.Read(forcedRevision).Revision!=forcedRevision)throw new NativeStaleEvidenceException();revision=next;automaticState.Acknowledged();},stop,allowForced:forced.Length>0);
            Phase="Applied";
            if(evidenceCurrent!=null&&!EvidenceStillCurrent(evidenceCurrent))throw new NativeStaleEvidenceException();
            requiredUntil.Clear();long wall=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),tick=Environment.TickCount64;
            foreach(var rule in combined.Where(r=>r.Route is "direct" or "dpi"))foreach(string ip in rule.Addresses){var scope=new NativeDirectPermission(coreImage,ip,rule.Port,rule.Network,physicalIndex);long due=tick+Math.Clamp(rule.ExpiresUnixMilliseconds-wall,0,600000);requiredUntil[scope]=Math.Max(requiredUntil.GetValueOrDefault(scope),due);}
            await CollectScopes(stop);
            Volatile.Write(ref forcedRefreshAt,Environment.TickCount64+TimeSpan.FromMinutes(4).Ticks/TimeSpan.TicksPerMillisecond);
    }
    internal async Task<bool> CalibrateAsync(NativeObservedDestination observed,CancellationToken stop,Func<bool>? evidenceCurrent=null)
    {
        if(string.IsNullOrWhiteSpace(observed.Application))throw new InvalidDataException("Automatic direct calibration requires an identified application.");
        var rule=new NativeDomainRule(observed.Domain,observed.Application,"tcp","direct",observed.Port,[observed.Address],DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds());Validate([rule]);
        await gate.WaitAsync(stop);
        try
        {
            if(automaticState.ResetRequired)await ResetAutomaticSafelyAsync(stop);
            int pid=core.ProcessId;if(pid!=scopedPid&&scopes.Count>0)throw new IOException("Native calibration generation changed.");
            if(!core.SupportsDirectScopes||!core.SupportsAutomaticProof)throw new IOException("Native calibration requires automatic proof and safe scope retirement.");
            if(scopedPid==0)scopedPid=pid;
            core.TraceCalibration("started",observed);
            await CollectScopes(stop);
            var scope=new NativeDirectPermission(coreImage,observed.Address,observed.Port,"tcp",physicalIndex);
            await EnableScope(scope,pid,stop);
            bool usable=false,stale=false;
            try{
                bool reached=await guard.VerifyAsync(stop)&&await core.ProbeDirectAsync(observed,pid,stop);
                if(reached){stop.ThrowIfCancellationRequested();stale=core.ProcessId!=pid||core.State!="Active"||(evidenceCurrent!=null&&!evidenceCurrent());usable=!stale;}
                core.TraceCalibration(stale?"probe-stale":usable?"probe-passed":"probe-failed",observed);
            }
            finally{if(!usable){using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await CollectScopes(cleanup.Token);}}
            // A stale result says nothing about the old rule. Network/core
            // retirement owns its removal; do not mutate it from this probe.
            if(stale)return false;
            if(!usable)
            {
                try{await RemoveAutomaticAsync(observed,stop,evidenceCurrent);}
                catch(NativeStaleEvidenceException){automaticState.RequireReset();using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(10));await ResetAutomaticSafelyAsync(cleanup.Token);}
                return false;
            }
            var certified=WithFreshCalibrationId(rule,usable,()=>core.ProcessId==pid&&core.State=="Active"&&(evidenceCurrent==null||evidenceCurrent()));if(certified==null){using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await CollectScopes(cleanup.Token);return false;}rule=certified;
            var candidate=StageAutomatic(automaticRules,observed,rule,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            try{await CommitPolicyAsync(candidate,()=>ApplyConfirmed(automaticEpoch,candidate.Values.ToArray(),stop,evidenceCurrent),stop,evidenceCurrent);}
            catch(NativeStaleEvidenceException){
                // The network changed during publication. Withdraw all automatic
                // routes for new flows; existing admitted flows keep their scopes.
                automaticState.RequireReset();
                using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(10));await ResetAutomaticSafelyAsync(cleanup.Token);
                core.TraceCalibration("probe-stale",observed);return false;
            }
            Volatile.Write(ref automaticCount,automaticRules.Count);core.TraceCalibration("applied",observed);return true;
        }
        finally{gate.Release();}
    }
    internal async Task<bool> CalibrateZapretAsync(NativeObservedDestination observed,
        Func<CancellationToken,Task<NativePathProbeResult>> probe,CancellationToken stop,Func<bool> evidenceCurrent)
    {
        var rule=new NativeDomainRule(observed.Domain,observed.Application,"tcp","zapret",observed.Port,[observed.Address],DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds());
        Validate([rule],allowZapret:true);
        await gate.WaitAsync(stop);
        try
        {
            if(!core.SupportsAutomaticProof)throw new IOException("Native automatic proof protocol unavailable.");
            if(automaticState.ResetRequired)await ResetAutomaticSafelyAsync(stop);
            int pid=core.ProcessId;if(pid<=0||scopedPid!=0&&scopedPid!=pid)throw new NativeStaleEvidenceException();
            if(!EvidenceStillCurrent(evidenceCurrent))return false;
            var result=await probe(stop);stop.ThrowIfCancellationRequested();
            if(!result.Reachable||result.Reason!="reachable"||core.ProcessId!=pid||!EvidenceStillCurrent(evidenceCurrent))return false;
            var certified=WithFreshCalibrationId(rule,result.Reachable&&result.Reason=="reachable",()=>core.ProcessId==pid&&EvidenceStillCurrent(evidenceCurrent));if(certified==null)return false;rule=certified;
            var candidate=StageAutomatic(automaticRules,observed,rule,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            try{await CommitPolicyAsync(candidate,()=>ApplyConfirmed(automaticEpoch,candidate.Values.ToArray(),stop,evidenceCurrent),stop,evidenceCurrent);}
            catch(NativeStaleEvidenceException)
            {
                automaticState.RequireReset();
                using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(10));await ResetAutomaticSafelyAsync(cleanup.Token);return false;
            }
            Volatile.Write(ref automaticCount,automaticRules.Count);core.TraceCalibration("zapret-applied",observed);return true;
        }
        finally{gate.Release();}
    }
#if CITA_DPI_LAB
    internal async Task<bool> CalibrateDpiForLabAsync(NativeObservedDestination observed,CancellationToken stop,Func<bool>? evidenceCurrent=null)
    {
        if(string.IsNullOrWhiteSpace(observed.Application))throw new InvalidDataException("DPI lab requires an identified application.");
        var rule=new NativeDomainRule(observed.Domain,observed.Application,"tcp","dpi",observed.Port,[observed.Address],DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds());Validate([rule]);
        await gate.WaitAsync(stop);
        try
        {
            if(automaticState.ResetRequired)await ResetAutomaticSafelyAsync(stop);
            int pid=core.ProcessId;if(pid!=scopedPid&&scopes.Count>0)throw new IOException("DPI lab generation changed.");
            if(!core.SupportsDirectScopes||!core.SupportsAutomaticProof)throw new IOException("DPI lab requires automatic proof and scope retirement.");if(scopedPid==0)scopedPid=pid;
            await CollectScopes(stop);var scope=new NativeDirectPermission(coreImage,observed.Address,observed.Port,"tcp",physicalIndex);await EnableScope(scope,pid,stop);
            bool usable=false,stale=false;
            try{bool reached=await guard.VerifyAsync(stop)&&await core.ProbeDpiForLabAsync(observed,pid,stop);if(reached){stop.ThrowIfCancellationRequested();stale=core.ProcessId!=pid||core.State!="Active"||(evidenceCurrent!=null&&!evidenceCurrent());usable=!stale;}core.TraceCalibration(usable?"dpi-probe-passed":"dpi-probe-failed",observed);}
            finally{if(!usable){using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await CollectScopes(cleanup.Token);}}
            if(stale)return false;
            if(!usable)
            {
                try{await RemoveAutomaticAsync(observed,stop,evidenceCurrent);}
                catch(NativeStaleEvidenceException){automaticState.RequireReset();using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(10));await ResetAutomaticSafelyAsync(cleanup.Token);}
                return false;
            }
            var certified=WithFreshCalibrationId(rule,usable,()=>core.ProcessId==pid&&core.State=="Active"&&(evidenceCurrent==null||evidenceCurrent()));if(certified==null){using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await CollectScopes(cleanup.Token);return false;}rule=certified;
            var candidate=StageAutomatic(automaticRules,observed,rule,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            try{await CommitPolicyAsync(candidate,()=>ApplyConfirmed(automaticEpoch,candidate.Values.ToArray(),stop,evidenceCurrent),stop,evidenceCurrent);}
            catch(NativeStaleEvidenceException){automaticState.RequireReset();using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(10));await ResetAutomaticSafelyAsync(cleanup.Token);return false;}
            Volatile.Write(ref automaticCount,automaticRules.Count);core.TraceCalibration("applied",observed);return true;
        }
        finally{gate.Release();}
    }
#endif
}
