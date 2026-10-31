using System.Net;

namespace RoutePilot.Agent;

internal sealed class NativeConnectorRetirementException(Exception cause):IOException("Connector socket retirement is unconfirmed.",cause);
internal sealed class NativeZapretConnection:INativeConnectorConnection
{
    readonly NativeBoundTcpLease lease;
    internal NativeZapretSocketInterception Owner{get;}
    internal NativeDynamicSocketLease? Permission{get;}
    internal long ConnectTimestamp=>lease.ConnectTimestamp;
    public Stream Stream{get;}
    internal NativeZapretConnection(NativeBoundTcpLease lease,NativeZapretSocketInterception owner,Stream stream,NativeDynamicSocketLease? permission=null){this.lease=lease;Owner=owner;Stream=stream;Permission=permission;}
    internal static async Task<NativeZapretConnection> OpenAsync(NativeZapretBundle bundle,NativeZapretTarget target,
        uint physicalIndex,string localAddress,IReadOnlySet<string> excluded,Func<bool> current,CancellationToken sessionStop,CancellationToken setupStop)
        =>await OpenCoreAsync(bundle,target,physicalIndex,localAddress,excluded,current,sessionStop,setupStop,null,NativeZapretCapture.WholeConnection,false,false,false);
    internal static async Task<NativeZapretConnection> OpenGuardedAsync(NativeZapretBundle bundle,NativeZapretTarget target,
        uint physicalIndex,string localAddress,IReadOnlySet<string> excluded,Func<bool> current,CancellationToken sessionStop,CancellationToken setupStop,NativeWfpGuard guard,NativeZapretCapture capture=NativeZapretCapture.WholeConnection,bool packetDiagnostics=false,bool allowInjector=false,bool useDynamic=false)
        =>await OpenCoreAsync(bundle,target,physicalIndex,localAddress,excluded,current,sessionStop,setupStop,guard,capture,packetDiagnostics,allowInjector,useDynamic);
    static async Task<NativeZapretConnection> OpenCoreAsync(NativeZapretBundle bundle,NativeZapretTarget target,
        uint physicalIndex,string localAddress,IReadOnlySet<string> excluded,Func<bool> current,CancellationToken sessionStop,CancellationToken setupStop,NativeWfpGuard? guard,NativeZapretCapture capture,bool packetDiagnostics,bool allowInjector,bool useDynamic)
    {
        if(allowInjector&&guard is {SupportsInjector:false})throw new IOException("Exact injector WFP protocol unavailable before socket admission.");
        if(useDynamic&&guard is not {SupportsDynamicSocket:true})throw new IOException("Dynamic socket protocol unavailable before admission.");
        using var admissionStop=CancellationTokenSource.CreateLinkedTokenSource(setupStop,sessionStop);
        using var admission=useDynamic?await NativeZapretWorker.ReserveAsync(admissionStop.Token):null;
        admissionStop.Token.ThrowIfCancellationRequested();
        if(!current())throw new IOException("Socket session changed while waiting for admission.");
        var lease=NativeBoundTcpLease.Bind(IPAddress.Parse(localAddress),physicalIndex,target.Epoch);
        NativeZapretSocketInterception? owner=null;
        try
        {
            var permission=useDynamic?guard!.CreateDynamicSocketLease(current,sessionStop):null;
            if(permission!=null)lease.FailWhen(permission.Lost);
            owner=new NativeZapretSocketInterception(bundle,target,physicalIndex,localAddress,excluded,()=>current()&&(permission==null||permission.IsCurrent),sessionStop,capture,packetDiagnostics,useDynamic,admission?.Borrow());
            INativeSocketInterception inner=permission==null?owner:new NativeDynamicSocketInterception(permission,owner);
            INativeSocketInterception interception=guard==null||useDynamic?inner:new NativeGuardedSocketInterception(guard,inner,Environment.ProcessPath!,allowInjector?Path.Combine(bundle.Root,"winws2.exe"):null);
            if(admission!=null)interception=new NativeAdmissionSocketInterception(interception,admission);
            var stream=await lease.ConnectAsync(new IPEndPoint(IPAddress.Parse(target.Address),target.Port),interception,setupStop);
            return new(lease,owner,stream,permission);
        }
        catch(Exception error)
        {
            error.Data["NativeWinDivertReady"]=owner?.ReadyTimestamp>0;
            error.Data["NativeWinDivertStarted"]=owner?.WorkerPid>0;
            error.Data["NativeWorkerNaturalExit"]=owner?.WorkerNaturalExitCode;
            error.Data["NativeWorkerOutput"]=owner?.WorkerOutput;
            try{await lease.DisposeAsync();}
            catch(Exception cleanup){throw new NativeConnectorRetirementException(new AggregateException(error,cleanup));}
            throw;
        }
    }
    public void CompleteWrites()=>lease.CompleteWrites();
    public async ValueTask DisposeAsync()
    {
        try{await lease.DisposeAsync();}
        catch(Exception error){throw new NativeConnectorRetirementException(error);}
    }
}
