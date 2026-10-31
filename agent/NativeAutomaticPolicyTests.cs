using System.Text.Json;

namespace RoutePilot.Agent;

internal static class NativeAutomaticPolicyTests
{
    internal static async Task Run()
    {
        await ExitedPolicyOwner();
        long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var old=new NativeObservedDestination("old.example",@"C:\apps\browser.exe","8.8.8.8",443);
        var failed=new NativeObservedDestination("failed.example",@"C:\apps\browser.exe","1.1.1.1",443);
        var next=new NativeObservedDestination("next.example",@"C:\apps\browser.exe","8.8.4.4",443);
        NativeDomainRule Rule(NativeObservedDestination value,string route)=>new(value.Domain,value.Application,"tcp",route,443,[value.Address],now+60000);
        var confirmed=new Dictionary<NativeObservedDestination,NativeDomainRule>{{old,Rule(old,"direct")}};
        const string connectorEpoch="0123456789abcdef0123456789abcdef";
        var admission=new NativeZapretPolicyGate();admission.Bind(connectorEpoch,()=>true);
        string document=JsonSerializer.Serialize(new{Epoch=connectorEpoch,Rules=new[]{Rule(failed,"zapret")}});
        await admission.PublishAsync(1,document,_=>Task.CompletedTask,CancellationToken.None);
        var state=new NativeAutomaticPolicyState();int closed=0;void Close(){closed++;admission.Invalidate();}
        var candidate=NativeRoutePolicyPublisher.StageAutomatic(confirmed,failed,Rule(failed,"zapret"),now);
        string existingId=Guid.NewGuid().ToString("N"),freshId=Guid.NewGuid().ToString("N");
        var withProof=new Dictionary<NativeObservedDestination,NativeDomainRule>{{old,Rule(old,"direct") with{CalibrationId=existingId}}};
        var stagedProof=NativeRoutePolicyPublisher.StageAutomatic(withProof,failed,Rule(failed,"zapret") with{CalibrationId=freshId},now);
        if(stagedProof[old].CalibrationId!=existingId||stagedProof[failed].CalibrationId!=freshId||withProof.Count!=1)throw new IOException("Staging lost or reassigned an existing proof.");
        var rejectionState=new NativeAutomaticPolicyState();var oldRules=new Dictionary<NativeObservedDestination,NativeDomainRule>(withProof);bool rejectionCaught=false;
        try{await NativeRoutePolicyPublisher.CommitAutomaticAsync(oldRules,stagedProof,()=>{rejectionState.BeforeWrite(123);throw new NativeCorePolicyRefusedException();},CancellationToken.None,rejectionState,()=>{});}catch(NativeCorePolicyRefusedException){rejectionCaught=true;}
        if(!rejectionCaught||!rejectionState.ResetRequired||rejectionState.RepairRequired||oldRules.Count!=1)throw new IOException("Explicit refusal was not marked for empty reconciliation.");
        bool refusalRepair=false;
        await NativeRoutePolicyPublisher.RecoverAutomaticFailureAsync(rejectionState,()=>NativeRoutePolicyPublisher.CommitAutomaticAsync(oldRules,new(),()=>{rejectionState.BeforeWrite(123);rejectionState.Acknowledged();return Task.CompletedTask;},CancellationToken.None,rejectionState,()=>{},isReset:true),()=>{refusalRepair=true;return Task.CompletedTask;});
        if(refusalRepair||rejectionState.ResetRequired||oldRules.Count!=0)throw new IOException("Explicit refusal did not reconcile empty without repair.");
        bool refused=false;try{await NativeRoutePolicyPublisher.CommitAutomaticAsync(confirmed,candidate,()=>throw new IOException("guard refused"),CancellationToken.None,state,Close);}catch(IOException){refused=true;}
        if(!refused||state.ResetRequired||closed!=0||confirmed.Count!=1||!confirmed.ContainsKey(old)||confirmed.ContainsKey(failed))throw new IOException("Failed pre-write policy contaminated confirmed automatic rules.");
        using(var canceled=new CancellationTokenSource())
        {
            ulong floor=1;bool canceledAfterAck=false;
            try{await NativeRoutePolicyPublisher.CommitAutomaticAsync(confirmed,candidate,()=>{state.BeforeWrite(123);floor=2;state.Acknowledged();canceled.Cancel();return Task.CompletedTask;},canceled.Token,state,Close);}catch(OperationCanceledException){canceledAfterAck=true;}
            if(!canceledAfterAck||floor!=2||!state.ResetRequired||state.RepairRequired||closed!=1||admission.Admission.TryEnter()!=null||confirmed.ContainsKey(failed)||!confirmed.ContainsKey(old))throw new IOException("Canceled post-ACK policy did not require reconciliation and close admission.");
            var reset=new Dictionary<NativeObservedDestination,NativeDomainRule>();
            bool premature=false,called=false;try{await NativeRoutePolicyPublisher.CommitAutomaticAsync(confirmed,candidate,()=>{called=true;return Task.CompletedTask;},CancellationToken.None,state,Close);}catch(IOException){premature=true;}
            if(!premature||called)throw new IOException("A new policy bypassed reconciliation.");
            bool failClosed=false;
            await NativeRoutePolicyPublisher.RecoverAutomaticFailureAsync(state,()=>NativeRoutePolicyPublisher.CommitAutomaticAsync(confirmed,reset,()=>{state.BeforeWrite(123);floor++;state.Acknowledged();return Task.CompletedTask;},CancellationToken.None,state,Close,isReset:true),()=>{failClosed=true;return Task.CompletedTask;});
            if(failClosed)throw new IOException("Successful immediate reconciliation unnecessarily closed the guard.");
            if(state.ResetRequired||confirmed.Count!=0||floor!=3)throw new IOException("Empty reconciliation did not complete.");
            var nextCandidate=NativeRoutePolicyPublisher.StageAutomatic(confirmed,next,Rule(next,"direct"),now);
            NativeObservedDestination[]? published=null;
            await NativeRoutePolicyPublisher.CommitAutomaticAsync(confirmed,nextCandidate,()=>{state.BeforeWrite(123);published=nextCandidate.Keys.ToArray();floor++;state.Acknowledged();return Task.CompletedTask;},CancellationToken.None,state,Close);
            if(floor!=4||published==null||published.Contains(failed)||published.Contains(old)||!published.Contains(next)||confirmed.ContainsKey(failed))throw new IOException("Next policy included an unconfirmed candidate.");
        }
        var stale=NativeRoutePolicyPublisher.StageAutomatic(confirmed,failed,Rule(failed,"zapret"),now);bool staleRejected=false;
        try{await NativeRoutePolicyPublisher.CommitAutomaticAsync(confirmed,stale,()=>{state.BeforeWrite(123);state.Acknowledged();return Task.CompletedTask;},CancellationToken.None,state,Close,()=>false);}catch(NativeStaleEvidenceException){staleRejected=true;}
        if(!staleRejected||!state.ResetRequired||confirmed.ContainsKey(failed)||!confirmed.ContainsKey(next))throw new IOException("Stale evidence committed a candidate.");
        var empty=new Dictionary<NativeObservedDestination,NativeDomainRule>();bool cleanupFailed=false;
        try{await NativeRoutePolicyPublisher.CommitAutomaticAsync(confirmed,empty,()=>throw new IOException("reset failed"),CancellationToken.None,state,Close,isReset:true);}catch(IOException){cleanupFailed=true;}
        if(!cleanupFailed||!state.ResetRequired||confirmed.Count!=1)throw new IOException("Failed cleanup falsely committed an empty policy.");
        await NativeRoutePolicyPublisher.CommitAutomaticAsync(confirmed,empty,()=>{state.BeforeWrite(123);state.Acknowledged();return Task.CompletedTask;},CancellationToken.None,state,Close,isReset:true);
        if(confirmed.Count!=0)throw new IOException("Successful cleanup did not clear confirmed rules.");
        bool unknown=false;try{await NativeRoutePolicyPublisher.CommitAutomaticAsync(confirmed,candidate,()=>{state.BeforeWrite(123);throw new IOException("ACK unknown");},CancellationToken.None,state,Close);}catch(IOException){unknown=true;}
        if(!unknown||!state.ResetRequired||!state.RepairRequired)throw new IOException("Unknown ACK was not fenced for core repair.");
        bool attempted=false,blocked=false;try{await NativeRoutePolicyPublisher.CommitAutomaticAsync(confirmed,empty,()=>{attempted=true;return Task.CompletedTask;},CancellationToken.None,state,Close);}catch(IOException){blocked=true;}
        if(!blocked||attempted)throw new IOException("Unknown ACK was retried without core repair.");
        bool resetCalled=false,failCloseCalled=false;
        await NativeRoutePolicyPublisher.RecoverAutomaticFailureAsync(state,()=>{resetCalled=true;return Task.CompletedTask;},()=>{failCloseCalled=true;return Task.CompletedTask;});
        if(resetCalled||!failCloseCalled||!state.RepairRequired||state.PossibleCorePid!=123)throw new IOException("Unknown ACK did not take the fail-closed path.");
        state.NewCore();if(state.ResetRequired||state.RepairRequired)throw new IOException("New core did not retire unknown ACK state.");
        state.RequireReset();resetCalled=false;failCloseCalled=false;
        await NativeRoutePolicyPublisher.RecoverAutomaticFailureAsync(state,()=>{resetCalled=true;throw new IOException("reset failure");},()=>{failCloseCalled=true;return Task.CompletedTask;});
        if(!resetCalled||!failCloseCalled||!state.RepairRequired)throw new IOException("Failed reconciliation did not require fail-closed repair.");

        // Cancel while waiting for the actual binding command lock: the
        // possible-write callback must not be invoked before the first write.
        var binding=new NativeCoreBinding("fixture","fixture","fixture","fixture","fixture");
        var commandGate=(SemaphoreSlim)typeof(NativeCoreBinding).GetField("gate",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(binding)!;
        await commandGate.WaitAsync();
        try
        {
            using var canceled=new CancellationTokenSource();bool marked=false;
            var pending=binding.PublishPolicyAsync(1,"{}",new string('a',64),canceled.Token,()=>marked=true);
            if(pending.IsCompleted)throw new IOException("Binding policy did not wait for its lock.");
            canceled.Cancel();bool canceledWait=false;try{await pending;}catch(OperationCanceledException){canceledWait=true;}
            if(!canceledWait||marked)throw new IOException("A canceled command lock wait was marked as a possible write.");
        }
        finally{commandGate.Release();}

        using var self=System.Diagnostics.Process.GetCurrentProcess();
        var processField=typeof(NativeCoreBinding).GetField("process",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        processField.SetValue(binding,self);
        try
        {
            var recovery=new NativeCoreRecovery(()=>binding,_=>Task.FromResult(true));
            typeof(NativeCoreRecovery).GetField("binding",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.SetValue(recovery,binding);
            typeof(NativeCoreRecovery).GetProperty("State",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.SetValue(recovery,"Active");
            var publisher=new NativeRoutePolicyPublisher(null!,recovery,"fixture",1);
            if(await publisher.ResetForCoreAsync(0,CancellationToken.None)||await publisher.ResetForCoreAsync(self.Id+1,CancellationToken.None)||publisher.Revision!=0||publisher.Phase!="Idle")throw new IOException("Stale core observation mutated the publisher or was treated as a fatal reset.");
            bool guardCalled=false,wrongPid=false;
            try{await recovery.FailClosedPolicyAsync(self.Id+1,_=>{guardCalled=true;return Task.CompletedTask;},CancellationToken.None);}catch(IOException){wrongPid=true;}
            if(!wrongPid||guardCalled||recovery.State!="Active")throw new IOException("Foreign core was affected by fail-closed cleanup.");
            // A confirmed retired owner may finish a late policy failure after
            // replacement. It must not close the current guard or cancel repair.
            typeof(NativeCoreRecovery).GetField("retiredPolicyPid",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.SetValue(recovery,self.Id+1);
            await recovery.FailClosedPolicyAsync(self.Id+1,_=>{guardCalled=true;return Task.CompletedTask;},CancellationToken.None);
            if(guardCalled||recovery.State!="Active")throw new IOException("Retired policy owner disturbed replacement core.");
            bool unknownRetired=false;
            try{await recovery.FailClosedPolicyAsync(self.Id+2,_=>{guardCalled=true;return Task.CompletedTask;},CancellationToken.None);}catch(IOException){unknownRetired=true;}
            if(!unknownRetired||guardCalled)throw new IOException("Unknown policy owner was accepted as retired.");
            await recovery.FailClosedPolicyAsync(self.Id,_=>{guardCalled=true;return Task.CompletedTask;},CancellationToken.None);
            if(!guardCalled||recovery.State!="PolicyFailClosed")throw new IOException("Matching core did not finish fail-closed cleanup.");
            typeof(NativeCoreRecovery).GetProperty("State",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.SetValue(recovery,"Active");
            bool unconfirmed=false;
            try{await recovery.FailClosedPolicyAsync(self.Id,_=>throw new IOException("guard failure"),CancellationToken.None);}catch(IOException){unconfirmed=true;}
            if(!unconfirmed||recovery.State!="PolicyRecoveryUnconfirmed")throw new IOException("Unconfirmed guard closure was hidden.");
        }
        finally{processField.SetValue(binding,null);}
        if(NativeServiceRoute.RecoveryAction(true,"Connected","PolicyFailClosed")!="Retry"||NativeServiceRoute.RecoveryAction(false,"Connected","PolicyFailClosed")!="None"||NativeServiceRoute.RecoveryAction(true,"Stopping","PolicyFailClosed")!="None"||NativeServiceRoute.RecoveryAction(true,"Connected","Closed")!="None")throw new IOException("Policy fail-close recovery confused a user Stop or normal Closed state.");
        if(NativeServiceRoute.RecoveryAction(true,"Connected","PolicyRecoveryUnconfirmed")!="Attention"||NativeServiceRoute.RecoveryAction(true,"Connected","Active",true)!="Attention")throw new IOException("Unconfirmed policy cleanup did not require attention.");
        foreach(string protectionFailure in new[]{"CoreRecoveryUnconfirmed","GuardLost","GuardLostUnconfirmed","TransportCloseUnconfirmed","LeaseCloseUnconfirmed"})
        {
            if(NativeServiceRoute.RecoveryAction(true,"Connected",protectionFailure)!="Attention"||NativeServiceRoute.AttentionFailure(protectionFailure)!=protectionFailure)throw new IOException("Unsafe protection state did not stop automatic recovery.");
            if(NativeServiceRoute.RecoveryAction(false,"Connected",protectionFailure)!="None"||NativeServiceRoute.RecoveryAction(true,"Stopping",protectionFailure)!="None")throw new IOException("An old protection failure overrode manual Stop.");
        }
        if(NativeServiceRoute.RecoveryAction(true,"Connected","GuardUnconfirmed")!="None"||NativeServiceRoute.RecoveryAction(true,"Connected","TransportDegraded")!="None"||NativeServiceRoute.AttentionFailure("Active")!=null||NativeServiceRoute.AttentionFailure("GuardLost",true)!="PolicyRecoveryUnconfirmed")throw new IOException("Pending guard checks or partial transport health were treated as terminal failures.");
        if(NativeServiceRoute.RecoveryAction(true,"Connected","CoreRepairFailed")!="Retry"||NativeServiceRoute.RecoveryAction(false,"Connected","CoreRepairFailed")!="None"||NativeServiceRoute.RecoveryAction(true,"Stopping","CoreRepairFailed")!="None")throw new IOException("A failed confirmed core repair was confused with user Stop.");
        if(NativeServiceRoute.ReportedPhase("Connected","Active",true)!="PolicyRecoveryUnconfirmed"||NativeServiceRoute.ReportedPhase("Stopping","Active",true)!="Stopping")throw new IOException("Unconfirmed policy cleanup was reported as connected or masked Stop.");
        if(!NativeServiceRoute.MayStartReplacement("a","a",true,false)||NativeServiceRoute.MayStartReplacement("b","a",true,false)||NativeServiceRoute.MayStartReplacement("a","a",false,false)||NativeServiceRoute.MayStartReplacement("a","a",true,true))throw new IOException("Stale or canceled intent could start a replacement runtime.");
    }
    static async Task ExitedPolicyOwner()
    {
        if(!OperatingSystem.IsWindows())return;
        var info=new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{"-NoProfile","-NonInteractive","-Command","exit 0"})info.ArgumentList.Add(arg);
        using var process=System.Diagnostics.Process.Start(info)??throw new IOException("Exited-owner fixture failed");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        int pid=process.Id;var binding=new NativeCoreBinding("fixture","fixture","fixture","fixture","fixture");
        const System.Reflection.BindingFlags fields=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
        typeof(NativeCoreBinding).GetField("process",fields)!.SetValue(binding,process);
        var recovery=new NativeCoreRecovery(()=>binding,_=>Task.FromResult(true));
        typeof(NativeCoreRecovery).GetField("binding",fields)!.SetValue(recovery,binding);
        typeof(NativeCoreRecovery).GetField("desired",fields)!.SetValue(recovery,1);
        bool closed=false;await recovery.FailClosedPolicyAsync(pid,_=>{closed=true;return Task.CompletedTask;},CancellationToken.None);
        if(closed||recovery.State!="Recovering"||(int)typeof(NativeCoreRecovery).GetField("desired",fields)!.GetValue(recovery)!=1||typeof(NativeCoreRecovery).GetField("binding",fields)!.GetValue(recovery)!=null||(int)typeof(NativeCoreRecovery).GetField("retiredPolicyPid",fields)!.GetValue(recovery)!=pid)throw new IOException("Confirmed dead policy owner disabled desired recovery or touched the guard.");
        await recovery.DisposeAsync();
    }
}
