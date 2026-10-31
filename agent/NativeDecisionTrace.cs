namespace RoutePilot.Agent;

// Opt-in, in-memory only. This is diagnostic evidence, not a policy input.
internal sealed record NativeTraceEntry(string Domain,string Application,string Event,long UnixMilliseconds);
internal sealed class NativeDecisionTrace
{
    // Fits the 32 KiB IPC reply budget even with escaped non-ASCII names.
    const int Capacity=24;
    readonly object sync=new();
    readonly Queue<(NativeTraceEntry Entry,long Tick)> entries=new();
    readonly Func<long> clock,wall;
    bool enabled;
    internal NativeDecisionTrace(Func<long>? clock=null,Func<long>? wall=null){this.clock=clock??(()=>Environment.TickCount64);this.wall=wall??(()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());}
    internal void Enable(bool value){lock(sync){enabled=value;if(!value)entries.Clear();}}
    void Prune(long now){while(entries.TryPeek(out var item)&&(now<item.Tick||now-item.Tick>=600000))entries.Dequeue();}
    internal (bool Enabled,NativeTraceEntry[] Entries) Snapshot(){lock(sync){Prune(clock());return(enabled,entries.Select(x=>x.Entry).ToArray());}}
    internal void Record(string kind,NativeObservedDestination observation)
    {
        if(kind is not ("observed" or "started" or "probe-passed" or "probe-failed" or "applied" or "zapret-applied"))return;
        string? domain=PolicySafety.Domain(observation.Domain);
        string application=Path.GetFileName(observation.Application);
        if(domain!=observation.Domain||application.Length is <1 or >80||application.Any(char.IsControl))return;
        lock(sync)
        {
            if(!enabled)return;
            long now=clock();Prune(now);
            if(entries.Count==Capacity)entries.Dequeue();
            entries.Enqueue((new(domain,application,kind,wall()),now));
        }
    }
}
