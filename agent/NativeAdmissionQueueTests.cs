namespace RoutePilot.Agent;

internal static class NativeAdmissionQueueTests
{
    static void Check(bool value,string reason){if(!value)throw new Exception(reason);}
    internal static async Task Run()
    {
        var queue=new NativeAdmissionQueue(4,8);
        var active=Enumerable.Range(0,4).Select(_=>queue.TryAcquire()!).ToArray();
        var stops=Enumerable.Range(0,8).Select(_=>new CancellationTokenSource()).ToArray();
        var pending=stops.Select(s=>queue.AcquireAsync(TimeSpan.FromSeconds(5),s.Token)).ToArray();
        Check(queue.Active==4&&queue.Waiting==8&&queue.TryAcquire()==null,"Probes bypassed a full FIFO or capacity.");
        bool overflow=false;try{await queue.AcquireAsync(TimeSpan.FromSeconds(1),default);}catch(IOException){overflow=true;}
        Check(overflow&&queue.Waiting==8,"Ninth waiter exceeded bounded admission.");
        foreach(int index in new[]{0,3,7}){stops[index].Cancel();bool canceled=false;try{await pending[index];}catch(OperationCanceledException){canceled=true;}Check(canceled,"Cancelled FIFO waiter remained pending.");}
        Check(queue.Waiting==5,"Cancellation left holes in the bounded FIFO.");
        active[0].Dispose();
        foreach(int index in new[]{1,2,4,5,6})
        {
            var granted=await pending[index];
            Check(pending.Where((_,i)=>i>index&&i is not (3 or 7)).All(t=>!t.IsCompleted),"Admission order was not FIFO.");
            Check(queue.TryAcquire()==null,"A synchronous probe bypassed foreground admission.");
            granted.Dispose();
        }
        foreach(var lease in active)lease.Dispose();foreach(var stop in stops)stop.Dispose();
        Check(queue.Active==0&&queue.Waiting==0,"FIFO drain lost capacity or retained waiters.");
        using(var held=queue.TryAcquire()){}

        var timed=new NativeAdmissionQueue(1,1);using var occupied=timed.TryAcquire();
        bool timeout=false;try{await timed.AcquireAsync(TimeSpan.FromMilliseconds(40),default);}catch(TimeoutException){timeout=true;}
        Check(timeout&&timed.Waiting==0&&timed.Active==1,"Timeout did not remove its waiter or released an unrelated active lease.");

        for(int i=0;i<64;i++)
        {
            var racing=new NativeAdmissionQueue(1,1);var owner=racing.TryAcquire()!;
            using var cancel=new CancellationTokenSource();var waiter=racing.AcquireAsync(TimeSpan.FromSeconds(3),cancel.Token);
            await Task.WhenAll(Task.Run(owner.Dispose),Task.Run(cancel.Cancel));
            try{(await waiter).Dispose();}catch(OperationCanceledException){}
            Check(racing.Active==0&&racing.Waiting==0,"Grant/cancel race leaked or duplicated an admission lease.");
        }
        // An unconfirmed composite retirement retains admission even after the
        // worker itself has gone; a retry releases that SAME capacity exactly once.
        var quarantined=new NativeAdmissionQueue(1,0);using var reservation=new NativeZapretAdmission(quarantined.TryAcquire()!);
        var inner=new Controlled();var interception=new NativeAdmissionSocketInterception(inner,reservation);
        await interception.WaitReadyAsync(new(Guid.NewGuid(),Guid.NewGuid().ToString("N"),"192.168.1.124",52001,"1.1.1.1",443,25),default);reservation.Dispose();
        bool failed=false;try{await interception.RetireAsync(default);}catch(IOException){failed=true;}
        Check(failed&&!interception.RetirementConfirmed&&quarantined.Active==1,"Unconfirmed cleanup recycled capacity.");
        inner.Confirmed=true;await interception.RetireAsync(default);await interception.RetireAsync(default);
        Check(interception.RetirementConfirmed&&quarantined.Active==0,"Retry did not release exactly one quarantined admission.");
        using var neverStarted=new NativeZapretAdmission(quarantined.TryAcquire()!);
        var unregistered=new NativeAdmissionSocketInterception(new Controlled(),neverStarted);
        neverStarted.Dispose();Check(quarantined.Active==0,"Construction before Connect registration consumed and leaked admission.");
        Console.WriteLine("PASS: bounded 4+8 FIFO admission, no probe bypass, first/middle/last cancellation, timeout, grant/cancel races and quarantine until full retirement.");
    }
    sealed class Controlled:INativeSocketInterception
    {
        internal bool Confirmed;
        public bool RetirementConfirmed=>Confirmed;
        public Task WaitReadyAsync(NativeBoundSocketIdentity socket,CancellationToken stop)=>Task.CompletedTask;
        public Task RetireAsync(CancellationToken stop)=>Task.CompletedTask;
    }
}
