using System.Diagnostics;

namespace RoutePilot.Agent;

internal sealed class NativeZapretRetirementException(NativeZapretWorker worker,Exception cause)
    :IOException("Zapret retirement is unconfirmed; retain the socket lease and retry cleanup.",cause)
{
    internal NativeZapretWorker Worker{get;}=worker;
}

// Bounded handshake worker. Not a generic command runner: arguments are fixed
// and the WinDivert filter is one owned 5-tuple, never an entire destination.
// The core/scheduler handoff is a separate gate; do not label this production
// routing until that authenticated socket-lease handshake is connected.
internal sealed class NativeZapretWorker:IDisposable
{
    static readonly NativeAdmissionQueue slots=new(4,8);
    NativeAdmissionQueue.Lease? admission;
    internal static async Task<NativeZapretAdmission> ReserveAsync(CancellationToken stop)=>new(await slots.AcquireAsync(TimeSpan.FromMilliseconds(1500),stop));
    internal static int Queued=>slots.Waiting;
    readonly object sync=new();
    Process? process;
    NativeProcessJob? job;
    NativeZapretTrust? trustedFiles;
    NativeProcessDiagnostics? diagnostics;
    string startupText="";
    internal string StartupText{get{lock(sync)return startupText;}}
    readonly NativeZapretPlan plan;
    readonly Func<bool> leaseCurrent;
    CancellationTokenRegistration leaseRegistration,sessionRegistration;
    Timer? deadline;
    bool cancelled,slotHeld,validated;
    long startedTimestamp;
    int? naturalExitCode;
    internal int? NaturalExitCode{get{lock(sync){if(process is {} p&&p.HasExited)return p.ExitCode;return naturalExitCode;}}}
    internal string ProbeTicket{get;}=Guid.NewGuid().ToString("N");
    NativeZapretWorker(NativeZapretPlan plan,Func<bool> leaseCurrent)
    {
        this.plan=plan;this.leaseCurrent=leaseCurrent;
    }
    internal static NativeZapretWorker Start(NativeZapretBundle bundle,NativeZapretPlan plan,Func<bool> leaseCurrent,CancellationToken leaseStop,CancellationToken stop,bool packetDiagnostics=false,bool flowLifetime=false,NativeAdmissionQueue.Lease? reserved=null)
    {
        // Core handoff must cancel, then REQUIRE RetirementConfirmed before
        // releasing its bound local port. Cancellation alone is not an ACK:
        // failure to terminate leaves admission closed and the slot reserved.
        if(!leaseStop.CanBeCanceled)throw new InvalidDataException("Zapret requires socket-lease cancellation.");
        stop.ThrowIfCancellationRequested();leaseStop.ThrowIfCancellationRequested();
        var worker=new NativeZapretWorker(plan,leaseCurrent);
        try
        {
            // Register BEFORE process creation. Cancel serializes with creation
            // on the same lock, including cancellation during hashing/launch.
            worker.leaseRegistration=leaseStop.Register(worker.Cancel);
            worker.sessionRegistration=stop.Register(worker.Cancel);
            lock(worker.sync)
            {
                if(worker.cancelled)throw new OperationCanceledException("Zapret lease cancelled before launch.");
                if(!leaseCurrent())throw new IOException("Zapret socket lease expired.");
                stop.ThrowIfCancellationRequested();leaseStop.ThrowIfCancellationRequested();
                if(worker.cancelled)throw new OperationCanceledException("Zapret lease changed during validation.");
                if(reserved!=null){if(!reserved.IsHeldBy(slots))throw new IOException("Worker admission is stale.");}
                else{worker.admission=slots.TryAcquire();if(worker.admission==null)throw new IOException("Zapret handshake budget exhausted.");}
                worker.slotHeld=true;bundle.Verify();
                if(!leaseCurrent())throw new IOException("Zapret socket lease changed.");
                stop.ThrowIfCancellationRequested();leaseStop.ThrowIfCancellationRequested();
                if(worker.cancelled)throw new OperationCanceledException("Zapret lease changed before process creation.");
                worker.trustedFiles=NativeZapretTrust.Open(bundle);
                stop.ThrowIfCancellationRequested();leaseStop.ThrowIfCancellationRequested();
                worker.job=new NativeProcessJob(128UL*1024*1024);
                worker.diagnostics=new NativeProcessDiagnostics();
                worker.startedTimestamp=Stopwatch.GetTimestamp();
                var arguments=plan.Arguments(bundle.Root);if(packetDiagnostics)arguments=[..arguments,"--debug=1"];
                worker.process=worker.job.StartOwned(Path.Combine(bundle.Root,"winws2.exe"),arguments,bundle.Root,worker.diagnostics.Writer);
                worker.diagnostics.CloseParentWriter();
                if(!flowLifetime)worker.deadline=new Timer(_=>worker.Cancel(),null,TimeSpan.FromSeconds(12),Timeout.InfiniteTimeSpan);
            }
            // A waiting cancellation callback must run before exposing success.
            if(stop.IsCancellationRequested||leaseStop.IsCancellationRequested){worker.Cancel();throw new OperationCanceledException("Zapret launch cancelled.");}
            return worker;
        }
        catch(Exception error)
        {
            try{worker.Dispose();}
            catch(IOException cleanup){throw new NativeZapretRetirementException(worker,new AggregateException(error,cleanup));}
            throw;
        }
    }
    internal bool RetirementConfirmed{get{lock(sync)return cancelled&&job==null&&process==null&&trustedFiles==null&&diagnostics==null&&!slotHeld;}}
    internal static int SlotsInUse=>slots.Active;
    internal (int Pid,long StartedTimestamp) CaptureIdentity()
    {
        lock(sync)
        {
            if(cancelled||process==null||process.HasExited||!leaseCurrent())throw new IOException("Zapret worker identity is no longer current.");
            return(process.Id,startedTimestamp);
        }
    }
    internal bool IdentityCurrent(int pid,long started)
    {
        lock(sync)return !cancelled&&process!=null&&!process.HasExited&&process.Id==pid&&startedTimestamp==started&&leaseCurrent();
    }
    internal string State
    {
        get
        {
            lock(sync)
            {
                if(cancelled)return job==null&&process==null&&trustedFiles==null&&!slotHeld?"Stopped":"RetirementUnconfirmed";
                if(process==null)return "NotStarted";
                try{if(process.HasExited||!leaseCurrent())return "Failed";return validated?"Validated":"StartedUnverified";}
                catch(InvalidOperationException){return "Failed";}
            }
        }
    }
    internal bool Confirm(string ticket,NativeZapretScope scope,NativePathProbeResult proof)
    {
        lock(sync)
        {
            if(ticket!=ProbeTicket||scope!=plan.Scope||!proof.Reachable||proof.Reason!="reachable"||State!="StartedUnverified")return false;
            validated=true;return State=="Validated";
        }
    }
    void Cancel()
    {
        lock(sync)
        {
            if(process is {} exited&&exited.HasExited)naturalExitCode=exited.ExitCode;
            cancelled=true;validated=false;
            try
            {
                if(job!=null)
                {
                    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    job.TerminateAsync(timeout.Token).GetAwaiter().GetResult();
                    if(job.ReadUsage().ActiveProcesses!=0)return;
                }
                if(process!=null&&!process.WaitForExit(1000))return;
                if(diagnostics!=null){startupText=diagnostics.Complete();diagnostics=null;}
                process?.Dispose();process=null;job?.Dispose();job=null;
                trustedFiles?.Dispose();trustedFiles=null;
                if(slotHeld){slotHeld=false;admission?.Dispose();admission=null;}
            }
            catch(Exception error)when(error is IOException or System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
            {
                // Do not announce retirement or recycle this slot after an
                // unconfirmed kill. The handoff must refuse port release.
            }
        }
    }
    public void Dispose()
    {
        Cancel();
        if(!RetirementConfirmed)throw new IOException("Zapret process retirement is unconfirmed; socket release is refused.");
        // Never dispose registrations while holding sync: another cancelling
        // thread may already be inside a callback waiting for that lock.
        deadline?.Dispose();leaseRegistration.Dispose();sessionRegistration.Dispose();
    }
}
