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
    readonly Func<JsonElement,string>? expectedEgress;
    readonly bool persistentControl;
    readonly Action<Process>? started;
    Process? process;
    string session="";
    ulong generation;
    bool faulted;
    internal JsonElement LastState{get;private set;}
    internal NativeValidationEvidence? LastValidation{get;private set;}
    internal int ProcessId{get{try{return process?.Id??0;}catch(InvalidOperationException){return 0;}}}
    internal NativeCoreBinding(string coreImage,string config,string nativeImage,string owner,string expectedExit,Func<JsonElement,CancellationToken,Task>? quarantined=null,Func<NativeAwgBackend,JsonElement,CancellationToken,Task>? beforeValidation=null,Func<JsonElement,string>? expectedEgress=null,bool persistentControl=false,Action<Process>? started=null)
    {this.coreImage=Path.GetFullPath(coreImage);this.config=Path.GetFullPath(config);this.nativeImage=Path.GetFullPath(nativeImage);this.owner=owner;this.expectedExit=expectedExit;this.quarantined=quarantined;this.beforeValidation=beforeValidation;this.expectedEgress=expectedEgress;this.persistentControl=persistentControl;this.started=started;}
    internal async Task StartAsync(CancellationToken stop)
    {
        var info=new ProcessStartInfo(coreImage){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add("run");info.ArgumentList.Add("-c");info.ArgumentList.Add(config);
        info.Environment["CITA_NATIVE_BINDING_LAB"]="1";info.Environment["CITA_NATIVE_UDP_LIFETIME_LAB"]="1";
        if(persistentControl)info.Environment["CITA_NATIVE_PERSISTENT_CONTROL"]="1";
        process=Process.Start(info)??throw new IOException("Binding core did not start.");started?.Invoke(process);_=process.StandardError.BaseStream.CopyToAsync(Stream.Null);
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
        // The expectation belongs to the trusted controller, never to imported
        // configuration. Different server profiles can legitimately have different exits.
        string expected=expectedEgress?.Invoke(spec)??expectedExit;
        if(expected.Length!=64||!expected.All(Uri.IsHexDigit))throw new InvalidDataException("Candidate exit expectation unavailable.");
        LastValidation=null;int attempts=1;
        JsonElement Snapshot()=>JsonSerializer.SerializeToElement(new{Session=id.Session,Generation=id.Generation,Action="Snapshot"});
        var owner=await candidate.SendAsync(Snapshot(),stop);
        var https=await ProbeAsync("--proxy-client",new{Port=port,Token=token,LiteralIPv4=true},stop);
        string? failure=https.TryGetProperty("failureCode",out var failureValue)?failureValue.GetString():null;
        if(NativeProbePolicy.RetryAnonymousHttps(https,owner,attempts))
        {
            var current=await candidate.SendAsync(Snapshot(),stop);
            if(current.GetProperty("state").GetString()=="RunningUnverified"&&current.GetProperty("epoch").GetInt32()==owner.GetProperty("epoch").GetInt32())
            {
                // Retry only our anonymous, idempotent acceptance GET on the
                // same candidate epoch; never replay an application request.
                await Task.Delay(200,stop);attempts++;
                https=await ProbeAsync("--proxy-client",new{Port=port,Token=token,LiteralIPv4=true},stop);
            }
        }
        bool exitMatched=https.GetProperty("success").GetBoolean()&&string.Equals(https.GetProperty("egressHash").GetString(),expected,StringComparison.OrdinalIgnoreCase);
        LastValidation=new(attempts,exitMatched,failure,false,owner.GetProperty("epoch").GetInt32(),https.TryGetProperty("egressHash",out var seenExit)?seenExit.GetString():null,https.TryGetProperty("failurePhase",out var phase)?phase.GetString():null);
        if(!exitMatched)throw new InvalidDataException("Candidate HTTPS validation failed.");
        // Configured DNS may be private inside the peer. The public-only SOCKS
        // acceptance client must never mistake that policy refusal for UDP loss.
        // Keep profile DNS for resolution; use the fixed public control route here.
        var udp=await ProbeAsync("--proxy-udp-client",new{Port=port,Token=token,Server="1.1.1.1"},stop);
        if(!udp.GetProperty("success").GetBoolean())throw new InvalidDataException("Candidate UDP validation failed.");
        LastValidation=LastValidation with {UdpVerified=true};
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
