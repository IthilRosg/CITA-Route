using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RoutePilot.Agent;

// Optional native service backend. All paths, policy and profile material come
// from one protected, digest-checked snapshot; IPC accepts no paths or keys.
internal sealed class NativeServiceRoute
{
    sealed record Settings(bool Enabled,string CatalogSha256,string CatalogName="native-catalog.json");
    sealed record Intent(string Run,ulong Generation,bool Desired,string Operation);
    NativeTrustedBundle bundle;
    readonly string statePath;
    string owner="";
    JsonElement policy;
    NativeEndpointPermission endpoint=null!;
    NativeProfileImportRequest? pendingImport;
    readonly object sync=new();
    readonly SemaphoreSlim signal=new(0,1);
    readonly NativeRepairBudget repairBudget=new();
    Intent intent;
    NativeGuardedSession? runtime;
    CancellationTokenSource? operation;
    string phase="Disconnected";
    string? failure;
    string? importStage;
    NativeServiceRoute(string root,NativeTrustedBundle bundle)
    {
        this.bundle=bundle;statePath=Path.Combine(root,"intent.json");
        (owner,policy,endpoint)=ReadBinding(bundle);
        intent=File.Exists(statePath)?JsonSerializer.Deserialize<Intent>(NativeTrustedBundle.ReadProtectedText(statePath,4096))??throw new InvalidDataException("Native intent missing."):new(Guid.NewGuid().ToString("N"),0,false,Guid.NewGuid().ToString("N"));
        if(intent.Run.Length!=32||!intent.Run.All(Uri.IsHexDigit)||intent.Operation.Length!=32||!intent.Operation.All(Uri.IsHexDigit))throw new InvalidDataException("Native intent invalid.");
        if(intent.Desired){intent=intent with{Generation=checked(intent.Generation+1),Operation=Guid.NewGuid().ToString("N")};Save(intent);phase="Preparing";}
        if(File.Exists(statePath))signal.Release();
    }
    static (string Owner,JsonElement Policy,NativeEndpointPermission Endpoint) ReadBinding(NativeTrustedBundle bundle)
    {
        if(string.IsNullOrEmpty(bundle.GuardPath)||string.IsNullOrEmpty(bundle.PolicyText)||string.IsNullOrEmpty(bundle.ProfileText)||string.IsNullOrEmpty(bundle.CoreConfigPath)||bundle.ExpectedExitHash.Length!=64||!bundle.ExpectedExitHash.All(Uri.IsHexDigit))throw new InvalidDataException("Incomplete native service snapshot.");
        using var plan=JsonDocument.Parse(bundle.PolicyText);var policy=plan.RootElement.Clone();
        using var config=JsonDocument.Parse(NativeTrustedBundle.ReadProtectedText(bundle.CoreConfigPath));
        string owner=config.RootElement.GetProperty("outbounds").EnumerateArray().Single(x=>x.GetProperty("type").GetString()=="cita_native_lab").GetProperty("owner").GetString()!;
        if(owner.Length!=32||!owner.All(Uri.IsHexDigit))throw new InvalidDataException("Native core owner missing.");
        var match=System.Text.RegularExpressions.Regex.Match(bundle.ProfileText,@"(?mi)^\s*Endpoint\s*=\s*([^\r\n#]+)");
        if(!match.Success||!System.Net.IPEndPoint.TryParse(match.Groups[1].Value.Trim(),out var peer)||peer.Address.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork||peer.Port==0)throw new InvalidDataException("Native service snapshot requires a pinned IPv4 endpoint.");
        return(owner,policy,new(bundle.OwnerPath,peer.Address.ToString(),checked((ushort)peer.Port)));
    }
    internal static NativeServiceRoute? TryLoad()
    {
        string root=Path.Combine(AppContext.BaseDirectory,"native"),file=Path.Combine(root,"service.json");if(!File.Exists(file))return null;
        var settings=JsonSerializer.Deserialize<Settings>(NativeTrustedBundle.ReadProtectedText(file,4096))??throw new InvalidDataException("Native service settings invalid.");
        if(!settings.Enabled)return null;var bundle=NativeTrustedBundle.Open(root,settings.CatalogSha256,settings.CatalogName);
        try{return new(root,bundle);}catch{bundle.Dispose();throw;}
    }
    void Save(Intent next)
    {
        string temporary=statePath+"."+Guid.NewGuid().ToString("N")+".new";
        try{File.WriteAllText(temporary,JsonSerializer.Serialize(next),new UTF8Encoding(false));File.Move(temporary,statePath,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    internal JsonElement Snapshot
    {get{lock(sync)return JsonSerializer.SerializeToElement(new{phase=phase=="Connected"&&runtime?.Core.State is {} state&&state!="Active"?state:phase,desired=intent.Desired,operation=intent.Operation,generation=intent.Generation,profileName=bundle.ProfileName,profileId=bundle.ProfileId,policyMode=policy.GetProperty("Vpn").EnumerateArray().Any(x=>x.GetString()=="0.0.0.0/0")?"FullVpn":"SelectedRoutes",errorCode=failure,importStage,runtime=runtime?.Snapshot});}}
    internal bool Busy{get{lock(sync)return intent.Desired||phase is not "Disconnected";}}
    internal void RequestImport(NativeProfileImportRequest request,string? expectedOperation)
    {
        lock(sync)
        {
            if(intent.Desired||phase!="Disconnected"||expectedOperation!=intent.Operation||pendingImport!=null)throw new InvalidOperationException("Disconnect before importing a native profile.");
            if(request.Base64.Length is <1 or >16384||request.Name.Length>160||request.RequestId.Length!=0&&(request.RequestId.Length!=32||!request.RequestId.All(Uri.IsHexDigit)||request.RequestId==expectedOperation))throw new InvalidDataException("Native import input refused.");
            var next=intent with{Generation=checked(intent.Generation+1),Operation=request.RequestId.Length==0?Guid.NewGuid().ToString("N"):request.RequestId};Save(next);intent=next;pendingImport=request;phase="Importing";failure=null;if(signal.CurrentCount==0)signal.Release();
        }
    }
    internal void Request(bool connect,string? expectedOperation)
    {
        lock(sync)
        {
            if(connect&&phase=="Importing")throw new InvalidOperationException("Native import owns the session.");
            if(!connect&&(intent.Desired||phase=="Importing")&&expectedOperation!=intent.Operation)throw new InvalidDataException("Stale native stop refused.");
            if(!connect)pendingImport=null;
            if(connect&&intent.Desired&&phase!="RecoveryFailed")return;
            var next=connect?intent with{Desired=true,Generation=checked(intent.Generation+1),Operation=Guid.NewGuid().ToString("N")}:intent with{Desired=false};
            Save(next);intent=next;phase=connect?"Preparing":"Stopping";failure=null;try{operation?.Cancel();}catch(ObjectDisposedException){}
            if(signal.CurrentCount==0)signal.Release();
        }
    }
    internal async Task Run(CancellationToken stop)
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                bool requested=await signal.WaitAsync(TimeSpan.FromSeconds(1),stop);
                if(!requested)
                {
                    lock(sync)
                    {
                        if(!intent.Desired||phase!="Connected"||runtime?.Core.State is not ("TransportUnavailable" or "TransportExhausted" or "Exhausted"))continue;
                        if(!repairBudget.TryAcquire()){phase="RecoveryFailed";failure="RepairBudgetExhausted";continue;}
                        var replacement=intent with{Generation=checked(intent.Generation+1),Operation=Guid.NewGuid().ToString("N")};Save(replacement);intent=replacement;phase="Recovering";
                    }
                }
                Intent selected;CancellationTokenSource attempt;NativeProfileImportRequest? import;
                lock(sync){selected=intent;import=pendingImport;pendingImport=null;operation=attempt=CancellationTokenSource.CreateLinkedTokenSource(stop);}
                try
                {
                    if(import!=null)
                    {
                        using var prepared=await NativeProfileImport.Prepare(Path.GetDirectoryName(statePath)!,bundle,policy,import,attempt.Token,stage=>{lock(sync)importStage=stage;});
                        var binding=ReadBinding(prepared.Bundle);NativeTrustedBundle previous;
                        lock(sync){attempt.Token.ThrowIfCancellationRequested();if(intent.Operation!=selected.Operation)throw new OperationCanceledException(attempt.Token);prepared.Commit(Path.GetDirectoryName(statePath)!,attempt.Token);previous=bundle;bundle=prepared.Bundle;(owner,policy,endpoint)=binding;phase="Disconnected";}
                        previous.Dispose();
                    }
                    else if(selected.Desired)
                    {
                        if(runtime!=null){await runtime.DisposeAsync();runtime=null;}
                        var next=new NativeGuardedSession(bundle.OwnerPath,bundle.CorePath,bundle.GuardPath,bundle.CoreConfigPath,owner,selected.Run,policy,bundle.ExpectedExitHash,endpoint:endpoint);
                        lock(sync)runtime=next;
                        await next.InitializeAsync(attempt.Token);
                        var spec=JsonSerializer.SerializeToElement(new{Session=Guid.NewGuid().ToString("N"),Generation=selected.Generation,Token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),Config=bundle.ProfileText});
                        await next.ConnectAsync(spec,attempt.Token);attempt.Token.ThrowIfCancellationRequested();
                        lock(sync){if(intent.Operation!=selected.Operation||!intent.Desired)throw new OperationCanceledException(attempt.Token);phase="Connected";}
                    }
                    else
                    {
                        using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(35));
                        if(runtime!=null){await runtime.DisconnectAsync(cleanup.Token);runtime=null;}
                        else {var guard=new NativeWfpGuard(bundle.GuardPath,selected.Run,policy);await guard.RemoveAsync(cleanup.Token);}
                        lock(sync){if(!intent.Desired)phase="Disconnected";}
                    }
                }
                catch(OperationCanceledException)when(attempt.IsCancellationRequested){}
                catch(Exception e){lock(sync){phase="RecoveryFailed";failure=e.GetType().Name;}}
                finally{lock(sync)if(ReferenceEquals(operation,attempt))operation=null;attempt.Dispose();}
            }
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        finally
        {
            // SCM stop/shutdown retains desired intent and persistent blocks.
            // Restart adopts the same namespace after the old job is retired.
            try{if(runtime!=null)await runtime.DisposeAsync();}finally{bundle.Dispose();}
        }
    }
}
