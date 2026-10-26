using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RoutePilot;

// Mutex ownership is thread-affine. Keep its owner on a dedicated thread while
// asynchronous UI operations await child processes; a crashed owner is recoverable.
internal sealed class MachineOperationLease:IDisposable
{
    internal static string GuardPowerShell(string body)=>
        "$machineGate=New-Object Threading.Mutex($false,'Global\\CITA.Route.NetworkOperation'); $machineHeld=$false; "+
        "try{try{$machineHeld=$machineGate.WaitOne(2000)}catch [Threading.AbandonedMutexException]{$machineHeld=$true}; "+
        "if(-not $machineHeld){throw 'Another CITA operation owns network maintenance.'}; "+body+
        "}finally{if($machineHeld){$machineGate.ReleaseMutex()};$machineGate.Dispose()}";
    readonly ManualResetEventSlim _release=new(false);
    readonly Thread _owner;
    int _disposed;
    MachineOperationLease(string name,TaskCompletionSource<bool> ready)
    {
        _owner=new Thread(()=>
        {
            bool held=false;
            try
            {
                using var mutex=new Mutex(false,name);
                try{held=mutex.WaitOne(0);}catch(AbandonedMutexException){held=true;}
                if(!held){ready.SetResult(false);return;}
                ready.SetResult(true);
                try{_release.Wait();}finally{mutex.ReleaseMutex();}
            }
            catch(Exception ex){ready.TrySetException(ex);}
        }){IsBackground=true,Name="CITA operation lease"};
        _owner.Start();
    }
    internal static MachineOperationLease Acquire(string name=@"Global\CITA.Route.NetworkOperation",bool allowInstaller=false)
    {
        if(!allowInstaller&&name==@"Global\CITA.Route.NetworkOperation"&&WireSockMaintenance.InstallerRunning())throw new IOException("Установщик WireSock ещё работает. Дождись завершения.");
        var ready=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease=new MachineOperationLease(name,ready);
        try
        {
            if(!ready.Task.GetAwaiter().GetResult())throw new IOException("Другая копия CITA Route выполняет сетевую операцию или обновление. Дождись завершения.");
            return lease;
        }
        catch{lease.Dispose();throw;}
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        _release.Set();_owner.Join();_release.Dispose();
    }
}
