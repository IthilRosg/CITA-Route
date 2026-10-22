namespace RoutePilot.Agent;

// Trusted adapters only. This is NOT a listener for messages from websites.
// Do not pass URLs, cookies, headers or payloads across this boundary.
internal sealed class PolicyBrowserFeedback
{
    internal sealed record Ticket(PolicyKey Key,string? Route,long Revision,int Generation,DateTime StartedUtc);
    internal sealed record Failure(PolicyKey Key,string? Route,string Kind,DateTime At);
    readonly Dictionary<string,Ticket> _tickets=new();
    readonly Dictionary<PolicyKey,DateTime> _quic=new();
    readonly Dictionary<PolicyKey,DateTime> _proof=new();
    readonly PolicyWebSignals _body=new();
    readonly Func<long> _clock;
    long _refill;
    double _tokens=8;
    internal PolicyBrowserFeedback(Func<long>? clock=null){_clock=clock??(()=>Environment.TickCount64);_refill=_clock();}
    internal int Pending=>_tickets.Count;
    internal static string? Classify(string error,bool cancelled=false)
    {
        if(cancelled)return null;
        if(error.StartsWith("net::",StringComparison.Ordinal))error=error[5..];
        return error switch{
            "ERR_QUIC_PROTOCOL_ERROR" or "ERR_QUIC_HANDSHAKE_FAILED"=>"quic",
            "ERR_CONNECTION_CLOSED" or "ERR_CONNECTION_RESET" or "ERR_TIMED_OUT" or
            "ERR_CONTENT_LENGTH_MISMATCH" or "ERR_INCOMPLETE_CHUNKED_ENCODING" or "ERR_HTTP2_PROTOCOL_ERROR"=>"body",
            _=>null};
    }
    internal string? Begin(PolicyKey key,string method,string resource,string? route,long revision,int generation,DateTime now)
    {
        if(!PolicySafety.Valid(key)||!PolicyWebSignals.Browser(key.Application)||method!="GET"||resource is not ("Image" or "Media" or "Fetch" or "XHR"))return null;
        foreach(string old in _tickets.Where(p=>now<p.Value.StartedUtc||now-p.Value.StartedUtc>=TimeSpan.FromSeconds(90)).Select(p=>p.Key).ToArray())_tickets.Remove(old);
        if(_tickets.Count>=512)return null;
        string id=Guid.NewGuid().ToString("N");_tickets[id]=new(key,route,revision,generation,now);return id;
    }
    internal Failure? Fail(string token,string error,bool cancelled,long revision,int generation,DateTime now)
    {
        if(!_tickets.Remove(token,out var ticket)||ticket.Generation!=generation||ticket.Revision!=revision||now<ticket.StartedUtc||now-ticket.StartedUtc>=TimeSpan.FromSeconds(90))return null;
        string? kind=Classify(error,cancelled);if(kind==null)return null;
        long tick=_clock();
        if(tick<_refill){_tokens=0;_refill=tick;}else{_tokens=Math.Min(8,_tokens+(tick-_refill)/3000d);_refill=tick;}
        if(_tokens<1)return null;_tokens--;
        if(kind=="quic")
        {
            _quic[ticket.Key]=now;_proof.Remove(ticket.Key);
            while(_quic.Count>256){var old=_quic.MinBy(p=>p.Value).Key;_quic.Remove(old);_proof.Remove(old);}
        }
        // Separate provenance: one transport failure reported both by the
        // engine and browser must not become two independent confirmations.
        else if(ticket.Route is "direct" or "dpi" or "vpn")_body.Fault(ticket.Key,ticket.Route,token,now);
        return new(ticket.Key,ticket.Route,kind,now);
    }
    internal void Finish(string? token){if(token!=null)_tickets.Remove(token);}
    internal Ticket? Find(string token)=>_tickets.GetValueOrDefault(token);
    internal HashSet<string> Avoided(PolicyKey key,DateTime now)=>_body.Avoided(key,now);
    internal bool Calibration(PolicyKey key,DateTime started,CalibrationResult result,bool accepted)
    {
        if(!_quic.TryGetValue(key,out var at))return false;
        if(started<at)return true; // a job already in flight is not fresh evidence
        // Reachable is the selector's aggregate result of TWO TLS probes, not
        // a single packet/HEAD. An adapter cannot manufacture this evidence.
        if(accepted&&result.Outcome==ProbeOutcome.Reachable&&result.Decision is {} d&&
            !result.Candidates.Any(c=>c.Outcome==ProbeOutcome.TlsAuthenticationError)&&
            result.Candidates.Any(c=>c.Route==d.Route&&c.Outcome==ProbeOutcome.Reachable))_proof[key]=d.CheckedUtc;
        return false;
    }
    internal bool Confirmed(PolicyDecision decision,DateTime now)=>
        _quic.TryGetValue(decision.Key,out var at)&&now>=at&&now-at<TimeSpan.FromMinutes(3)&&
        _proof.TryGetValue(decision.Key,out var proof)&&proof==decision.CheckedUtc;
    internal void Clear(){_tickets.Clear();_quic.Clear();_proof.Clear();_body.Clear();_tokens=8;_refill=_clock();}
}

internal sealed record BrowserFeedbackWatch(PolicyKey Key,string? PreviousRoute,int Generation,DateTime At,long Version,bool Quic);

