using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoutePilot.Agent;

internal interface INativeAwgBinding
{
    Task QuarantineAsync(JsonElement spec,CancellationToken stop);
    Task ActivateAsync(NativeAwgBackend candidate,JsonElement spec,JsonElement ready,CancellationToken stop);
    Task CloseAsync(CancellationToken stop);
}

// Isolated core candidate only. Its caller supplies trusted component/config
// paths. All mutable endpoint credentials stay on the inherited stdin pipe.
internal sealed class NativeCoreBinding : INativeAwgBinding,IAsyncDisposable
{
    readonly string coreImage,config,nativeImage,owner,expectedExit;
    readonly SemaphoreSlim gate=new(1);
    readonly Func<JsonElement,CancellationToken,Task>? quarantined;
    readonly Func<NativeAwgBackend,JsonElement,CancellationToken,Task>? beforeValidation;
    Process? process;
    string session="";
    ulong generation;
    bool faulted;
    internal JsonElement LastState{get;private set;}
    internal int ProcessId=>process?.Id??0;
    internal NativeCoreBinding(string coreImage,string config,string nativeImage,string owner,string expectedExit,Func<JsonElement,CancellationToken,Task>? quarantined=null,Func<NativeAwgBackend,JsonElement,CancellationToken,Task>? beforeValidation=null)
    {this.coreImage=Path.GetFullPath(coreImage);this.config=Path.GetFullPath(config);this.nativeImage=Path.GetFullPath(nativeImage);this.owner=owner;this.expectedExit=expectedExit;this.quarantined=quarantined;this.beforeValidation=beforeValidation;}
    internal async Task StartAsync(CancellationToken stop)
    {
        var info=new ProcessStartInfo(coreImage){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add("run");info.ArgumentList.Add("-c");info.ArgumentList.Add(config);
        info.Environment["CITA_NATIVE_BINDING_LAB"]="1";info.Environment["CITA_NATIVE_UDP_LIFETIME_LAB"]="1";
        process=Process.Start(info)??throw new IOException("Binding core did not start.");_=process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var ready=await NativeAwgBackend.ReadReply(process.StandardOutput,deadline.Token);
        if(ready.GetProperty("stage").GetString()!="BindingControlReady"||ready.GetProperty("owner").GetString()!=owner||!ready.GetProperty("success").GetBoolean())throw new IOException("Core control readiness mismatch.");
    }
    internal async Task<JsonElement> CommandAsync(string action,string id,ulong gen,int port=0,string token="",bool requireSuccess=true,CancellationToken stop=default)
    {
        await gate.WaitAsync(stop);
        try
        {
            stop.ThrowIfCancellationRequested();if(process==null||faulted||process.HasExited)throw new IOException("Core control unavailable.");
            // Once written, consume the acknowledgement even if the initiating
            // user operation is cancelled. This keeps the private stream aligned.
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var command=JsonSerializer.Serialize(new{Owner=owner,Action=action,Session=id,Generation=gen,Port=port,Token=token});
            bool refused=false;
            try
            {
                await process.StandardInput.WriteLineAsync(command.AsMemory(),deadline.Token);await process.StandardInput.FlushAsync(deadline.Token);
                var reply=await NativeAwgBackend.ReadReply(process.StandardOutput,deadline.Token);
                if(reply.GetProperty("stage").GetString()!=action||reply.GetProperty("owner").GetString()!=owner||reply.GetProperty("session").GetString()!=id||reply.GetProperty("generation").GetUInt64()!=gen)throw new IOException("Core reply mismatch.");
                LastState=reply;
                if(requireSuccess&&!reply.GetProperty("success").GetBoolean()){refused=true;throw new InvalidDataException("Core binding command refused.");}
                return reply;
            }
            catch(InvalidDataException)when(refused){throw;}
            catch{faulted=true;process.StandardInput.Close();throw;}
        }
        finally{gate.Release();}
    }
    public async Task QuarantineAsync(JsonElement spec,CancellationToken stop)
    {
        var id=NativeAwgBackend.ValidateStart(spec);
        var ack=await CommandAsync("Quarantine",id.Session,id.Generation,stop:stop);session=id.Session;generation=id.Generation;
        if(quarantined!=null)await quarantined(ack,stop);stop.ThrowIfCancellationRequested();
    }
    internal async Task<JsonElement> ProbeAsync(string mode,object spec,CancellationToken stop)
    {
        var info=new ProcessStartInfo(nativeImage){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add(mode);
        using var p=Process.Start(info)??throw new IOException("Candidate probe did not start.");_=p.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try{await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(spec));p.StandardInput.Close();var reply=await NativeAwgBackend.ReadReply(p.StandardOutput,stop);await p.WaitForExitAsync(stop);return reply;}
        finally{if(!p.HasExited){p.Kill();await p.WaitForExitAsync();}}
    }
    public async Task ActivateAsync(NativeAwgBackend candidate,JsonElement spec,JsonElement ready,CancellationToken stop)
    {
        var id=NativeAwgBackend.ValidateStart(spec);int port=ready.GetProperty("port").GetInt32();string token=spec.GetProperty("Token").GetString()!;
        if(beforeValidation!=null)await beforeValidation(candidate,spec,stop);
        var https=await ProbeAsync("--proxy-client",new{Port=port,Token=token,LiteralIPv4=true},stop);
        if(!https.GetProperty("success").GetBoolean()||https.GetProperty("egressHash").GetString()!=expectedExit)throw new InvalidDataException("Candidate HTTPS validation failed.");
        var match=Regex.Match(spec.GetProperty("Config").GetString()!,@"(?mi)^\s*DNS\s*=\s*([0-9.]+)");if(!match.Success)throw new InvalidDataException("Candidate DNS unavailable.");
        var udp=await ProbeAsync("--proxy-udp-client",new{Port=port,Token=token,Server=match.Groups[1].Value},stop);
        if(!udp.GetProperty("success").GetBoolean())throw new InvalidDataException("Candidate UDP validation failed.");
        stop.ThrowIfCancellationRequested();await CommandAsync("Activate",id.Session,id.Generation,port,token,stop:stop);stop.ThrowIfCancellationRequested();
    }
    public async Task CloseAsync(CancellationToken stop){if(generation!=0)await CommandAsync("Close",session,generation,stop:stop);}
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try{if(process!=null){try{process.StandardInput.Close();}catch(IOException){}if(!process.HasExited){process.Kill();await process.WaitForExitAsync();}process.Dispose();process=null;}}
        finally{gate.Release();}
    }
}
