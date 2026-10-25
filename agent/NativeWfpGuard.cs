using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace RoutePilot.Agent;

// Internal controller boundary; image and policy must come from protected service
// storage. UI callers never supply executable paths or raw guard operations.
internal sealed record NativeEndpointPermission(string Image,string Server,ushort Port);
internal sealed class NativeWfpGuard(string image,string run,JsonElement policy,Action<Process>? started=null,NativeEndpointPermission? endpoint=null)
{
    readonly SemaphoreSlim gate=new(1);
    internal string Digest{get;private set;}="";
    internal string EndpointDigest{get;private set;}="";
    internal string[] Prefixes{get;private set;}=[];
    internal ulong Luid{get;private set;}
    internal int Bindings{get;private set;}
    internal string? LastFailureStage{get;private set;}
    internal string? LastFailureReason{get;private set;}
    async Task<JsonElement> Request(string action,CancellationToken stop,uint index=0)
    {
        await gate.WaitAsync(stop);
        try
        {
            using var budget=CancellationTokenSource.CreateLinkedTokenSource(stop);budget.CancelAfter(TimeSpan.FromSeconds(15));
            var info=new ProcessStartInfo(Path.GetFullPath(image)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            using var p=Process.Start(info)??throw new IOException("Guard helper unavailable.");started?.Invoke(p);_=p.StandardError.BaseStream.CopyToAsync(Stream.Null);
            try
            {
                object request=action is "Arm" or "Plan"?new{Action=action,Run=run,Policy=policy,Endpoint=endpoint}:new{Action=action,Run=run,ExpectedPolicy=Digest,ExpectedEndpoint=EndpointDigest,InterfaceIndex=index};
                await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(),budget.Token);p.StandardInput.Close();
                var reply=await NativeAwgBackend.ReadReply(p.StandardOutput,budget.Token,action=="Plan"?262144:8192);await p.WaitForExitAsync(budget.Token);
                if(p.ExitCode!=0||!reply.GetProperty("success").GetBoolean()||reply.GetProperty("run").GetString()!=run){LastFailureStage=reply.TryGetProperty("stage",out var failed)?failed.GetString():"InvalidReply";LastFailureReason=reply.TryGetProperty("reason",out var reason)?reason.GetString():null;if(reply.TryGetProperty("shape",out var shape))LastFailureReason+=" "+shape.GetRawText();throw new IOException("Guard operation refused.");}
                if(action!="Plan"&&reply.GetProperty("policyDigest").GetString()!=Digest)throw new IOException("Guard policy identity mismatch.");
                if(action!="Plan"&&(reply.TryGetProperty("endpointDigest",out var ep)?ep.GetString():"")!=EndpointDigest)throw new IOException("Guard endpoint identity mismatch.");
                return reply;
            }
            finally{if(!p.HasExited){p.Kill();await p.WaitForExitAsync();}}
        }
        finally{gate.Release();}
    }
    internal async Task PlanAsync(CancellationToken stop)
    {
        var reply=await Request("Plan",stop);Digest=reply.GetProperty("policyDigest").GetString()!;EndpointDigest=reply.TryGetProperty("endpointDigest",out var ep)?ep.GetString()!:"";Prefixes=reply.GetProperty("prefixes").EnumerateArray().Select(x=>x.GetString()!).ToArray();
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
                var candidates=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.Name is "Cita-NativeLab" or "Cita-Route").ToArray();
                if(candidates.Length>1)throw new IOException("Ambiguous TUN interface.");
                if(candidates.Length==1){index=checked((uint)candidates[0].GetIPProperties().GetIPv4Properties().Index);if(index!=0)break;}
            }
            catch(NetworkInformationException){}
            await Task.Delay(100,stop);
        }
        if(index==0)throw new IOException("Owned TUN interface unavailable.");
        var reply=await Request("BindTunnel",stop,index);ulong luid=reply.GetProperty("tunnelLuid").GetUInt64();
        if(luid==0||reply.GetProperty("permits").GetInt32()!=Prefixes.Count(p=>!p.Contains(':')))throw new IOException("Tunnel guard binding incomplete.");
        Luid=luid;Bindings++;
    }
    internal async Task<bool> VerifyAsync(CancellationToken stop)
    {
        try{var r=await Request("Inspect",stop);int expected=Luid==0?0:Prefixes.Count(p=>!p.Contains(':'));ulong seen=r.TryGetProperty("tunnelLuid",out var value)?value.GetUInt64():0;return seen==Luid&&r.GetProperty("permits").GetInt32()==expected&&r.GetProperty("rules").GetInt32()==Prefixes.Length+expected+(EndpointDigest.Length>0?1:0);}
        catch(OperationCanceledException)when(stop.IsCancellationRequested){throw;}catch{return false;}
    }
    internal async Task CloseAsync(CancellationToken stop){await Request("CloseTunnel",stop);Luid=0;}
    // Cleanup is deliberately separate from disposal: a service crash/stop must
    // not silently remove the persistent protection of a desired VPN session.
    internal async Task RemoveAsync(CancellationToken stop)
    {
        await gate.WaitAsync(stop);
        try
        {
            var info=new ProcessStartInfo(Path.GetFullPath(image)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            using var p=Process.Start(info)??throw new IOException("Guard cleanup unavailable.");_=p.StandardError.BaseStream.CopyToAsync(Stream.Null);
            try{await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{Action="Cleanup",Run=run}));p.StandardInput.Close();var r=await NativeAwgBackend.ReadReply(p.StandardOutput,stop);await p.WaitForExitAsync(stop);if(p.ExitCode!=0||r.GetProperty("run").GetString()!=run||!r.GetProperty("success").GetBoolean()||r.GetProperty("rules").GetInt32()!=0)throw new IOException("Guard cleanup unconfirmed.");Luid=0;}
            finally{if(!p.HasExited){p.Kill();await p.WaitForExitAsync();}}
        }
        finally{gate.Release();}
    }
}