public sealed partial class PolicyWorker
{
    readonly PolicyBrowserFeedback _browserFeedback=new();
    readonly Dictionary<PolicyKey,(string Route,long Revision)> _browserPublished=new();
    long _browserRevision;
    readonly HashSet<PolicyKey> _browserFallbackPublished=new();
    long _browserGuardTick=long.MinValue;
    bool _browserGuard;
    int _browserTransportGeneration=int.MinValue,_browserTicketGeneration;
    bool BrowserCurrent()
    {
        var session=Storage.Read<PolicySession>(Runtime("session.json"));
        if(!session.Running||session.Id!=_epoch.Split(':')[0]||File.Exists(Runtime("stop.request")))return false;
        if(session.BridgeGeneration!=_browserTransportGeneration)
        {
            _browserTransportGeneration=session.BridgeGeneration;_browserTicketGeneration++;_browserFeedback.Clear();
            if(_browserFallbackPublished.Count>0)_dirty=true;_browserFallbackPublished.Clear();_browserGuardTick=long.MinValue;
        }
        long now=Environment.TickCount64;if(_browserGuardTick==long.MinValue||now<_browserGuardTick||now-_browserGuardTick>=200){_browserGuardTick=now;_browserGuard=Current();}return _browserGuard;
    }
    internal string? BeginBrowserResource(string domain,string application,string method,string resource,DateTime now)
    {
        if(!_options.ExperimentalBrowserFeedback||_options.Mode!="Auto"||(_publisher!=null&&_publisher.Phase!="Applied")||Manual(domain)||!BrowserCurrent())return null;
        var key=new PolicyKey(_epoch,domain,application,443);
        if(!PolicySafety.Valid(key)||!PolicyWebSignals.Browser(application)||method!="GET"||resource is not ("Image" or "Media" or "Fetch" or "XHR"))return null;
        ObserveDestination(key,"tcp","tls",now);
        var published=_browserPublished.GetValueOrDefault(key);
        return _browserFeedback.Begin(key,method,resource,published.Route,published.Revision,_browserTicketGeneration,now);
    }
    internal void FinishBrowserResource(string? token)=>_browserFeedback.Finish(token);
    internal bool ObserveBrowserFailure(string? token,string error,bool cancelled,DateTime now)
    {
        if(token==null||!_options.ExperimentalBrowserFeedback||_options.Mode!="Auto"||(_publisher!=null&&_publisher.Phase!="Applied")||!BrowserCurrent())return false;
        var ticket=_browserFeedback.Find(token);
        if(ticket==null||Manual(ticket.Key.Domain)){_browserFeedback.Finish(token);return false;}
        var published=_browserPublished.GetValueOrDefault(ticket.Key);
        var failure=_browserFeedback.Fail(token,error,cancelled,published.Revision,_browserTicketGeneration,now);if(failure==null)return false;
        if(failure.Kind=="quic")_web.Quic(failure.Key,now);
        // Preserve a queued signal even if another calibration is in flight.
        _failures[failure.Key]=(1,now);_queuedAt.TryAdd(failure.Key,now);_nextProbe.Remove(failure.Key);Touch(failure.Key,now);
        while(_failures.Count>256)_failures.Remove(_failures.MinBy(p=>p.Value.At).Key);
        while(_queuedAt.Count>256)_queuedAt.Remove(_queuedAt.MinBy(p=>p.Value).Key);
        return true;
    }
    void BrowserPublication()
    {
        if(!_options.ExperimentalBrowserFeedback)return;
        var active=_decisions.Values.Where(d=>Binding(d,DateTime.UtcNow) is "direct" or "dpi" or "vpn").ToDictionary(d=>d.Key,d=>Binding(d,DateTime.UtcNow));
        foreach(var key in _browserPublished.Keys.Where(k=>!active.ContainsKey(k)).ToArray())_browserPublished.Remove(key);
        foreach(var pair in active)if(!_browserPublished.TryGetValue(pair.Key,out var old)||old.Route!=pair.Value)_browserPublished[pair.Key]=(pair.Value,++_browserRevision);
        _browserFallbackPublished.Clear();_browserFallbackPublished.UnionWith(WebFallbacks(DateTime.UtcNow));
    }
    internal BrowserFeedbackWatch? WatchBrowserFailure(string token,string error,bool cancelled,DateTime now)
    {
        var ticket=_browserFeedback.Find(token);long version=_publisher?.Version??0;
        return ticket!=null&&ObserveBrowserFailure(token,error,cancelled,now)?new(ticket.Key,ticket.Route,_browserTicketGeneration,now,version,PolicyBrowserFeedback.Classify(error,cancelled)=="quic"):null;
    }
    internal bool BrowserRepairReady(BrowserFeedbackWatch watch,DateTime now)=>
        _options.ExperimentalBrowserFeedback&&_options.Mode=="Auto"&&BrowserCurrent()&&_browserTicketGeneration==watch.Generation&&now>=watch.At&&now-watch.At<TimeSpan.FromSeconds(20)&&!Manual(watch.Key.Domain)&&
        _publisher?.Phase=="Applied"&&_publisher.Version>watch.Version&&_decisions.TryGetValue(watch.Key,out var decision)&&PolicySafety.Live(decision,_epoch,now)&&decision.CheckedUtc>=watch.At&&
        (watch.Quic?_browserFallbackPublished.Contains(watch.Key):watch.PreviousRoute!=null&&_browserPublished.TryGetValue(watch.Key,out var applied)&&applied.Route!=watch.PreviousRoute);
    internal async Task AttachBrowserPublisher(PolicyPublisher publisher,CancellationToken stop)
    {if(_publisher!=null)throw new InvalidOperationException("A publisher is already attached.");_publisher=publisher;await PublishAsync(stop);}
    internal async Task ApplyBrowserWork(CancellationToken stop)
    {await CompletePending();DrainFailures(stop);await CompletePending();Prune();if(_dirty)await PublishAsync(stop);}
}
