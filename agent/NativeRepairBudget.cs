namespace RoutePilot.Agent;

// Counts attempts, not successes. A successful probe cannot reset a flapping
// transport's allowance. Monotonic time prevents wall-clock changes refilling it.
internal sealed class NativeRepairBudget(TimeProvider? clock=null)
{
    readonly TimeProvider time=clock??TimeProvider.System;
    readonly Queue<long> attempts=new();
    long? last;
    internal bool TryAcquire()
    {
        lock(attempts)
        {
            long now=time.GetTimestamp();
            if(last is {} previous&&now<previous)return false;
            last=now;
            while(attempts.TryPeek(out long oldest)&&time.GetElapsedTime(oldest,now)>=TimeSpan.FromHours(1))attempts.Dequeue();
            if(attempts.Count>=6||attempts.Count(t=>time.GetElapsedTime(t,now)<TimeSpan.FromMinutes(5))>=2)return false;
            attempts.Enqueue(now);return true;
        }
    }
}
