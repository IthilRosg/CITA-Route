using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RoutePilot.Agent;

// Optional native service backend. All paths, policy and profile material come
// from one protected, digest-checked snapshot; IPC accepts no paths or keys.
internal sealed class NativeServiceRoute
{
    sealed record Settings(bool Enabled,string CatalogSha256,string CatalogName="native-catalog.json");
    internal sealed record Intent(string Run,ulong Generation,bool Desired,string Operation,string Boot="",string ConnectionId="");
    internal static Intent LoadOrCreateTemplateIntent(string statePath,bool allowCreate,Func<string,string> readProtected,Func<string> bootIdentity)
    {
        bool missing=false;
        try{_ = File.GetAttributes(statePath);}catch(FileNotFoundException){missing=true;}
        if(missing)
        {
            if(!allowCreate)throw new InvalidDataException("Configured native intent is missing.");
            var created=new Intent(Guid.NewGuid().ToString("N"),0,false,Guid.NewGuid().ToString("N"),bootIdentity());
            string temporary=statePath+"."+Guid.NewGuid().ToString("N")+".new";
            try
            {
                byte[] bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(created));
                using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){output.Write(bytes);output.Flush(true);}
                // Never overwrite a concurrent writer. Its winner is read and
                // validated through the same protected-file path below.
                try{File.Move(temporary,statePath,false);}
                catch(IOException)when(File.Exists(statePath)){}
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        return JsonSerializer.Deserialize<Intent>(readProtected(statePath))??throw new InvalidDataException("Native intent missing.");
    }
    static bool ValidConnectionId(string? id)=>id is {Length:32}&&id.All(c=>char.IsAsciiHexDigit(c)&&!char.IsUpper(c));
    static void CheckConnectionIntent(Intent value)
    {
        if(value.ConnectionId==null||value.ConnectionId.Length>0&&!ValidConnectionId(value.ConnectionId)||value.Desired&&!ValidConnectionId(value.ConnectionId))
            throw new InvalidDataException("Native connection lineage is unavailable or invalid.");
    }
    static Intent AdvanceConnectionAttempt(Intent value)=>value with{Generation=checked(value.Generation+1),Operation=Guid.NewGuid().ToString("N")};
    NativeTrustedBundle bundle;
    NativeForcedIntentStore? forcedStore;
    readonly string statePath;
    string owner="";
    JsonElement policy;
    NativeEndpointPermission? endpoint;
    bool Unconfigured=>string.IsNullOrEmpty(bundle.ProfileText);
    NativeProfileImportRequest? pendingImport;
    readonly object sync=new();
    readonly SemaphoreSlim signal=new(0,1);
    readonly NativeRepairBudget repairBudget=new();
    Intent intent;
    NativeGuardedSession? runtime;
    CancellationTokenSource? operation;
    string phase="Disconnected";
    string? failure;
    string? failureStage;
    string? importStage;
    Task? policyTask;
    string policyRequestState="Idle";
    internal static bool MayReportFailure(string currentOperation,string failedOperation,bool currentDesired,bool failedDesired,string currentPhase,string failedStage)
        =>currentOperation==failedOperation&&currentDesired==failedDesired&&(currentPhase!="Stopping"||failedStage=="Stopping");
    internal static string RecoveryAction(bool desired,string phase,string? coreState,bool policyUnconfirmed=false)
    {
        if(!desired||phase!="Connected")return "None";
        if(AttentionFailure(coreState,policyUnconfirmed)!=null)return "Attention";
        return coreState is "PolicyFailClosed" or "CoreRepairFailed" or "TransportUnavailable" or "TransportExhausted" or "Exhausted"?"Retry":"None";
    }
    internal static string? AttentionFailure(string? coreState,bool policyUnconfirmed=false)
        =>policyUnconfirmed||coreState=="PolicyRecoveryUnconfirmed"?"PolicyRecoveryUnconfirmed":coreState is "CoreRecoveryUnconfirmed" or "GuardLost" or "GuardLostUnconfirmed" or "TransportCloseUnconfirmed" or "LeaseCloseUnconfirmed"?coreState:null;
    internal static string ReportedPhase(string phase,string? coreState,bool policyUnconfirmed)
        =>phase!="Connected"?phase:policyUnconfirmed?"PolicyRecoveryUnconfirmed":coreState is not (null or "Active")?coreState:phase;
    internal static bool MayStartReplacement(string currentOperation,string selectedOperation,bool desired,bool canceled)
        =>!canceled&&desired&&currentOperation==selectedOperation;
    NativeServiceRoute(string root,NativeTrustedBundle bundle)
    {
        this.bundle=bundle;statePath=Path.Combine(root,"intent.json");
        // Only a catalogued mutable role can activate forced routing. A stray
        // file left by an older install must not become policy after downgrade.
        if(!string.IsNullOrEmpty(bundle.ForcedIntentPath))forcedStore=NativeForcedIntentStore.Protected(root);
        (owner,policy,endpoint)=ReadBinding(bundle);
        intent=LoadOrCreateTemplateIntent(statePath,Unconfigured,p=>NativeTrustedBundle.ReadProtectedText(p,4096),()=>NativeBootIdentity.Current);
        CheckConnectionIntent(intent);
        if(intent.Run.Length!=32||!intent.Run.All(Uri.IsHexDigit)||intent.Operation.Length!=32||!intent.Operation.All(Uri.IsHexDigit))throw new InvalidDataException("Native intent invalid.");
        if(intent.Desired&&!NativeBootIdentity.MayResume(intent.Desired,intent.Boot,NativeBootIdentity.Current)){intent=intent with{Desired=false,Boot=NativeBootIdentity.Current,Operation=Guid.NewGuid().ToString("N")};Save(intent);}
        if(intent.Desired){if(Unconfigured)throw new InvalidDataException("Unconfigured native intent cannot connect.");intent=AdvanceConnectionAttempt(intent);Save(intent);phase="Preparing";}
        if(Unconfigured)phase="Unconfigured";else if(File.Exists(statePath)){if(!intent.Desired)phase="Stopping";signal.Release();}
    }
    static (string Owner,JsonElement Policy,NativeEndpointPermission? Endpoint) ReadBinding(NativeTrustedBundle bundle)
    {
        if(string.IsNullOrEmpty(bundle.GuardPath)||string.IsNullOrEmpty(bundle.PolicyText)||string.IsNullOrEmpty(bundle.CoreConfigPath)||bundle.ProfileText.Length>0&&(bundle.ExpectedExitHash.Length!=64||!bundle.ExpectedExitHash.All(Uri.IsHexDigit)))throw new InvalidDataException("Incomplete native service snapshot.");
        using var plan=JsonDocument.Parse(bundle.PolicyText);var policy=plan.RootElement.Clone();
        using var config=JsonDocument.Parse(NativeTrustedBundle.ReadProtectedText(bundle.CoreConfigPath));
        string owner=config.RootElement.GetProperty("outbounds").EnumerateArray().Single(x=>x.GetProperty("type").GetString()=="cita_native_lab").GetProperty("owner").GetString()!;
        if(owner.Length!=32||!owner.All(Uri.IsHexDigit))throw new InvalidDataException("Native core owner missing.");
        if(string.IsNullOrEmpty(bundle.ProfileText)){if(bundle.ExpectedExitHash.Length!=0)throw new InvalidDataException("Native template carries an exit expectation.");return(owner,policy,null);}
        var match=System.Text.RegularExpressions.Regex.Match(bundle.ProfileText,@"(?mi)^\s*Endpoint\s*=\s*([^\r\n#]+)");
        if(!match.Success||!System.Net.IPEndPoint.TryParse(match.Groups[1].Value.Trim(),out var peer)||peer.Address.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork||peer.Port==0)throw new InvalidDataException("Native service snapshot requires a pinned IPv4 endpoint.");
        return(owner,policy,new(bundle.OwnerPath,peer.Address.ToString(),checked((ushort)peer.Port)));
    }
    internal static NativeServiceRoute? TryLoad()
        => NativeMaintenanceGate.LoadChecked(NativeMaintenanceGate.IsPending,TryLoadCore);
    // Caller holds a read-only intent lease, validated desired=false, for the
    // entire maintenance session. This path never bypasses bundle validation.
    internal static NativeServiceRoute? TryLoadForMaintenance()=>TryLoadCore();
    static NativeServiceRoute? TryLoadCore()
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
    {get{lock(sync)return JsonSerializer.SerializeToElement(new{phase=ReportedPhase(phase,runtime?.Core.State,runtime?.PolicyRecoveryUnconfirmed==true),desired=intent.Desired,operation=intent.Operation,generation=intent.Generation,connectionId=intent.ConnectionId,profileName=bundle.ProfileName,profileId=bundle.ProfileId,policyMode=policy.GetProperty("Vpn").EnumerateArray().Any(x=>x.GetString()=="0.0.0.0/0")?"FullVpn":"SelectedRoutes",errorCode=failure,errorStage=failureStage,importStage,policyRequestState,zapretIssue=bundle.ZapretIssue,runtime=runtime?.Snapshot});}}
    internal JsonElement ReadResources(string? expectedOperation)
    {
        lock(sync)
        {
            if(expectedOperation!=intent.Operation||!intent.Desired||phase!="Connected"||runtime==null)return JsonSerializer.SerializeToElement(new{available=false,phase,operation=intent.Operation});
            try
            {
                var usage=runtime.ResourceUsage;using var service=System.Diagnostics.Process.GetCurrentProcess();service.Refresh();
                return JsonSerializer.SerializeToElement(new{available=true,phase,operation=intent.Operation,generation=intent.Generation,coreState=runtime.Core.State,tickMs=Environment.TickCount64,logicalProcessors=Environment.ProcessorCount,job=usage,
                    service=new{pid=service.Id,startedTicks=service.StartTime.ToUniversalTime().Ticks,cpuTicks=service.TotalProcessorTime.Ticks,privateBytes=service.PrivateMemorySize64,workingSetBytes=service.WorkingSet64,handles=service.HandleCount,threads=service.Threads.Count,managedBytes=GC.GetTotalMemory(false),gc0=GC.CollectionCount(0),gc1=GC.CollectionCount(1),gc2=GC.CollectionCount(2)}});
            }
            catch(Exception error)when(error is ObjectDisposedException or InvalidOperationException or System.ComponentModel.Win32Exception){return JsonSerializer.SerializeToElement(new{available=false,phase,operation=intent.Operation,errorCode="ResourceSnapshotUnavailable"});}
        }
    }
    internal JsonElement ReadDecisionTrace(string? expectedOperation,bool? enable=null)
    {
        lock(sync)
        {
            if(expectedOperation!=intent.Operation||runtime==null||enable!=false&&(!intent.Desired||phase!="Connected"))throw new InvalidOperationException("Decision trace is unavailable for this session.");
            if(enable.HasValue)runtime.EnableDecisionTrace(enable.Value);
            var trace=runtime.DecisionTrace;
            return JsonSerializer.SerializeToElement(new{traceEnabled=trace.Enabled,traceEntries=trace.Entries});
        }
    }
    internal NativeForcedIntentDocument ReadForcedIntent(ulong minimumRevision=1)
    {
        NativeMaintenanceGate.ThrowIfPending();
        lock(sync){if(forcedStore==null)throw new FileNotFoundException("Forced intent is not initialized.");return forcedStore.Read(minimumRevision);}
    }
    internal NativeForcedIntentDocument InitializeForcedIntent()
    {
        NativeMaintenanceGate.ThrowIfPending();
        lock(sync)
        {
            if(intent.Desired||phase is not ("Disconnected" or "Unconfigured")||forcedStore!=null)throw new InvalidOperationException("Forced intent initialization refused.");
            string root=Path.GetDirectoryName(statePath)!;var candidate=NativeForcedIntentStore.Protected(root);var document=candidate.InitializeNew();forcedStore=candidate;return document;
        }
    }
    internal NativeForcedIntentDocument ReplaceForcedIntent(ulong expectedRevision,NativeForcedIntentRule[] rules)
    {
        NativeMaintenanceGate.ThrowIfPending();
        lock(sync)
        {
            if(intent.Desired||phase is not ("Disconnected" or "Unconfigured")||forcedStore==null)throw new InvalidOperationException("Forced intent replacement refused.");
            var validated=rules??throw new InvalidDataException("Forced intent rules missing.");NativeForcedIntent.Validate(new NativeForcedIntentDocument(1,expectedRevision,validated));return forcedStore.Replace(expectedRevision,validated);
        }
    }
    internal void RequestPolicy(NativePolicyRequest request,string? expectedOperation)
    {
        NativeMaintenanceGate.ThrowIfPending();
        // Freeze and validate the entire request before the asynchronous task
        // can outlive its caller. An invalid epoch must not appear accepted.
        request=NativeRoutePolicyPublisher.SnapshotRequest(request);
        lock(sync)
        {
            if(!intent.Desired||phase!="Connected"||runtime==null||expectedOperation!=intent.Operation||policyTask is {IsCompleted:false})throw new InvalidOperationException("Native policy operation refused.");
            var selected=runtime;string operation=intent.Operation;policyRequestState="Requested";
            policyTask=Task.Run(async()=>{string result;try{using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(45));await selected.PublishPolicyAsync(request,budget.Token);result="Applied";}catch{result="Failed";}lock(sync){if(intent.Operation==operation&&ReferenceEquals(runtime,selected))policyRequestState=result;}});
        }
    }
    internal bool Busy{get{lock(sync)return intent.Desired||phase is not ("Disconnected" or "Unconfigured");}}
    internal void PrepareForShutdown()
    {
        lock(sync){var next=intent with{Desired=false};Save(next);intent=next;phase="Stopping";pendingImport=null;try{operation?.Cancel();}catch(ObjectDisposedException){};if(signal.CurrentCount==0)signal.Release();}
    }
    internal void RequestImport(NativeProfileImportRequest request,string? expectedOperation)
    {
        NativeMaintenanceGate.ThrowIfPending();
        lock(sync)
        {
            if(intent.Desired||phase is not ("Disconnected" or "Unconfigured")||expectedOperation!=intent.Operation||pendingImport!=null)throw new InvalidOperationException("Disconnect before importing a native profile.");
            if(request.Base64.Length is <1 or >16384||request.Name.Length>160||request.RequestId.Length!=0&&(request.RequestId.Length!=32||!request.RequestId.All(Uri.IsHexDigit)||request.RequestId==expectedOperation))throw new InvalidDataException("Native import input refused.");
            var next=intent with{Generation=checked(intent.Generation+1),Operation=request.RequestId.Length==0?Guid.NewGuid().ToString("N"):request.RequestId,ConnectionId=""};Save(next);intent=next;pendingImport=request;phase="Importing";failure=null;failureStage=null;if(signal.CurrentCount==0)signal.Release();
        }
    }
    internal void Request(bool connect,string? expectedOperation,string? connectionId=null)
    {
        if(connect)NativeMaintenanceGate.ThrowIfPending();
        lock(sync)
        {
            if(connect&&connectionId!=null)
            {
                if(!ValidConnectionId(connectionId))throw new InvalidDataException("Invalid native connection identity.");
                if(intent.Desired)
                {
                    if(string.Equals(intent.ConnectionId,connectionId,StringComparison.Ordinal))return;
                    throw new InvalidDataException("Another native connection is active.");
                }
                if(expectedOperation!=intent.Operation)throw new InvalidDataException("Stale native start refused.");
                if(string.Equals(intent.ConnectionId,connectionId,StringComparison.Ordinal))throw new InvalidDataException("Retired native connection identity cannot be reused.");
            }
            if(connect&&phase=="Importing")throw new InvalidOperationException("Native import owns the session.");
            if(connect&&Unconfigured)throw new InvalidOperationException("Import a native profile first.");
            if(connect&&phase=="Stopping")throw new InvalidOperationException("Wait for native cleanup before connecting.");
            if(!connect&&(intent.Desired||phase=="Importing")&&expectedOperation!=intent.Operation)throw new InvalidDataException("Stale native stop refused.");
            if(!connect)pendingImport=null;
            if(connect&&intent.Desired&&phase!="RecoveryFailed")return;
            var next=connect?intent with{Desired=true,Generation=checked(intent.Generation+1),Operation=Guid.NewGuid().ToString("N"),Boot=NativeBootIdentity.Current,ConnectionId=connectionId??Guid.NewGuid().ToString("N")}:intent with{Desired=false};
            Save(next);intent=next;phase=connect?"Preparing":"Stopping";failure=null;failureStage=null;try{operation?.Cancel();}catch(ObjectDisposedException){}
            if(signal.CurrentCount==0)signal.Release();
        }
    }
    internal void StopByConnectionId(string? connectionId)
    {
        if(!ValidConnectionId(connectionId))throw new InvalidDataException("Invalid native connection identity.");
        lock(sync)
        {
            if(!string.Equals(intent.ConnectionId,connectionId,StringComparison.Ordinal))throw new InvalidDataException("Stale native connection identity refused.");
            if(!intent.Desired)return;
            var next=intent with{Desired=false};Save(next);intent=next;phase="Stopping";failure=null;failureStage=null;
            try{operation?.Cancel();}catch(ObjectDisposedException){}
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
                        string? coreState=runtime?.Core.State;bool policyUnconfirmed=runtime?.PolicyRecoveryUnconfirmed==true;
                        string action=RecoveryAction(intent.Desired,phase,coreState,policyUnconfirmed);
                        if(action=="None")continue;
                        if(NativeMaintenanceGate.IsPending()){phase="RecoveryFailed";failure="NativeMaintenanceInProgress";failureStage="Initializing";continue;}
                        if(action=="Attention"){phase="RecoveryFailed";failure=AttentionFailure(coreState,policyUnconfirmed);failureStage=failure=="PolicyRecoveryUnconfirmed"?"Policy":"Protection";continue;}
                        if(!repairBudget.TryAcquire()){phase="RecoveryFailed";failure="RepairBudgetExhausted";continue;}
                        var replacement=AdvanceConnectionAttempt(intent);Save(replacement);intent=replacement;phase="Recovering";
                    }
                }
                Intent selected;CancellationTokenSource attempt;NativeProfileImportRequest? import;
                lock(sync){selected=intent;import=pendingImport;pendingImport=null;operation=attempt=CancellationTokenSource.CreateLinkedTokenSource(stop);}
                string attemptStage=import!=null?"Importing":selected.Desired?"Initializing":"Stopping";
                try
                {
                    if(import!=null)
                    {
                        NativeMaintenanceGate.ThrowIfPending();
                        using var prepared=await NativeProfileImport.Prepare(Path.GetDirectoryName(statePath)!,bundle,policy,import,attempt.Token,stage=>{lock(sync)importStage=stage;});
                        var binding=ReadBinding(prepared.Bundle);NativeTrustedBundle previous;
                        lock(sync){attempt.Token.ThrowIfCancellationRequested();if(intent.Operation!=selected.Operation)throw new OperationCanceledException(attempt.Token);prepared.Commit(Path.GetDirectoryName(statePath)!,attempt.Token);previous=bundle;bundle=prepared.Bundle;(owner,policy,endpoint)=binding;phase="Disconnected";}
                        previous.Dispose();
                    }
                    else if(selected.Desired)
                    {
                        NativeMaintenanceGate.ThrowIfPending();
                        if(runtime!=null){await runtime.DisposeAsync();runtime=null;}
                        // A Stop or a newer profile may arrive while retiring
                        // the old runtime. Never start the obsolete intent.
                        NativeGuardedSession next;
                        lock(sync){NativeMaintenanceGate.ThrowIfPending();if(!MayStartReplacement(intent.Operation,selected.Operation,intent.Desired,attempt.IsCancellationRequested))throw new OperationCanceledException(attempt.Token);next=new NativeGuardedSession(bundle.OwnerPath,bundle.CorePath,bundle.GuardPath,bundle.CoreConfigPath,owner,selected.Run,policy,bundle.ExpectedExitHash,endpoint:endpoint,adaptive:true,zapretBundle:bundle.ZapretBundle,forcedStore:forcedStore);runtime=next;}
                        await next.InitializeAsync(attempt.Token);attemptStage="Connecting";
                        var spec=JsonSerializer.SerializeToElement(new{Session=Guid.NewGuid().ToString("N"),Generation=selected.Generation,Token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),Config=bundle.ProfileText});
                        await next.ConnectAsync(spec,attempt.Token);attempt.Token.ThrowIfCancellationRequested();
                        lock(sync){if(intent.Operation!=selected.Operation||!intent.Desired)throw new OperationCanceledException(attempt.Token);phase="Connected";}
                    }
                    else
                    {
                        using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(35));
                        if(runtime!=null){await runtime.DisconnectAsync(cleanup.Token);runtime=null;}
                        else {var guard=new NativeWfpGuard(bundle.GuardPath,selected.Run,policy);await guard.RemoveAsync(cleanup.Token);}
                        lock(sync){if(!intent.Desired)phase=Unconfigured?"Unconfigured":"Disconnected";}
                    }
                }
                catch(OperationCanceledException)when(attempt.IsCancellationRequested){}
                catch(Exception e){lock(sync){if(MayReportFailure(intent.Operation,selected.Operation,intent.Desired,selected.Desired,phase,attemptStage)){phase="RecoveryFailed";failure=e is NativeMaintenanceInProgress?nameof(NativeMaintenanceInProgress):NativeCandidateValidationError.Code(e);failureStage=attemptStage;}}}
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
