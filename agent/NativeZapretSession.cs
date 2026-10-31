using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace RoutePilot.Agent;

// Session-owned consumer of the protected module and acknowledged policy gate.
// It never takes an executable, filter, URL or credentials from a user request.
internal sealed class NativeZapretSession(NativeZapretBundle bundle,NativeWfpGuard guard,
    NativeCoreRecovery core,NativeZapretPolicyGate policy,string configPath,
    NativeEndpointPermission? endpoint,Func<bool> sessionCurrent,CancellationToken sessionStop)
{
    readonly SemaphoreSlim gate=new(1,1);
    long version;
    int selectedPid;
    string selectedStamp="",epoch="";
    string state="Preparing";
    internal string State=>Volatile.Read(ref state);
    internal NativePathProbeResult? LastProbe{get;private set;}
    internal sealed record FailureInfo(string Stage,string Reason);
    // ProbeAsync serializes attempts with gate. Starting a new attempt clears
    // the preceding result only after admission, never while another owns it.
    readonly FailureTracker failures=new();
    internal FailureInfo? Failure=>failures.Current;
    internal sealed class FailureTracker
    {
        readonly object sync=new();
        object? currentAttempt;
        FailureInfo? current;
        internal FailureInfo? Current{get{lock(sync)return current;}}
        internal object Begin(){lock(sync){currentAttempt=new object();current=null;return currentAttempt;}}
        internal void Finish(object attempt,FailureInfo? result){lock(sync)if(ReferenceEquals(attempt,currentAttempt))current=result;}
    }
    internal static string FailureReason(Exception error)=>error switch{NativeStaleEvidenceException=>"stale",OperationCanceledException=>"cancelled",IOException=>"io",_=>"other"};
    internal static bool EligibleFailure(NativePathProbeResult? result)=>result is {Reachable:false,Reason:
        "dial-failed" or "dial-timeout" or "tls-failed" or "tls-timeout" or
        "http-write-failed" or "http-write-timeout" or "http-header-failed" or
        "http-header-timeout" or "http-body-failed" or "http-body-timeout"};
    sealed record Uplink(string Id,uint Index,string Address,string Stamp)
    {
        internal static Uplink Read(string name)
        {
            var nic=NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.Name==name&&n.OperationalStatus==OperationalStatus.Up);
            var properties=nic.GetIPProperties();
            var addresses=properties.UnicastAddresses.Select(a=>a.Address).Where(a=>a.AddressFamily==AddressFamily.InterNetwork&&
                !IPAddress.IsLoopback(a)&&!a.Equals(IPAddress.Any)&&!(a.GetAddressBytes()[0]==169&&a.GetAddressBytes()[1]==254)).ToArray();
            if(addresses.Length!=1)throw new IOException("Zapret requires one unambiguous physical IPv4 source.");
            uint index=checked((uint)properties.GetIPv4Properties().Index);
            if(index==0||nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)throw new IOException("Zapret physical interface refused.");
            string stamp=string.Join(";",properties.UnicastAddresses.Select(a=>"a:"+a.Address)
                .Concat(properties.GatewayAddresses.Select(a=>"g:"+a.Address)).Concat(properties.DnsAddresses.Select(a=>"d:"+a))
                .Append("i:"+index).Append("id:"+nic.Id).OrderBy(s=>s,StringComparer.Ordinal));
            return new(nic.Id,index,addresses[0].ToString(),stamp);
        }
    }
    internal void Invalidate()
    {Interlocked.Increment(ref version);policy.Invalidate();Volatile.Write(ref state,"Stale");}
    internal static async Task ConfirmCachedAsync(Func<bool> current,Func<CancellationToken,Task<bool>> verify,CancellationToken stop)
    {
        if(!current())throw new NativeStaleEvidenceException();
        if(!await verify(stop))throw new IOException("Zapret guard is no longer confirmed.");
        stop.ThrowIfCancellationRequested();if(!current())throw new NativeStaleEvidenceException();
    }
    async Task<(Uplink Uplink,Func<bool> Current,string Epoch)> PrepareAsync(CancellationToken stop)
    {
        using var document=JsonDocument.Parse(await File.ReadAllTextAsync(configPath,stop));
        var outbound=document.RootElement.GetProperty("outbounds").EnumerateArray().Single(x=>x.GetProperty("tag").GetString()=="native-direct");
        if(outbound.GetProperty("type").GetString()!="direct")throw new IOException("Zapret physical outbound is not pinned.");
        string name=outbound.GetProperty("bind_interface").GetString()??throw new IOException("Zapret physical interface missing.");
        var uplink=Uplink.Read(name);int pid=core.ProcessId;
        if(pid<=0||core.State!="Active"||!guard.SupportsDynamicSocket)throw new IOException("Zapret guarded core protocol unavailable.");
        bool NetworkCurrent(){try{return Uplink.Read(name)==uplink;}catch{return false;}}
        if(selectedPid==pid&&selectedStamp==uplink.Stamp&&State=="Available"&&core.IsOwnedConnectorReady(pid,epoch)&&NetworkCurrent())
        {
            long selected=Interlocked.Read(ref version);string selectedEpoch=epoch;
            bool CachedCurrent()=>Interlocked.Read(ref version)==selected&&epoch==selectedEpoch&&State=="Available"&&
                !sessionStop.IsCancellationRequested&&sessionCurrent()&&core.ProcessId==pid&&
                (core.State is "Active" or "TransportDegraded")&&core.IsOwnedConnectorReady(pid,selectedEpoch)&&NetworkCurrent();
            await ConfirmCachedAsync(CachedCurrent,guard.VerifyAsync,stop);
            return(uplink,CachedCurrent,selectedEpoch);
        }
        Invalidate();long attempt=Interlocked.Read(ref version);string nextEpoch=Guid.NewGuid().ToString("N");
        bool Current()=>Interlocked.Read(ref version)==attempt&&!sessionStop.IsCancellationRequested&&sessionCurrent()&&
            core.ProcessId==pid&&(core.State is "Active" or "TransportDegraded")&&NetworkCurrent();
        if(!Current())throw new NativeStaleEvidenceException();
        var excluded=new HashSet<string>(StringComparer.Ordinal);
        if(endpoint!=null)excluded.Add(endpoint.Server);
        policy.Bind(nextEpoch,Current);
        try
        {
            await core.StartOwnedConnectorAsync(pid,nextEpoch,Current,policy.Allows,async(request,setup)=>
            {
                if(!Current()||!policy.Allows(request))throw new IOException("Zapret request is no longer authorized.");
                return await NativeZapretConnection.OpenGuardedAsync(bundle,
                    new(nextEpoch,request.Domain,request.Application,request.Address,request.Port),uplink.Index,uplink.Address,
                    excluded,Current,sessionStop,setup,guard,useDynamic:true);
            },sessionStop,stop,policy.Admission,replace:true);
            if(!Current())throw new NativeStaleEvidenceException();
            selectedPid=pid;selectedStamp=uplink.Stamp;epoch=nextEpoch;Volatile.Write(ref state,"Available");
            return(uplink,Current,nextEpoch);
        }
        catch{policy.Invalidate();Volatile.Write(ref state,"Unavailable");throw;}
    }
    internal async Task EnsureReadyAsync(CancellationToken stop)
    {
        await gate.WaitAsync(stop);
        try{await PrepareAsync(stop);}
        finally{gate.Release();}
    }
    internal async Task<NativePathProbeResult> ProbeAsync(NativeObservedDestination observed,CancellationToken stop)
    {
        await gate.WaitAsync(stop);
        object attempt=failures.Begin();
        string stage="prepare";
        try
        {
            var prepared=await PrepareAsync(stop);
            stage="authorize";
            var candidate=new NativeDomainRule(observed.Domain,observed.Application,"tcp","zapret",observed.Port,[observed.Address],DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds());
            // Validate the same exact domain/app/IP scope as the future policy;
            // this permits only our anonymous root GET, never a user payload.
            NativeZapretAuthorization.CreateCandidate(prepared.Epoch,1,[candidate],_=>prepared.Current(),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),Environment.TickCount64);
            var excluded=new HashSet<string>(StringComparer.Ordinal);if(endpoint!=null)excluded.Add(endpoint.Server);
            stage="probe";
            var result=await NativeZapretProbe.RunAsync(observed.Domain,async token=>
                await NativeZapretConnection.OpenGuardedAsync(bundle,
                    new(prepared.Epoch,observed.Domain,observed.Application,observed.Address,observed.Port),prepared.Uplink.Index,
                    prepared.Uplink.Address,excluded,prepared.Current,sessionStop,token,guard,useDynamic:true),stop);
            stage="verify";
            if(!prepared.Current()||!core.IsOwnedConnectorReady(core.ProcessId,prepared.Epoch))throw new NativeStaleEvidenceException();
            if(!await guard.VerifyAsync(stop))throw new IOException("Zapret guard changed during the probe.");
            LastProbe=result;failures.Finish(attempt,result.Reachable?null:new("probe","unreachable"));return result;
        }
        catch(Exception error){failures.Finish(attempt,new(stage,FailureReason(error)));LastProbe=new(false,"zapret-unavailable");throw;}
        finally{gate.Release();}
    }
}
