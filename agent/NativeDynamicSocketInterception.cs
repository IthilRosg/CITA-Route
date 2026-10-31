namespace RoutePilot.Agent;

internal sealed class NativeDynamicSocketInterception(NativeDynamicSocketLease permission,NativeZapretSocketInterception worker):INativeSocketInterception
{
    public bool RetirementConfirmed=>worker.RetirementConfirmed&&permission.RetirementConfirmed;
    public async Task WaitReadyAsync(NativeBoundSocketIdentity socket,CancellationToken stop)
    {
        await permission.WaitReadyAsync(socket,stop);
        permission.DataPlaneCurrent=()=>worker.ReadyTimestamp==0||worker.Alive;
        await worker.WaitReadyAsync(socket,stop);
        if(!permission.IsCurrent)throw new IOException("Dynamic socket permission disappeared during startup.");
    }
    public async Task RetireAsync(CancellationToken stop)
    {
        await worker.RetireAsync(stop);
        if(!worker.RetirementConfirmed)throw new IOException("Worker still owns socket interception.");
        await permission.RetireAsync(stop);
    }
}
