namespace RoutePilot.Agent;

// Per-session counters only. No interface identifiers or addresses are retained.
internal sealed class NativeNetworkTelemetry
{
    readonly object sync=new();
    long address,availability,changed,unchanged,unreadable,coreResets,networkResets,lastEventTick,lastResetTick;
    static void Increment(ref long value){if(value<long.MaxValue)value++;}
    internal void Event(bool isAddress){lock(sync){if(isAddress)Increment(ref address);else Increment(ref availability);lastEventTick=Environment.TickCount64;}}
    internal void Stamp(bool? differs){lock(sync){if(differs==null)Increment(ref unreadable);else if(differs.Value)Increment(ref changed);else Increment(ref unchanged);}}
    internal void Reset(bool isCore){lock(sync){if(isCore)Increment(ref coreResets);else Increment(ref networkResets);lastResetTick=Environment.TickCount64;}}
    internal object Snapshot(){lock(sync)return new{address,availability,stampChanged=changed,stampUnchanged=unchanged,stampUnreadable=unreadable,coreResets,networkResets,lastEventTick,lastResetTick};}
}
