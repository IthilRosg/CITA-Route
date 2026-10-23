namespace RoutePilot.Agent;

// Passive observations are hints, never proof that an alternate path works.
// Keep discovery separate from failures so a page full of new CDN names cannot
// crowd failures out of the two-job calibration scheduler.
internal sealed class PolicyWebSignals
{
    internal const int QueueLimit=64;
    readonly Func<long> _clock;
    readonly Dictionary<PolicyKey,DateTime> _seen=new(),_discovery=new();
    readonly HashSet<PolicyKey> _priority=new();
    int _priorityStreak;
    sealed class Faults
    {
        internal readonly Dictionary<string,DateTime> Flows=new();
        internal DateTime AvoidUntil;
    }
    readonly Dictionary<(PolicyKey Key,string Route),Faults> _faults=new();
    readonly Dictionary<PolicyKey,DateTime> _quic=new();
    double _tokens=4;
    long _refill;
    internal PolicyWebSignals(Func<long>? clock=null){_clock=clock??(()=>Environment.TickCount64);_refill=_clock();}
    internal int Queued=>_discovery.Count;
    internal void Prioritize(PolicyKey key){if(_discovery.ContainsKey(key))_priority.Add(key);}
    internal void Discover(PolicyKey key,DateTime now)
    {
        if(_seen.TryGetValue(key,out var last)&&now>=last&&now-last<TimeSpan.FromMinutes(10))return;
        // Do not remember a dropped destination: its next observed use can retry.
        if(_discovery.Count>=QueueLimit)return;
        _seen[key]=now;_discovery.TryAdd(key,now);
        while(_seen.Count>512)_seen.Remove(_seen.MinBy(p=>p.Value).Key);
    }
    void Refill()
    {
        long tick=_clock();
        if(tick<_refill){_tokens=0;_refill=tick;}
        else{_tokens=Math.Min(4,_tokens+(tick-_refill)/3000d);_refill=tick;}
    }
    internal bool TakeSpecific(PolicyKey key)
    {
        Refill();if(_tokens<1||!_discovery.ContainsKey(key))return false;
        _tokens--;_discovery.Remove(key);_priority.Remove(key);return true;
    }
    internal PolicyKey? Take(Func<PolicyKey,bool> eligible,DateTime now,bool reserve=false)
    {
        Refill();
        bool ordinaryTurn=_priorityStreak>=2&&_discovery.Keys.Any(k=>!_priority.Contains(k));
        foreach(var pair in _discovery.OrderByDescending(p=>ordinaryTurn?!_priority.Contains(p.Key):_priority.Contains(p.Key)).ThenBy(p=>p.Value).ToArray())
        {
            if(now<pair.Value||now-pair.Value>=TimeSpan.FromMinutes(2)||!eligible(pair.Key)){_discovery.Remove(pair.Key);_priority.Remove(pair.Key);continue;}
            if(_tokens<(reserve?2:1))return null;
            _tokens--;_discovery.Remove(pair.Key);_priorityStreak=_priority.Remove(pair.Key)?Math.Min(2,_priorityStreak+1):0;return pair.Key;
        }
        return null;
    }
    internal bool Fault(PolicyKey key,string route,string flow,DateTime now)
    {
        if(route is not ("direct" or "dpi" or "vpn"))return false;
        var index=(key,route);
        if(!_faults.TryGetValue(index,out var faults))_faults[index]=faults=new();
        foreach(string old in faults.Flows.Where(p=>now<p.Value||now-p.Value>TimeSpan.FromSeconds(90)).Select(p=>p.Key).ToArray())faults.Flows.Remove(old);
        // API/log observations of one flow never count twice.
        if(!faults.Flows.TryAdd(flow,now))return false;
        bool wasAvoided=faults.AvoidUntil>now;
        if(faults.Flows.Count>=2)faults.AvoidUntil=now.AddMinutes(3);
        while(faults.Flows.Count>8)faults.Flows.Remove(faults.Flows.MinBy(p=>p.Value).Key);
        while(_faults.Count>256)_faults.Remove(_faults.MinBy(p=>p.Value.Flows.Values.DefaultIfEmpty(DateTime.MinValue).Max()).Key);
        return !wasAvoided&&faults.AvoidUntil>now;
    }
    internal HashSet<string> Avoided(PolicyKey key,DateTime now)=>_faults.Where(p=>p.Key.Key==key&&p.Value.AvoidUntil>now&&p.Value.AvoidUntil-now<=TimeSpan.FromMinutes(3)).Select(p=>p.Key.Route).ToHashSet();
    internal object[] Snapshot(DateTime now)=>_faults.Where(p=>p.Value.Flows.Values.Any(at=>at<=now&&now-at<TimeSpan.FromMinutes(5)))
        .OrderByDescending(p=>p.Value.Flows.Values.Max()).Take(16).Select(p=>(object)new{domain=p.Key.Key.Domain,application=Path.GetFileName(p.Key.Key.Application),route=p.Key.Route,distinctFlows=p.Value.Flows.Count,lastFaultUtc=p.Value.Flows.Values.Max(),avoidUntilUtc=p.Value.AvoidUntil,quarantined=Avoided(p.Key.Key,now).Contains(p.Key.Route)}).ToArray();
    internal static bool Browser(string application)=>new[]{"msedge.exe","chrome.exe","firefox.exe","brave.exe","vivaldi.exe","opera.exe","chromium.exe"}.Contains(Path.GetFileName(application),StringComparer.OrdinalIgnoreCase);
    internal static bool WebClient(string application)=>Browser(application)||Path.GetFileName(application).Equals("msedgewebview2.exe",StringComparison.OrdinalIgnoreCase);
    internal bool Quic(PolicyKey key,DateTime now)
    {
        // Clash snapshots do not reliably export the sniffed protocol. This
        // remembers browser UDP/443 only; the engine's positive QUIC guard is
        // authoritative before any rejection can happen.
        if(!WebClient(key.Application))return false;
        bool fresh=!_quic.ContainsKey(key);_quic[key]=now;
        while(_quic.Count>256)_quic.Remove(_quic.MinBy(p=>p.Value).Key);
        return fresh;
    }
    internal bool HasQuic(PolicyKey key,DateTime now)=>_quic.TryGetValue(key,out var at)&&now>=at&&now-at<TimeSpan.FromMinutes(5);
    internal void Clear(){_seen.Clear();_discovery.Clear();_priority.Clear();_priorityStreak=0;_faults.Clear();_quic.Clear();_tokens=4;_refill=_clock();}
}

