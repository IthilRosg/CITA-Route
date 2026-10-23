namespace RoutePilot.Agent;

internal sealed record PolicyWarmHint(PolicyKey Key,DateTime UsedUtc)
{public string LastRoute{get;init;}="";public string Evidence{get;init;}="Headers";}

// Hints never authorize routing. Only a new calibration can create a decision.
public sealed partial class PolicyWorker
{
    readonly Dictionary<PolicyKey,DateTime> _warmHints=new();
    readonly Queue<PolicyKey> _warmQueue=new();
    readonly Func<long> _warmClock;
    long _warmStarted,_warmNext,_warmPersistNext;
    bool _warmDirty;
    int _warmLaunched;
    internal int WarmQueued=>_warmQueue.Count;
    internal static bool WarmHintValid(PolicyWarmHint hint,DateTime now)=>
        hint!=null&&hint.Key!=null&&PolicySafety.Valid(hint.Key)&&hint.Key.Strategy==PolicyKey.CurrentStrategy&&
        !PolicySafety.ManagedApplication(hint.Key.Application)&&hint.UsedUtc<=now&&now-hint.UsedUtc<TimeSpan.FromHours(24);
    void RememberWarm(PolicyKey key,DateTime now)
    {
        if(!_options.Warmup)return;
        _warmHints[key]=now;_warmDirty=true;
        while(_warmHints.Count>DecisionLimit)_warmHints.Remove(_warmHints.MinBy(p=>p.Value).Key);
    }
    void LoadWarmHints(PolicyCache cache,DateTime now)
    {
        if(!_options.Warmup||cache.WarmHints==null||cache.WarmHints.Length>DecisionLimit)return;
        bool Priority(PolicyWarmHint hint)
        {
            var previous=cache.Decisions.FirstOrDefault(d=>d.Key==hint.Key);
            string route=hint.LastRoute.Length>0?hint.LastRoute:previous?.Route??"";
            string evidence=hint.LastRoute.Length>0?hint.Evidence:previous?.Evidence??"Headers";
            return route is "vpn" or "dpi"&&evidence is "BodyComplete" or "BodySample";
        }
        foreach(var hint in cache.WarmHints.Where(h=>WarmHintValid(h,now)).OrderByDescending(Priority).ThenByDescending(h=>h.UsedUtc))
        {
            var key=hint.Key with{Epoch=_epoch};
            if(Manual(key.Domain))continue;
            _warmHints[key]=hint.UsedUtc;
            if(!_decisions.TryGetValue(key,out var decision)||!PolicySafety.Live(decision,_epoch,now))
                if(!_warmQueue.Contains(key))_warmQueue.Enqueue(key);
        }
    }
    internal void DrainWarmup(CancellationToken stop)
    {
        long tick=_warmClock();
        if(!_options.Warmup||_options.Mode!="Auto"||stop.IsCancellationRequested)return;
        bool persist=_warmDirty&&tick>=_warmPersistNext;
        bool launch=_warmQueue.Count>0&&tick>=_warmStarted&&tick-_warmStarted<30000&&_warmLaunched<5&&_pending.Count<1&&tick>=_warmNext;
        if(!persist&&!launch)
        {if(tick<_warmStarted||tick-_warmStarted>=30000||_warmLaunched>=5)_warmQueue.Clear();return;}
        if(!Current())return;
        if(persist)
        {
            _warmPersistNext=tick+60000;
            try{PersistCache();_warmDirty=false;}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
            {Event("История прогрева не сохранена; работающие маршруты сохранены.");}
        }
        if(tick<_warmStarted||tick-_warmStarted>=30000||_warmLaunched>=5){_warmQueue.Clear();return;}
        // One background slot, at most five calibrations (<=1920 KiB body data),
        // at least three seconds apart. Foreground work is scheduled first.
        if(_pending.Count>=1||tick<_warmNext)return;
        while(_warmQueue.TryDequeue(out var key))
        {
            var now=DateTime.UtcNow;
            if(Manual(key.Domain)||!File.Exists(key.Application)||_pending.ContainsKey(key)||
                (_decisions.TryGetValue(key,out var decision)&&PolicySafety.Live(decision,_epoch,now))||
                (_nextProbe.TryGetValue(key,out var next)&&next>now))continue;
            var linked=CancellationTokenSource.CreateLinkedTokenSource(stop);_pendingStops[key]=linked;
            _pendingGeneration[key]=_generation;_jobMetadata[key]=(now,now,"startup-warmup");
            _warmLaunched++;_warmNext=tick+3000;
            _pending[key]=_calibration?.Invoke(key,linked.Token)??Calibrate(key,linked.Token);
            break;
        }
    }
    PolicyWarmHint WarmHint(PolicyKey key,DateTime used)
    {
        _decisions.TryGetValue(key,out var decision);
        return new(key,used){LastRoute=decision?.Route??"",Evidence=decision?.Evidence??"Headers"};
    }
    void ClearWarmup(){_warmHints.Clear();_warmQueue.Clear();_warmLaunched=5;}
}
