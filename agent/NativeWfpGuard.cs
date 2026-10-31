using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoutePilot.Agent;

// Internal controller boundary; image and policy must come from protected service
// storage. UI callers never supply executable paths or raw guard operations.
internal sealed record NativeEndpointPermission(string Image,string Server,ushort Port);
internal sealed record NativeDirectPermission(string Image,string Server,ushort Port,string Protocol,uint InterfaceIndex);
internal sealed record NativeConnectorPermission(string Image,string LocalAddress,ushort LocalPort,string Server,uint InterfaceIndex,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]string? Role=null);
internal sealed class NativeWfpGuard(string image,string run,JsonElement policy,Action<Process>? started=null,NativeEndpointPermission? endpoint=null):INativeConnectorPermissions
{
    readonly SemaphoreSlim gate=new(1);
    internal NativeDynamicSocketLease CreateDynamicSocketLease(Func<bool> current,CancellationToken sessionStop)=>new(this,Path.GetFullPath(image),run,current,sessionStop);
    internal string Digest{get;private set;}="";
    internal string EndpointDigest{get;private set;}="";
    internal string[] Prefixes{get;private set;}=[];
    internal ulong Luid{get;private set;}
    internal int Bindings{get;private set;}
    internal int DirectPermits{get;private set;}
    internal string DirectDigest{get;private set;}="";
    internal bool SupportsConnector{get;private set;}
    internal bool SupportsDynamicSocket{get;private set;}
    internal bool SupportsInjector{get;private set;}
    internal int ConnectorPermits{get;private set;}
    internal string ConnectorDigest{get;private set;}="";
    internal string? ExpectedTunnelName{get;set;}
    internal string? LastFailureStage{get;private set;}
    internal string? LastFailureReason{get;private set;}
    async Task<JsonElement> Request(string action,CancellationToken stop,uint index=0,NativeDirectPermission? direct=null,Action<JsonElement>? confirmed=null,NativeConnectorPermission? connector=null,ushort evidencePort=0)
    {
        await gate.WaitAsync(stop);
        try
        {
            using var budget=CancellationTokenSource.CreateLinkedTokenSource(stop);budget.CancelAfter(TimeSpan.FromSeconds(15));
            var info=new ProcessStartInfo(Path.GetFullPath(image)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            using var p=Process.Start(info)??throw new IOException("Guard helper unavailable.");started?.Invoke(p);_=p.StandardError.BaseStream.CopyToAsync(Stream.Null);
            try
            {
                object request=action is "Arm" or "Plan"?new{Action=action,Run=run,Policy=policy,Endpoint=endpoint}:connector!=null?new{Action=action,Run=run,ExpectedPolicy=Digest,ExpectedEndpoint=EndpointDigest,Connector=connector}:evidencePort>0?new{Action=action,Run=run,ExpectedPolicy=Digest,ExpectedEndpoint=EndpointDigest,EvidencePort=evidencePort}:new{Action=action,Run=run,ExpectedPolicy=Digest,ExpectedEndpoint=EndpointDigest,InterfaceIndex=index,Direct=direct};
                await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(),budget.Token);p.StandardInput.Close();
                var reply=await NativeAwgBackend.ReadReply(p.StandardOutput,budget.Token,action=="Plan"?262144:8192);await p.WaitForExitAsync(budget.Token);
                if(p.ExitCode!=0||!reply.GetProperty("success").GetBoolean()||reply.GetProperty("run").GetString()!=run){LastFailureStage=reply.TryGetProperty("stage",out var failed)?failed.GetString():"InvalidReply";LastFailureReason=reply.TryGetProperty("reason",out var reason)?reason.GetString():null;if(reply.TryGetProperty("shape",out var shape))LastFailureReason+=" "+shape.GetRawText();throw new IOException("Guard operation refused.");}
                if(action!="Plan"&&reply.GetProperty("policyDigest").GetString()!=Digest)throw new IOException("Guard policy identity mismatch.");
                if(action!="Plan"&&(reply.TryGetProperty("endpointDigest",out var ep)?ep.GetString():"")!=EndpointDigest)throw new IOException("Guard endpoint identity mismatch.");
                confirmed?.Invoke(reply);
                return reply;
            }
            finally{if(!p.HasExited){p.Kill();await p.WaitForExitAsync();}}
        }
        finally{gate.Release();}
    }
    internal async Task PlanAsync(CancellationToken stop)
    {
        var reply=await Request("Plan",stop);Digest=reply.GetProperty("policyDigest").GetString()!;EndpointDigest=reply.TryGetProperty("endpointDigest",out var ep)?ep.GetString()!:"";Prefixes=reply.GetProperty("prefixes").EnumerateArray().Select(x=>x.GetString()!).ToArray();
        SupportsConnector=reply.TryGetProperty("connectorProtocol",out var connectorProtocol)&&connectorProtocol.GetInt32()==1;
        SupportsDynamicSocket=reply.TryGetProperty("socketLeaseProtocol",out var socketProtocol)&&socketProtocol.GetInt32()==2;
        SupportsInjector=reply.TryGetProperty("injectorProtocol",out var injectorProtocol)&&injectorProtocol.GetInt32()==1;
        if(Digest.Length!=64||!Digest.All(Uri.IsHexDigit)||Prefixes.Length==0)throw new InvalidDataException("Invalid compiled guard policy.");
    }
    internal async Task ArmAsync(CancellationToken stop){if(Digest.Length!=64)throw new InvalidOperationException("Plan guard first.");await Request("Arm",stop);}
    internal async Task BindAsync(CancellationToken stop)
    {
        uint index=0;
        for(int attempt=0;attempt<100;attempt++)
        {
            try
            {
                var candidates=NetworkInterface.GetAllNetworkInterfaces().Where(n=>ExpectedTunnelName!=null?n.Name==ExpectedTunnelName:n.Name is "Cita-NativeLab" or "Cita-Route" or "Cita-Native").ToArray();
                if(candidates.Length>1)throw new IOException("Ambiguous TUN interface.");
                if(candidates.Length==1){index=checked((uint)candidates[0].GetIPProperties().GetIPv4Properties().Index);if(index!=0)break;}
            }
            catch(NetworkInformationException){}
            await Task.Delay(100,stop);
        }
        if(index==0)throw new IOException("Owned TUN interface unavailable.");
        await Request("BindTunnel",stop,index,confirmed:reply=>{ulong luid=reply.GetProperty("tunnelLuid").GetUInt64();if(luid==0||reply.GetProperty("permits").GetInt32()!=Prefixes.Count(p=>!p.Contains(':')))throw new IOException("Tunnel guard binding incomplete.");Luid=luid;Bindings++;});
    }
    internal async Task<bool> VerifyAsync(CancellationToken stop)
    {
            try{bool valid=false;await Request("Inspect",stop,confirmed:r=>{int expected=Luid==0?0:Prefixes.Count(p=>!p.Contains(':'));ulong seen=r.TryGetProperty("tunnelLuid",out var value)?value.GetUInt64():0;int direct=r.TryGetProperty("directPermits",out var count)?count.GetInt32():0;string digest=r.TryGetProperty("directDigest",out var proof)?proof.GetString()??"":"";int connectors=r.TryGetProperty("connectorPermits",out var connectorCount)?connectorCount.GetInt32():0;string connectorDigest=r.TryGetProperty("connectorDigest",out var connectorProof)?connectorProof.GetString()??"":"";valid=seen==Luid&&r.GetProperty("permits").GetInt32()==expected&&direct==DirectPermits&&digest==DirectDigest&&connectors==ConnectorPermits&&connectorDigest==ConnectorDigest&&r.GetProperty("rules").GetInt32()==Prefixes.Length+expected+DirectPermits+ConnectorPermits+(EndpointDigest.Length>0?1:0);});return valid;}
        catch(OperationCanceledException)when(stop.IsCancellationRequested){throw;}catch{return false;}
    }
    internal async Task SetDirectAsync(NativeDirectPermission permission,bool grant,CancellationToken stop)
    {
        await Request(grant?"GrantDirect":"RevokeDirect",stop,direct:permission,confirmed:reply=>{int count=reply.GetProperty("directPermits").GetInt32();string digest=reply.TryGetProperty("directDigest",out var proof)?proof.GetString()??"":"";if(count is <0 or >256||(count==0?digest.Length!=0:digest.Length!=64||!digest.All(Uri.IsHexDigit)))throw new IOException("Direct scope acknowledgement invalid.");DirectPermits=count;DirectDigest=digest;});
    }
    internal async Task SetConnectorAsync(NativeConnectorPermission permission,bool grant,CancellationToken stop)
    {
        if(!SupportsConnector)throw new IOException("Socket-scoped connector WFP protocol unavailable.");
        if(permission.Role is not (null or "agent" or "injector")||permission.Role=="injector"&&!SupportsInjector)throw new IOException("Connector injector role unavailable.");
        await Request(grant?"GrantConnector":"RevokeConnector",stop,connector:permission,confirmed:reply=>{
            int count=reply.GetProperty("connectorPermits").GetInt32();string digest=reply.TryGetProperty("connectorDigest",out var value)?value.GetString()??"":"";
            if(count is <0 or >128||(count==0?digest.Length!=0:digest.Length!=64||!digest.All(Uri.IsHexDigit)))throw new IOException("Connector scope acknowledgement invalid.");
            ConnectorPermits=count;ConnectorDigest=digest;
        });
    }
    Task INativeConnectorPermissions.SetAsync(NativeConnectorPermission permission,bool grant,CancellationToken stop)=>SetConnectorAsync(permission,grant,stop);
    Task<bool> INativeConnectorPermissions.VerifyAsync(CancellationToken stop)=>VerifyAsync(stop);
    internal async Task<JsonElement> ReadConnectorDropsAsync(ushort port,CancellationToken stop)
    {
        if(!SupportsConnector||port<1024)throw new IOException("Connector drop evidence unavailable.");
        var reply=await Request("InspectDrops",stop,evidencePort:port);
        return reply.TryGetProperty("connectorDrops",out var drops)?drops.Clone():JsonSerializer.SerializeToElement(Array.Empty<object>());
    }
    internal async Task CloseAsync(CancellationToken stop){await Request("CloseTunnel",stop,confirmed:_=>{Luid=0;DirectPermits=0;DirectDigest="";ConnectorPermits=0;ConnectorDigest="";});}
    // Cleanup is deliberately separate from disposal: a service crash/stop must
    // not silently remove the persistent protection of a desired VPN session.
    internal async Task RemoveAsync(CancellationToken stop)
    {
        await gate.WaitAsync(stop);
        try
        {
            var info=new ProcessStartInfo(Path.GetFullPath(image)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            using var p=Process.Start(info)??throw new IOException("Guard cleanup unavailable.");_=p.StandardError.BaseStream.CopyToAsync(Stream.Null);
            try{await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{Action="Cleanup",Run=run}));p.StandardInput.Close();var r=await NativeAwgBackend.ReadReply(p.StandardOutput,stop);await p.WaitForExitAsync(stop);if(p.ExitCode!=0||r.GetProperty("run").GetString()!=run||!r.GetProperty("success").GetBoolean()||r.GetProperty("rules").GetInt32()!=0)throw new IOException("Guard cleanup unconfirmed.");Luid=0;DirectPermits=0;DirectDigest="";ConnectorPermits=0;ConnectorDigest="";}
            finally{if(!p.HasExited){p.Kill();await p.WaitForExitAsync();}}
        }
        finally{gate.Release();}
    }
}
