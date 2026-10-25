using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoutePilot.Agent;

public sealed record NativeDomainRule(string Domain,string Application,string Network,string Route,ushort Port,string[] Addresses,long ExpiresUnixMilliseconds);
public sealed record NativePolicyRequest(string Epoch,NativeDomainRule[] Rules);
internal sealed record NativeObservedDestination(string Domain,string Application,string Address,ushort Port);
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
        return observations.Where(o=>!attempted.TryGetValue(o,out long due)||due<=now)
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
// Old exact permissions remain until explicit retirement; the core enforces TTL
// for new flows. The guard budget prevents unbounded retained permissions.
internal sealed class NativeRoutePolicyPublisher(NativeWfpGuard guard,NativeCoreRecovery core,string coreImage,uint physicalIndex)
{
    readonly SemaphoreSlim gate=new(1);
    readonly HashSet<NativeDirectPermission> scopes=new();
    ulong revision;
    int scopedPid;
    internal ulong Revision=>revision;
    internal string Phase{get;private set;}="Idle";
    readonly Dictionary<NativeObservedDestination,NativeDomainRule> automaticRules=new();
    string automaticEpoch=Guid.NewGuid().ToString("N");
    int automaticCount;
    internal int AutomaticRuleCount=>Volatile.Read(ref automaticCount);
    internal async Task ResetForNetworkAsync(CancellationToken stop)
    {
        await gate.WaitAsync(stop);
        try{automaticRules.Clear();Volatile.Write(ref automaticCount,0);automaticEpoch=Guid.NewGuid().ToString("N");await ApplyConfirmed(automaticEpoch,[],stop);}
        finally{gate.Release();}
    }
    internal async Task ResetForCoreAsync(int expectedPid,CancellationToken stop)
    {
        await gate.WaitAsync(stop);
        try{
            if(expectedPid<=0||core.ProcessId!=expectedPid||expectedPid==scopedPid)throw new IOException("Native core retirement identity mismatch.");
            // The previous process has been retired by NativeCoreRecovery, so
            // none of its admitted Direct flows can still need these scopes.
            foreach(var scope in scopes.ToArray()){await guard.SetDirectAsync(scope,false,stop);scopes.Remove(scope);}
            automaticRules.Clear();Volatile.Write(ref automaticCount,0);revision=0;scopedPid=expectedPid;
        }finally{gate.Release();}
    }
    internal static void Validate(IReadOnlyCollection<NativeDomainRule> rules)
    {
        if(rules.Count>128)throw new InvalidDataException("Native route budget exceeded.");var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach(var rule in rules)
        {
            string domain=rule.Domain.TrimEnd('.').ToLowerInvariant();
            if(domain.Length is <1 or >253||IPAddress.TryParse(domain,out _)||domain.Split('.').Any(label=>!Regex.IsMatch(label,@"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z"))||rule.Network is not ("tcp" or "udp")||rule.Port==0||rule.ExpiresUnixMilliseconds<=now||rule.ExpiresUnixMilliseconds>now+600000)throw new InvalidDataException("Native route scope refused.");
            if(rule.Application.Length>512||rule.Application.Any(c=>c<32||c==127)||rule.Application.Length>0&&!Regex.IsMatch(rule.Application,@"\A[a-zA-Z]:\\"))throw new InvalidDataException("Native application scope refused.");
            if(!keys.Add(domain+"\0"+rule.Application+"\0"+rule.Network+"\0"+rule.Port))throw new InvalidDataException("Duplicate native route.");
            if(rule.Route is "direct" or "dpi")
            {
                if(rule.Addresses.Length is <1 or >4||rule.Addresses.Distinct().Count()!=rule.Addresses.Length)throw new InvalidDataException("Native DNS scope refused.");
                foreach(string text in rule.Addresses){if(!IPAddress.TryParse(text,out var ip)||ip.AddressFamily!=AddressFamily.InterNetwork||ip.ToString()!=text||!Public(ip))throw new InvalidDataException("Native public IPv4 scope required.");}
            }
            else if(rule.Route is not ("vpn" or "blocked")||rule.Addresses.Length!=0)throw new InvalidDataException("Native route kind refused.");
        }
    }
    static bool Public(IPAddress ip){var b=ip.GetAddressBytes();return b[0]!=0&&b[0]!=10&&b[0]!=127&&b[0]<224&&!(b[0]==169&&b[1]==254)&&!(b[0]==172&&b[1]>=16&&b[1]<=31)&&!(b[0]==192&&b[1]==168)&&!(b[0]==100&&b[1]>=64&&b[1]<=127);}
    internal async Task ApplyAsync(string epoch,IReadOnlyCollection<NativeDomainRule> rules,CancellationToken stop)
    {
        Validate(rules);if(epoch.Length!=32||!epoch.All(Uri.IsHexDigit))throw new InvalidDataException("Native network epoch missing.");
        await gate.WaitAsync(stop);
        try
        {
            automaticRules.Clear();Volatile.Write(ref automaticCount,0);
            await ApplyConfirmed(epoch,rules,stop);
        }
        catch{Phase="ApplyFailed";throw;}
        finally{gate.Release();}
    }
    async Task ApplyConfirmed(string epoch,IReadOnlyCollection<NativeDomainRule> rules,CancellationToken stop)
    {
            if(!core.SupportsPolicy)throw new IOException("Native core does not support acknowledged policies.");
            string document=JsonSerializer.Serialize(new{Epoch=epoch,Rules=rules});if(System.Text.Encoding.UTF8.GetByteCount(document)>65536)throw new InvalidDataException("Native policy document budget exceeded.");
            Phase="GrantingScopes";int pid=core.ProcessId;
            if(pid!=scopedPid){if(guard.DirectPermits!=0)throw new IOException("Native core changed without scope retirement.");scopes.Clear();scopedPid=pid;}
            foreach(var rule in rules.Where(r=>r.Route is "direct" or "dpi"))foreach(string ip in rule.Addresses)
            {
                var scope=new NativeDirectPermission(coreImage,ip,rule.Port,rule.Network,physicalIndex);
                if(scopes.Contains(scope))continue;
                await guard.SetDirectAsync(scope,true,stop);scopes.Add(scope);
            }
            if(!await guard.VerifyAsync(stop))throw new IOException("Direct scopes not independently confirmed.");
            Phase="Publishing";ulong next=checked(revision+1);
            await core.PublishPolicyAsync(next,document,guard.DirectDigest,pid,stop);revision=next;Phase="Applied";
    }
    internal async Task<bool> CalibrateAsync(NativeObservedDestination observed,CancellationToken stop)
    {
        var rule=new NativeDomainRule(observed.Domain,observed.Application,"tcp","direct",observed.Port,[observed.Address],DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds());Validate([rule]);
        await gate.WaitAsync(stop);
        try
        {
            int pid=core.ProcessId;if(pid!=scopedPid&&scopes.Count>0)throw new IOException("Native calibration generation changed.");
            var scope=new NativeDirectPermission(coreImage,observed.Address,observed.Port,"tcp",physicalIndex);bool added=!scopes.Contains(scope);
            if(added){await guard.SetDirectAsync(scope,true,stop);scopes.Add(scope);scopedPid=pid;}
            bool usable=false;
            try{if(await guard.VerifyAsync(stop))usable=await core.ProbeDirectAsync(observed,pid,stop);}
            finally{if(!usable&&added){using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await guard.SetDirectAsync(scope,false,cleanup.Token);scopes.Remove(scope);}}
            if(!usable){if(automaticRules.Remove(observed)){await ApplyConfirmed(automaticEpoch,automaticRules.Values.Where(r=>r.ExpiresUnixMilliseconds>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToArray(),stop);Volatile.Write(ref automaticCount,automaticRules.Count);}return false;}
            long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();foreach(var key in automaticRules.Where(x=>x.Value.ExpiresUnixMilliseconds<=now).Select(x=>x.Key).ToArray())automaticRules.Remove(key);
            // One exact domain/app tuple may have several CDN addresses. Keep
            // the latest verified address; an unverified address stays on VPN.
            foreach(var key in automaticRules.Keys.Where(x=>x.Domain==observed.Domain&&x.Application==observed.Application).ToArray())automaticRules.Remove(key);
            // Observation rotation can introduce a 129th still-live decision.
            // Retire only the oldest rule for NEW flows; keep its exact guard
            // permission for already admitted connections until safe retirement.
            if(automaticRules.Count>=128){var oldest=automaticRules.OrderBy(x=>x.Value.ExpiresUnixMilliseconds).ThenBy(x=>x.Key.Domain,StringComparer.Ordinal).First().Key;automaticRules.Remove(oldest);}
            automaticRules[observed]=rule;await ApplyConfirmed(automaticEpoch,automaticRules.Values.ToArray(),stop);Volatile.Write(ref automaticCount,automaticRules.Count);return true;
        }
        finally{gate.Release();}
    }
}