public sealed partial class PolicyWorker
{
    readonly PolicyWebSignals _web=new();
    HashSet<PolicyKey> _webFallbacks=new();
    bool _quicCompatible;
    internal HashSet<string> AvoidedRoutes(PolicyKey key,DateTime now){var routes=_web.Avoided(key,now);if(_options.ExperimentalBrowserFeedback)routes.UnionWith(_browserFeedback.Avoided(key,now));return routes;}
    HashSet<PolicyKey> WebFallbacks(DateTime now)=>_options.Mode=="Auto"&&_quicCompatible
        ?_decisions.Values.Where(d=>PolicySafety.Live(d,_epoch,now)&&((d.Route is "vpn" or "dpi")||(_options.ExperimentalBrowserFeedback&&_browserFeedback.Confirmed(d,now)))&&!Manual(d.Key.Domain)&&_web.HasQuic(d.Key,now)).Select(d=>d.Key).ToHashSet():new();
    void PruneWeb(DateTime now)
    {
        var current=WebFallbacks(now);if(!_webFallbacks.SetEquals(current)){_webFallbacks=current;_dirty=true;}
    }
    internal void ObserveDestination(PolicyKey key,string network,string protocol,DateTime now)
    {
        if(_options.Mode!="Auto"||!PolicySafety.Valid(key)||Manual(key.Domain)||PolicySafety.ManagedApplication(key.Application))return;
        if(network!="tcp"&&!(network=="udp"&&PolicyWebSignals.WebClient(key.Application)))return;
        Touch(key,now);
        if(!_pending.ContainsKey(key)&&(!_decisions.TryGetValue(key,out var old)||!PolicySafety.Live(old,_epoch,now)))_web.Discover(key,now);
        if(network=="udp"&&_quicCompatible&&_web.Quic(key,now))_dirty=true;
    }
    internal void ObserveFault(PolicyKey key,string route,string flow,DateTime now)
    {
        if(_options.Mode!="Auto"||!PolicySafety.Valid(key)||Manual(key.Domain)||PolicySafety.ManagedApplication(key.Application))return;
        if(_web.Fault(key,route,flow,now))_nextProbe.Remove(key);
        RegisterFailure(key,"log:"+flow,_runStop);
    }
}
