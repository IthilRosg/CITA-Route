namespace RoutePilot.Agent;

// One FIFO for foreground admission and non-waiting probes. Queue entries own
// no socket, WFP rule or process. Active leases are released by confirmed cleanup.
internal sealed class NativeAdmissionQueue(int capacity,int queueLimit)
{
    readonly object sync=new();
    readonly LinkedList<Waiter> waiting=new();
    int available=capacity>0&&queueLimit>=0?capacity:throw new ArgumentOutOfRangeException(nameof(capacity));
    sealed class Waiter
    {
        internal readonly TaskCompletionSource<Lease> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Waiter>? Node;
    }
    internal sealed class Lease(NativeAdmissionQueue owner):IDisposable
    {
        NativeAdmissionQueue? held=owner;
        internal bool IsHeldBy(NativeAdmissionQueue queue)=>ReferenceEquals(Volatile.Read(ref held),queue);
        public void Dispose()=>Interlocked.Exchange(ref held,null)?.Release();
    }
    internal int Active{get{lock(sync)return capacity-available;}}
    internal int Waiting{get{lock(sync)return waiting.Count;}}
    internal Lease? TryAcquire()
    {
        lock(sync){if(waiting.Count!=0||available==0)return null;available--;return new(this);}
    }
    internal async Task<Lease> AcquireAsync(TimeSpan timeout,CancellationToken stop)
    {
        if(timeout<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(timeout));
        stop.ThrowIfCancellationRequested();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(timeout);
        Waiter entry;
        lock(sync){
            deadline.Token.ThrowIfCancellationRequested();
            if(waiting.Count==0&&available>0){available--;return new(this);}
            if(waiting.Count>=queueLimit)throw new IOException("Zapret admission queue is full.");
            entry=new();entry.Node=waiting.AddLast(entry);
        }
        using var registration=deadline.Token.Register(()=>{
            lock(sync){if(entry.Node?.List==null)return;waiting.Remove(entry.Node);entry.Node=null;entry.Completion.TrySetCanceled(deadline.Token);}
        });
        try{
            var lease=await entry.Completion.Task;
            if(deadline.IsCancellationRequested){lease.Dispose();deadline.Token.ThrowIfCancellationRequested();}
            return lease;
        }catch(OperationCanceledException)when(!stop.IsCancellationRequested){throw new TimeoutException("Zapret admission wait budget expired.");}
    }
    void Release()
    {
        lock(sync){
            if(waiting.First is {} node){waiting.RemoveFirst();node.Value.Node=null;node.Value.Completion.SetResult(new(this));}
            else{if(available>=capacity)throw new InvalidOperationException("Admission capacity over-release.");available++;}
        }
    }
}

internal sealed class NativeZapretAdmission(NativeAdmissionQueue.Lease lease):IDisposable
{
    NativeAdmissionQueue.Lease? held=lease;
    internal bool OwnsLease=>Volatile.Read(ref held)!=null;
    internal NativeAdmissionQueue.Lease Borrow()=>Volatile.Read(ref held)??throw new IOException("Admission no longer held.");
    internal NativeAdmissionQueue.Lease Take()=>Interlocked.Exchange(ref held,null)??throw new IOException("Admission already transferred or released.");
    public void Dispose()=>Interlocked.Exchange(ref held,null)?.Dispose();
}

internal sealed class NativeAdmissionSocketInterception(INativeSocketInterception inner,NativeZapretAdmission admission):INativeSocketInterception
{
    NativeAdmissionQueue.Lease? held;
    int started;
    public bool RetirementConfirmed=>inner.RetirementConfirmed&&held==null&&!admission.OwnsLease;
    public async Task WaitReadyAsync(NativeBoundSocketIdentity socket,CancellationToken stop)
    {
        if(Interlocked.Exchange(ref started,1)!=0)throw new IOException("Admission startup is single-use.");
        // ConnectAsync has registered this owner before calling WaitReady.
        // Before that point the caller must retain/dispose the reservation.
        held=admission.Take();await inner.WaitReadyAsync(socket,stop);
    }
    public async Task RetireAsync(CancellationToken stop)
    {
        await inner.RetireAsync(stop);
        if(!inner.RetirementConfirmed)throw new IOException("Admission remains quarantined with unretired interception.");
        Interlocked.Exchange(ref held,null)?.Dispose();
        admission.Dispose();
    }
}
