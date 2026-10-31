namespace RoutePilot.Agent;

// Admission covers setup through ACK, not the lifetime of an established
// flow. A policy publisher first closes admission, cancels and drains setup,
// then publishes; a timed-out drain remains closed and must be retried.
// Before the drain succeeds, a connect already in progress may have emitted
// SYN or performed interception setup: this is not instantaneous revocation.
internal sealed class NativeConnectorAdmission
{
    readonly object sync=new();
    CancellationTokenSource generation=new();
    TaskCompletionSource drained=Completed();
    int pending,closing;
    bool open,drainConfirmed=true;
    static TaskCompletionSource Completed(){var t=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);t.SetResult();return t;}
    internal sealed class Lease:IDisposable
    {
        NativeConnectorAdmission? owner;
        internal CancellationToken Cancellation{get;}
        internal Lease(NativeConnectorAdmission owner,CancellationToken cancellation){this.owner=owner;Cancellation=cancellation;}
        public void Dispose()=>Interlocked.Exchange(ref owner,null)?.Release();
    }
    internal void Open()
    {
        lock(sync)
        {
            if(open||pending!=0||closing!=0||!drainConfirmed)throw new InvalidOperationException("Connector admission is not drained.");
            generation=new CancellationTokenSource();open=true;
        }
    }
    internal Lease? TryEnter()
    {
        lock(sync)
        {
            if(!open)return null;
            if(pending++==0)drained=new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new(this,generation.Token);
        }
    }
    void Release()
    {
        lock(sync){if(--pending==0)drained.TrySetResult();}
    }
    // Immediately close admission and cancel setup. Call CloseAndDrainAsync
    // before publishing a replacement; Close alone is not a drain ACK.
    internal void Close()
    {
        MarkClosed().Cancel();
    }
    // The owner may mark closure while holding its state lock, then cancel
    // this exact generation after releasing it. Never run callbacks in locks.
    internal CancellationTokenSource MarkClosed()
    {lock(sync){open=false;drainConfirmed=false;return generation;}}
    internal async Task CloseAndDrainAsync(CancellationToken stop)
    {
        CancellationTokenSource selected;Task completion;
        lock(sync){open=false;drainConfirmed=false;closing++;selected=generation;completion=drained.Task;}
        // Cancel outside the lock: user callbacks may synchronously dispose a
        // lease. Cancelling the old generation cannot cancel a newer one.
        try
        {
            selected.Cancel();await completion.WaitAsync(stop);
            lock(sync){if(ReferenceEquals(selected,generation)&&pending==0)drainConfirmed=true;}
        }
        finally{lock(sync)closing--;}
    }
}
