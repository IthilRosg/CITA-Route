namespace RoutePilot.Agent;

internal static class NativeCalibrationTests
{
    internal static bool LockWaitCovered{get;private set;}
    static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    internal static async Task Run()
    {
        LockWaitCovered=false;
        var one=new NativeObservedDestination("one.example","app","1.1.1.1",443);
        var zapretTrace=new NativeDecisionTrace();zapretTrace.Record("zapret-applied",one);Check(zapretTrace.Snapshot().Entries.Length==0,"Trace must remain opt-in.");
        zapretTrace.Enable(true);zapretTrace.Record("zapret-applied",one);zapretTrace.Record("unknown-private-event",one);
        Check(zapretTrace.Snapshot().Entries is [{Event:"zapret-applied"}],"Known Zapret event missing or unknown event accepted.");
        zapretTrace.Enable(false);Check(zapretTrace.Snapshot().Entries.Length==0,"Disabling trace must clear entries.");
        var wireRule=new NativeDomainRule("one.example",@"C:\app.exe","tcp","direct",443,["1.1.1.1"],DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds());
        var automaticJson=System.Text.Json.JsonSerializer.SerializeToElement(wireRule);
        Check(!automaticJson.TryGetProperty("CalibrationId",out _),"Absent private proof must not be serialized to older cores.");
        var proofA=NativeRoutePolicyPublisher.WithFreshCalibrationId(wireRule,true,()=>true)!;
        var proofB=NativeRoutePolicyPublisher.WithFreshCalibrationId(wireRule,true,()=>true)!;
        Check(proofA.CalibrationId!=proofB.CalibrationId&&NativeRoutePolicyPublisher.ValidCalibrationId(proofA.CalibrationId!)&&wireRule.CalibrationId==null,"Fresh probes must create distinct private identities.");
        Check(NativeRoutePolicyPublisher.WithFreshCalibrationId(wireRule,false,()=>true)==null&&NativeRoutePolicyPublisher.WithFreshCalibrationId(wireRule,true,()=>false)==null,"Failed or stale proof produced a private identity.");
        bool copyDenied=false;try{NativeRoutePolicyPublisher.WithFreshCalibrationId(proofA,true,()=>true);}catch(InvalidDataException){copyDenied=true;}Check(copyDenied,"An existing proof must not be promoted as a fresh probe.");
        foreach(string? value in new[]{proofA.CalibrationId,"","FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF","0123456789abcdef0123456789abcdeg"})
        {
            bool publicDenied=false;try{NativeRoutePolicyPublisher.SnapshotRequest(new NativePolicyRequest(new string('a',32),[wireRule with{CalibrationId=value}]));}catch(InvalidDataException){publicDenied=true;}
            Check(publicDenied,"Public input accepted a private identity.");
        }
        NativeRoutePolicyPublisher.Validate([proofA],allowPrivateCalibrationId:true);
        bool wildcardProofDenied=false;try{NativeRoutePolicyPublisher.Validate([proofA with{Application=""}],allowPrivateCalibrationId:true);}catch(InvalidDataException){wildcardProofDenied=true;}Check(wildcardProofDenied,"Private proof must name its application.");
        bool nonAutomaticDenied=false;try{NativeRoutePolicyPublisher.Validate([wireRule with{Route="vpn",Addresses=[],CalibrationId=proofA.CalibrationId}],allowPrivateCalibrationId:true);}catch(InvalidDataException){nonAutomaticDenied=true;}Check(nonAutomaticDenied,"A non-automatic route accepted a private proof.");
        var oldCore=new NativeCoreRecovery(()=>throw new Exception("must not start"),_=>Task.FromResult(false));
        var oldPublisher=new NativeRoutePolicyPublisher(null!,oldCore,"fixture",1);bool oldCoreDenied=false;
        var oldObservation=one with{Application=@"C:\app.exe"};
        try{await oldPublisher.CalibrateAsync(oldObservation,CancellationToken.None);}catch(IOException){oldCoreDenied=true;}Check(oldCoreDenied,"An old core accepted automatic calibration.");
        bool probeCalled=false;oldCoreDenied=false;
        try{await oldPublisher.CalibrateZapretAsync(oldObservation,_=>{probeCalled=true;throw new Exception("must not probe");},CancellationToken.None,()=>true);}catch(IOException){oldCoreDenied=true;}
        Check(oldCoreDenied&&!probeCalled,"An old core started a Zapret probe.");
        var forcedJson=System.Text.Json.JsonSerializer.SerializeToElement(wireRule with{Forced=true});
        Check(!automaticJson.TryGetProperty("Forced",out _)&&forcedJson.GetProperty("Forced").GetBoolean(),"Forced routing must be explicit without breaking older automatic policy messages.");
        bool forcedRejected=false;try{NativeRoutePolicyPublisher.Validate([wireRule with{Forced=true}]);}catch(InvalidDataException){forcedRejected=true;}
        Check(forcedRejected,"An unpersisted forced policy must not be accepted by user RPC.");
        var mutableAddresses=new[]{"1.1.1.1"};var mutableRules=new[]{wireRule with{Addresses=mutableAddresses}};
        var snapshot=NativeRoutePolicyPublisher.SnapshotRequest(new NativePolicyRequest(new string('a',32),mutableRules));
        mutableAddresses[0]="127.0.0.1";mutableRules[0]=wireRule with{Forced=true};
        Check(snapshot.Rules[0].Addresses[0]=="1.1.1.1"&&!snapshot.Rules[0].Forced,"Policy snapshot must not follow later caller mutations.");
        foreach(var oversized in new[]{new NativePolicyRequest(new string('a',32),Enumerable.Repeat(wireRule,129).ToArray()),new NativePolicyRequest(new string('a',32),[wireRule with{Addresses=Enumerable.Repeat("1.1.1.1",5).ToArray()}])})
        {bool denied=false;try{NativeRoutePolicyPublisher.SnapshotRequest(oversized);}catch(InvalidDataException){denied=true;}Check(denied,"Oversized inputs must be refused before snapshot allocation.");}
        bool hugeDomainDenied=false;try{NativeRoutePolicyPublisher.Validate([wireRule with{Domain=new string('a',10000)}]);}catch(InvalidDataException){hugeDomainDenied=true;}
        Check(hugeDomainDenied,"Oversized domains must be refused before normalization.");
        var service=(NativeServiceRoute)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(NativeServiceRoute));
        bool invalidEpochDenied=false;try{service.RequestPolicy(new NativePolicyRequest("bad",[wireRule]),null);}catch(InvalidDataException){invalidEpochDenied=true;}
        Check(invalidEpochDenied,"The service must reject an invalid epoch before queuing or touching session state.");
        var wildcard=wireRule with{Domain="ONE.EXAMPLE.",Application="",Route="blocked",Addresses=[]};
        var otherApplication=wireRule with{Application=@"C:\other.exe"};
        Check(NativePolicyComposition.Compose([wildcard],[wireRule,otherApplication],DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Length==1,"Manual wildcard must suppress exact automatic routes for every app.");
        var specific=wildcard with{Application=wireRule.Application,Route="vpn"};
        var combined=NativePolicyComposition.Compose([specific],[wireRule,otherApplication],DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Check(combined.Length==2&&combined.Any(r=>r.Application==otherApplication.Application&&r.Route=="direct")&&combined.Any(r=>r.Route=="vpn"),"Manual app rule must suppress only its matching automatic rule.");
        var udp=wireRule with{Network="udp"};
        Check(NativePolicyComposition.Compose([wildcard],[udp],DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Length==2,"A manual TCP rule must not override UDP.");
        bool wildcardAutoRefused=false;try{NativePolicyComposition.Compose([],[wireRule with{Application=""}],DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());}catch(InvalidDataException){wildcardAutoRefused=true;}
        Check(wildcardAutoRefused,"An automatic wildcard must not extend direct access to every application.");
        var shared=new[]{"1.1.1.1"};var owned=NativePolicyComposition.Compose([],[wireRule with{Addresses=shared}],DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());shared[0]="127.0.0.1";
        Check(owned[0].Addresses[0]=="1.1.1.1","Candidate policy must own its address arrays.");
        var full=Enumerable.Range(0,128).Select(i=>wildcard with{Domain=$"domain{i}.example"}).ToArray();
        Check(NativePolicyComposition.Compose(full,[wireRule],DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Length==128,"Manual capacity must not be displaced by automatic entries.");
        var acknowledged=System.Text.Json.JsonSerializer.SerializeToElement(new{policyRevision=2,policyDigest="ABC"});
        NativeCoreBinding.VerifyPolicyAck(acknowledged,2,"abc");
        foreach(var invalid in new[]{System.Text.Json.JsonSerializer.SerializeToElement(new{policyRevision=1,policyDigest="abc"}),System.Text.Json.JsonSerializer.SerializeToElement(new{policyRevision=2,policyDigest="bad"}),System.Text.Json.JsonSerializer.SerializeToElement(new{policyRevision=2})})
        {bool denied=false;try{NativeCoreBinding.VerifyPolicyAck(invalid,2,"abc");}catch(IOException){denied=true;}Check(denied,"Mismatched or incomplete policy acknowledgement must fault the control channel.");}
        var permission=new NativeDirectPermission(@"C:\core.exe","1.1.1.1",443,"tcp",1);
        var scopeAck=System.Text.Json.JsonSerializer.SerializeToElement(new{policyRevision=2,scope=new{Address="1.1.1.1",Network="tcp",Port=443}});
        NativeCoreBinding.VerifyScopeAck(scopeAck,2,permission);
        foreach(var invalid in new[]{System.Text.Json.JsonSerializer.SerializeToElement(new{policyRevision=1,scope=new{Address="1.1.1.1",Network="tcp",Port=443}}),System.Text.Json.JsonSerializer.SerializeToElement(new{policyRevision=2,scope=new{Address="8.8.8.8",Network="tcp",Port=443}}),System.Text.Json.JsonSerializer.SerializeToElement(new{policyRevision=2,scope=new{Address="1.1.1.1",Network="tcp"}})})
        {bool denied=false;try{NativeCoreBinding.VerifyScopeAck(invalid,2,permission);}catch(IOException){denied=true;}Check(denied,"Mismatched or incomplete scope acknowledgement must be refused.");}
        foreach(bool retired in new[]{false,true})NativeCoreBinding.VerifyScopeAck(System.Text.Json.JsonSerializer.SerializeToElement(new{policyRevision=2,scope=new{Address="1.1.1.1",Network="tcp",Port=443},scopeRetired=retired}),2,permission,true);
        NativeCoreBinding.VerifyScopeAck(scopeAck,2,permission,true); // omitted false in the existing Go protocol
        var malformedRetire=System.Text.Json.JsonSerializer.SerializeToElement(new{policyRevision=2,scope=new{Address="1.1.1.1",Network="tcp",Port=443},scopeRetired="true"});
        bool retirementDenied=false;try{NativeCoreBinding.VerifyScopeAck(malformedRetire,2,permission,true);}catch(IOException){retirementDenied=true;}
        Check(retirementDenied,"Malformed scope retirement must be refused.");
        // Uninitialized session deliberately has no controller or OS handles:
        // malformed requests must fail before any automatic-controller access.
        var preflight=(NativeGuardedSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(NativeGuardedSession));
        foreach(var request in new[]{new NativePolicyRequest("bad",[wireRule]),new NativePolicyRequest(new string('a',32),[wireRule with{Forced=true}]),new NativePolicyRequest(new string('a',32),[proofA]),new NativePolicyRequest(new string('a',32),null!)})
        {
            bool refused=false;try{await preflight.PublishPolicyAsync(request,CancellationToken.None);}catch(InvalidDataException){refused=true;}
            Check(refused,"Invalid manual policy reached the automatic controller.");
        }
        using(var canceledRequest=new CancellationTokenSource())
        {
            canceledRequest.Cancel();bool refused=false;
            try{await preflight.PublishPolicyAsync(new NativePolicyRequest(new string('a',32),[wireRule]),canceledRequest.Token);}catch(OperationCanceledException){refused=true;}
            Check(refused,"Already-cancelled policy reached the automatic controller.");
        }
        // Exercise the real session entry point without starting a core or WFP.
        // A failed prerequisite must not cancel the automatic controller.
        var session=(NativeGuardedSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(NativeGuardedSession));
        var binding=new NativeCoreBinding("fixture","fixture","fixture","fixture","fixture");
        Check(binding.ConnectorCounts==null,"Absent connector must not fabricate traffic counts.");
        typeof(NativeCoreBinding).GetProperty("SupportsPolicy",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(binding,true);
        var recovery=new NativeCoreRecovery(()=>binding,_=>Task.FromResult(true));
        Check(recovery.ConnectorCounts==null,"Absent binding must not fabricate traffic counts.");
        typeof(NativeCoreRecovery).GetField("binding",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(recovery,binding);
        typeof(NativeGuardedSession).GetField("core",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(session,recovery);
        using var autoStop=new CancellationTokenSource();
        typeof(NativeGuardedSession).GetField("automaticStop",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(session,autoStop);
        string missing=Path.Combine(Path.GetTempPath(),"cita-missing-config-"+Guid.NewGuid().ToString("N"));
        typeof(NativeGuardedSession).GetField("configPath",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(session,missing);
        bool missingRejected=false;try{await session.PublishPolicyAsync(new NativePolicyRequest(new string('a',32),[wireRule]),CancellationToken.None);}catch(FileNotFoundException){missingRejected=true;}
        Check(missingRejected&&!autoStop.IsCancellationRequested,"Missing configuration must not stop automatic routing.");
        string temporary=Path.Combine(Path.GetTempPath(),"cita-policy-preflight-"+Guid.NewGuid().ToString("N")+".json");
        bool tempCreated=false;
        try
        {
            await using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {tempCreated=true;byte[] bytes=System.Text.Encoding.UTF8.GetBytes("{\"outbounds\":[{\"tag\":\"native-direct\",\"type\":\"direct\",\"bind_interface\":\"cita-nonexistent-"+Guid.NewGuid().ToString("N")+"\"}]}");await file.WriteAsync(bytes);}
            typeof(NativeGuardedSession).GetField("configPath",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(session,temporary);
            bool interfaceRejected=false;try{await session.PublishPolicyAsync(new NativePolicyRequest(new string('a',32),[wireRule]),CancellationToken.None);}catch(InvalidOperationException){interfaceRejected=true;}
            Check(interfaceRejected&&!autoStop.IsCancellationRequested,"Unavailable interface must not stop automatic routing.");
        }
        finally{if(tempCreated)File.Delete(temporary);}
        // The manual-policy lock may be held by another operation. A request
        // cancelled while waiting must not cancel the live automatic controller.
        var interfaces=System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
        var liveNic=interfaces.FirstOrDefault(n=>n.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up&&n.GetIPProperties().GetIPv4Properties()!=null&&interfaces.Count(x=>x.Name==n.Name)==1);
        if(liveNic!=null)
        {
            string path=Path.Combine(Path.GetTempPath(),"cita-policy-lock-"+Guid.NewGuid().ToString("N")+".json");
            using var gate=new SemaphoreSlim(0,1);using var policyStop=new CancellationTokenSource();using var requestStop=new CancellationTokenSource();
            bool created=false;
            try
            {
                await using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {created=true;var bytes=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new{outbounds=new[]{new{tag="native-direct",type="direct",bind_interface=liveNic.Name}}});await file.WriteAsync(bytes);}
                typeof(NativeGuardedSession).GetField("configPath",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(session,path);
                typeof(NativeGuardedSession).GetField("policyGate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(session,gate);
                typeof(NativeGuardedSession).GetField("policyStop",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(session,policyStop);
                var pending=session.PublishPolicyAsync(new NativePolicyRequest(new string('a',32),[wireRule]),requestStop.Token);
                await Task.Delay(100);Check(!pending.IsCompleted,"Manual request did not wait for the policy lock.");
                requestStop.Cancel();bool canceledWait=false;try{await pending;}catch(OperationCanceledException){canceledWait=true;}
                Check(canceledWait&&!autoStop.IsCancellationRequested,"Cancelled manual policy lock wait stopped automatic routing.");
                LockWaitCovered=true;
            }
            finally{if(created)File.Delete(path);}
        }
        var rotated=new NativeObservedDestination("ONE.EXAMPLE.","APP","8.8.8.8",443);
        var otherPort=new NativeObservedDestination("one.example","app","1.1.1.1",8443);
        var otherApp=new NativeObservedDestination("one.example","other","1.1.1.1",443);
        var otherDomain=new NativeObservedDestination("two.example","app","1.1.1.1",443);
        Check(NativeRoutePolicyPublisher.SameAutomaticScope(one,rotated),"A verified replacement address must replace the same case-insensitive scope.");
        Check(!NativeRoutePolicyPublisher.SameAutomaticScope(one,otherPort)&&!NativeRoutePolicyPublisher.SameAutomaticScope(one,otherApp)&&!NativeRoutePolicyPublisher.SameAutomaticScope(one,otherDomain),"Rotating one address must preserve other port, application and domain scopes.");
        var networkGeneration=new NativeNetworkGeneration();long previousNetwork=networkGeneration.Current;networkGeneration.Changed();Check(networkGeneration.Current!=previousNetwork,"A network event must invalidate evidence even when addresses later return to their old values.");
        var goodProbe=NativePathProbeResult.Read(System.Text.Json.JsonSerializer.SerializeToElement(new{probeReachable=true,probeResult="reachable"}));
        Check(goodProbe.Reachable&&goodProbe.Reason=="reachable","Confirmed probe category lost.");
        var legacyProbe=NativePathProbeResult.Read(System.Text.Json.JsonSerializer.SerializeToElement(new{probeReachable=false}));
        Check(!legacyProbe.Reachable&&legacyProbe.Reason=="legacy-unclassified","Older core results must remain unclassified.");
        var certificate=NativePathProbeResult.Read(System.Text.Json.JsonSerializer.SerializeToElement(new{probeReachable=false,probeResult="tls-certificate"}));
        Check(!certificate.Reachable&&certificate.Reason=="tls-certificate","Certificate errors cannot authorize a route.");
        foreach(var reply in new[]{new{probeReachable=true,probeResult="tls-certificate"},new{probeReachable=true,probeResult="unknown"}}){bool denied=false;try{NativePathProbeResult.Read(System.Text.Json.JsonSerializer.SerializeToElement(reply));}catch(InvalidDataException){denied=true;}Check(denied,"Contradictory or unknown probe results must be refused.");}
        bool malformedProbe=false;try{NativePathProbeResult.Read(System.Text.Json.JsonSerializer.SerializeToElement(new{probeReachable="true",probeResult="reachable"}));}catch(InvalidDataException){malformedProbe=true;}Check(malformedProbe,"Malformed reachability must not authorize a route.");
        var two=new NativeObservedDestination("two.example","app","8.8.8.8",443);
        var unknown=new NativeObservedDestination("cdn.example","","1.1.1.1",443);
        var schedule=new NativeCalibrationSchedule();
        Check(schedule.Select([unknown],0)==null,"Unknown applications must not generate domain-wide automatic direct rules.");
        Check(schedule.Select([one,two],0)==one,"Fresh observations must be selected deterministically.");
        schedule.Mark(one,0);Check(schedule.Select([one,two],1)==two,"A recently checked destination must not starve another.");
        schedule.Mark(two,1);Check(schedule.Select([one,two],59999)==null,"Checks must be throttled until their deadline.");
        Check(schedule.Select([one,two],60000)==one,"A destination must become eligible after its deadline.");
        schedule.Select([two],60001);Check(schedule.Count==1,"Retired observations must not accumulate.");
        schedule.Clear();Check(schedule.Count==0&&schedule.Select([two],0)==two,"A network or process generation reset must discard previous timing.");
        var budget=new NativeCalibrationBudget();for(int i=0;i<32;i++)Check(budget.TryAcquire(0),"The bounded budget refused an allowed check.");
        Check(!budget.TryAcquire(0)&&!budget.TryAcquire(-1),"Bursts and clock rollback must not bypass the budget.");
        Check(budget.TryAcquire(60000),"A later budget window must recover.");
        var wake=new NativeObservationWakeup();Check(wake.Observe([one]),"A new destination must wake the controller.");
        Check(!wake.Observe([one])&&wake.Pending==1,"Duplicate observations must coalesce.");
        Check(await wake.WaitAsync(CancellationToken.None,TimeSpan.FromMilliseconds(50)),"Pending wakeup was lost.");
        Check(!await wake.WaitAsync(CancellationToken.None,TimeSpan.FromMilliseconds(10)),"An unchanged observation must not cause a busy loop.");
        wake.Wake();wake.Wake();Check(wake.Pending==1&&await wake.WaitAsync(CancellationToken.None,TimeSpan.FromMilliseconds(50)),"Network events must wake calibration without creating a burst.");
        wake.Observe([two]);wake.Reset();Check(wake.Pending==0&&wake.Observe([two]),"Reset must clear wakeups and cached observations.");
        wake.Reset();using var canceled=new CancellationTokenSource();canceled.Cancel();
        try{await wake.WaitAsync(canceled.Token);throw new InvalidOperationException("Canceled controller continued waiting.");}catch(OperationCanceledException){}
        bool rejected=false;try{schedule.Select(Enumerable.Repeat(one,129).ToArray(),0);}catch(InvalidDataException){rejected=true;}
        Check(rejected,"Oversized observation bursts must be refused.");
        rejected=false;try{NativeRoutePolicyPublisher.Validate([new NativeDomainRule("example.com",@"C:\app.exe","tcp","dpi",443,["1.1.1.1"],DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds())]);}catch(InvalidDataException){rejected=true;}
        Check(rejected,"An unimplemented DPI path must not be published as plain Direct.");
        var owner=System.Text.Json.JsonSerializer.SerializeToElement(new{state="RunningUnverified"});
        var timeout=System.Text.Json.JsonSerializer.SerializeToElement(new{success=false,stage="UdpQueries",dnsFailure="ReadTimeout"});
        var malformed=System.Text.Json.JsonSerializer.SerializeToElement(new{success=false,stage="UdpQueries",dnsFailure="InvalidFrame"});
        var stopped=System.Text.Json.JsonSerializer.SerializeToElement(new{state="Stopped"});
        Check(NativeProbePolicy.RetryAnonymousUdp(timeout,owner,1),"An anonymous UDP timeout may receive one bounded retry.");
        Check(!NativeProbePolicy.RetryAnonymousUdp(timeout,owner,2)&&!NativeProbePolicy.RetryAnonymousUdp(timeout,stopped,1)&&!NativeProbePolicy.RetryAnonymousUdp(malformed,owner,1),"Retry must not conceal protocol errors, inactive owners, or exceed its budget.");
        Check(NativeServiceRoute.MayReportFailure("a","a",true,true,"Preparing","Initializing"),"Current operation failure must remain visible.");
        Check(!NativeServiceRoute.MayReportFailure("b","a",true,true,"Preparing","Connecting"),"A stale failure must not replace a new operation.");
        Check(!NativeServiceRoute.MayReportFailure("a","a",false,true,"Stopping","Connecting"),"A late connection failure must not cancel Stop.");
        Check(!NativeServiceRoute.MayReportFailure("a","a",false,false,"Stopping","Importing"),"A late import failure must not cancel Stop.");
        Check(NativeServiceRoute.MayReportFailure("a","a",false,false,"Stopping","Stopping"),"A real cleanup failure must remain visible.");
        long tick=100;var trace=new NativeDecisionTrace(()=>tick,()=>1234);
        var privateObservation=new NativeObservedDestination("example.com",@"C:\private\browser.exe","1.1.1.1",443);
        trace.Record("observed",privateObservation);Check(!trace.Snapshot().Enabled&&trace.Snapshot().Entries.Length==0,"Trace must be off by default.");
        trace.Enable(true);trace.Record("observed",privateObservation);var entry=trace.Snapshot().Entries.Single();
        Check(entry.Application=="browser.exe"&&entry.Domain=="example.com"&&entry.UnixMilliseconds==1234,"Trace must only keep bounded display identity.");
        trace.Record("unknown",privateObservation);Check(trace.Snapshot().Entries.Length==1,"Unknown events must not be recorded.");
        for(int i=0;i<80;i++)trace.Record("started",privateObservation);Check(trace.Snapshot().Entries.Length==24,"Trace must have a fixed event budget.");
        tick+=600000;Check(trace.Snapshot().Entries.Length==0,"Old trace entries must expire with monotonic time.");
        trace.Record("applied",privateObservation);trace.Enable(false);Check(trace.Snapshot().Entries.Length==0&&!trace.Snapshot().Enabled,"Opt-out must clear memory immediately.");
    }
}
