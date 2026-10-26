using System;
using System.IO;
using System.Threading.Tasks;

namespace RoutePilot;

internal static class MachineOperationLeaseTests
{
    internal static async Task Run()
    {
        string name=@"Local\CITA.Lease.Test."+Guid.NewGuid().ToString("N");
        using(var first=MachineOperationLease.Acquire(name))
        {
            await Task.Delay(5).ConfigureAwait(false);
            bool blocked=false;
            try{using var second=MachineOperationLease.Acquire(name);}catch(IOException){blocked=true;}
            if(!blocked)throw new Exception("Another async owner acquired the machine operation lease.");
        }
        using var restored=MachineOperationLease.Acquire(name);
    }
}
