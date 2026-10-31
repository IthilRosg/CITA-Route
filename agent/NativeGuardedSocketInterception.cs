namespace RoutePilot.Agent;

internal interface INativeConnectorPermissions
{
    Task SetAsync(NativeConnectorPermission permission,bool grant,CancellationToken stop);
    Task<bool> VerifyAsync(CancellationToken stop);
}

// Admission order: held socket -> exact WFP permit -> verified guard ->
// verified WinDivert filter -> TCP connect. Retirement reverses the kernel
// resources before the bound socket owner may release its source port.
internal sealed class NativeGuardedSocketInterception(INativeConnectorPermissions guard,
    INativeSocketInterception inner,string agentImage,string? injectorImage=null):INativeSocketInterception
{
    readonly SemaphoreSlim gate=new(1,1);
    readonly List<NativeConnectorPermission> permissions=new();
    bool started;
    int retiring,confirmed;
    public bool RetirementConfirmed=>Volatile.Read(ref confirmed)==1;
    public async Task WaitReadyAsync(NativeBoundSocketIdentity socket,CancellationToken stop)
    {
        await gate.WaitAsync(stop);
        try
        {
            if(started||Volatile.Read(ref retiring)!=0)throw new InvalidOperationException("Guarded socket admission is single-use.");
            started=true;var permission=new NativeConnectorPermission(agentImage,socket.LocalAddress,socket.LocalPort,socket.RemoteAddress,socket.PhysicalIndex);
            // A helper may have committed a filter before its ACK is lost.
            // Retain revocation ownership BEFORE attempting the grant.
            permissions.Add(permission);await guard.SetAsync(permission,true,stop);
            if(injectorImage!=null){var injector=permission with{Image=injectorImage,Role="injector"};permissions.Add(injector);await guard.SetAsync(injector,true,stop);}
            if(!await guard.VerifyAsync(stop))throw new IOException("Connector WFP scope was not independently confirmed.");
            await inner.WaitReadyAsync(socket,stop);
        }
        finally{gate.Release();}
    }
    public async Task RetireAsync(CancellationToken stop)
    {
        Interlocked.Exchange(ref retiring,1);await gate.WaitAsync(stop);
        try
        {
            if(RetirementConfirmed)return;
            await inner.RetireAsync(stop);
            if(!inner.RetirementConfirmed)throw new IOException("Connector interceptor retirement remains unconfirmed.");
            if(permissions.Count>0)
            {
                for(int i=permissions.Count-1;i>=0;i--)await guard.SetAsync(permissions[i],false,stop);
                if(!await guard.VerifyAsync(stop))throw new IOException("Connector WFP revocation remains unconfirmed.");
                permissions.Clear();
            }
            Volatile.Write(ref confirmed,1);
        }
        finally{gate.Release();}
    }
}
