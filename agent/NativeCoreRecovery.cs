using System.Text.Json;

namespace RoutePilot.Agent;

// Experimental controller only: callers must supply an independently enforced
// OS guard. Never remove that guard on failure, or reconstruct an AWG backend.
internal sealed class NativeCoreRecovery : IAsyncDisposable
{
    readonly Func<NativeCoreBinding> factory;
    readonly Func<CancellationToken,Task<bool>> guard;
    readonly Func<NativeCoreBinding,CancellationToken,Task>? afterStart;
    readonly Func<CancellationToken,Task>? beforeRepair;
    readonly SemaphoreSlim gate=new(1);
    readonly CancellationTokenSource lifetime=new();
    NativeCoreBinding? binding;
    NativeAwgBackend? backend;
    JsonElement spec,ready;
    Task? monitor;
    int repairs,desired;
    int leaseChecks;
    internal int ProcessId=>binding?.ProcessId??0;
    internal int Repairs=>Volatile.Read(ref repairs);
    string state="Stopped";
    internal string State{get=>Volatile.Read(ref state);private set=>Volatile.Write(ref state,value);}
    internal NativeCoreRecovery(Func<NativeCoreBinding> factory,Func<CancellationToken,Task<bool>> guard,Func<NativeCoreBinding,CancellationToken,Task>? afterStart=null,Func<CancellationToken,Task>? beforeRepair=null)
    {this.factory=factory;this.guard=guard;this.afterStart=afterStart;this.beforeRepair=beforeRepair;}
    async Task Create(CancellationToken stop)
    {
        if(!await guard(stop))throw new InvalidOperationException("Independent native guard unavailable.");
        binding=factory();await binding.StartAsync(stop);
        leaseChecks=0;
        if(afterStart!=null)await afterStart(binding,stop);
        await binding.QuarantineAsync(spec,stop);await binding.ActivateAsync(backend!,spec,ready,stop);
        if(!await guard(stop))throw new InvalidOperationException("Independent native guard disappeared.");
        stop.ThrowIfCancellationRequested();State="Active";
    }
    internal async Task StartAsync(NativeAwgBackend backend,JsonElement spec,JsonElement ready,CancellationToken stop)
    {
        await gate.WaitAsync(stop);
        try
        {
            if(monitor!=null||desired!=0)throw new InvalidOperationException("Native core controller already used.");
            NativeAwgBackend.ValidateStart(spec);this.backend=backend;this.spec=spec.Clone();this.ready=ready.Clone();Volatile.Write(ref desired,1);State="Starting";
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop,lifetime.Token);deadline.CancelAfter(TimeSpan.FromSeconds(35));
            try{await Create(deadline.Token);monitor=Monitor();}
            catch{State="Closed";Volatile.Write(ref desired,0);if(binding!=null){await binding.DisposeAsync();binding=null;}throw;}
        }
        finally{gate.Release();}
    }
    async Task Monitor()
    {
        try
        {
            while(!lifetime.IsCancellationRequested)
            {
                await Task.Delay(500,lifetime.Token);await gate.WaitAsync(lifetime.Token);
                try
                {
                    if(Volatile.Read(ref desired)==0)break;
                    // The pinned lab core deliberately has a finite command
                    // lease. End explicitly before its pipe budget, rather than
                    // treating normal lease exhaustion as a core crash.
                    if(leaseChecks>=48)
                    {
                        State="LeaseExpired";
                        try{if(binding!=null)await binding.CloseAsync(lifetime.Token);}catch{State="LeaseCloseUnconfirmed";}
                        break;
                    }
                    bool armed;
                    try{armed=await guard(lifetime.Token);}
                    catch(OperationCanceledException)when(lifetime.IsCancellationRequested){break;}
                    catch{armed=false;}
                    if(!armed)
                    {
                        // Keep the TUN/core in place if its independent guard
                        // cannot be confirmed. Killing it would remove routes.
                        State="GuardUnconfirmed";
                        try{if(binding!=null)await binding.CloseAsync(lifetime.Token);State="GuardLost";}catch{State="GuardLostUnconfirmed";}
                        break;
                    }
                    try
                    {
                        await binding!.CommandAsync("Snapshot",spec.GetProperty("Session").GetString()!,spec.GetProperty("Generation").GetUInt64(),stop:lifetime.Token);
                        leaseChecks++;
                        continue;
                    }
                    catch(OperationCanceledException)when(lifetime.IsCancellationRequested){break;}
                    catch{State="Recovering";}
                    if(binding!=null){await binding.DisposeAsync();binding=null;}
                    // Owner-lifetime budget: never reset after a successful repair.
                    if(Volatile.Read(ref desired)==0||repairs>=2){State="Exhausted";break;}
                    Interlocked.Increment(ref repairs);
                    using var repair=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);repair.CancelAfter(TimeSpan.FromSeconds(35));
                    try{if(beforeRepair!=null)await beforeRepair(repair.Token);await Create(repair.Token);}
                    catch{if(binding!=null){await binding.DisposeAsync();binding=null;}State="Closed";break;}
                }
                finally{gate.Release();}
            }
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
    }
    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref desired,0);lifetime.Cancel();
        try{if(monitor!=null)await monitor;}finally{await Release();}
    }
    async Task Release()
    {
        await gate.WaitAsync();
        try{if(binding!=null){await binding.DisposeAsync();binding=null;}backend=null;spec=default;ready=default;State="Stopped";}
        finally{gate.Release();}
    }
}
