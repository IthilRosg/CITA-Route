using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using RoutePilot.Agent;

internal static class NativePolicySessionLab
{
    static void Check(bool value){if(!value)throw new IOException("Native policy route proof failed.");}
    static bool RecordTunProof(Dictionary<string,object?> report,JsonElement probe,string expected)
    {
        bool success=probe.TryGetProperty("success",out var ok)&&ok.ValueKind==JsonValueKind.True;
        bool exitMatched=probe.TryGetProperty("egressHash",out var hash)&&hash.ValueKind==JsonValueKind.String&&hash.GetString()==expected;
        if(!success||!exitMatched){
            string Safe(string name){if(!probe.TryGetProperty(name,out var value)||value.ValueKind!=JsonValueKind.String)return "";string text=value.GetString()??"";return text.Length<=64&&text.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_')?text:"unclassified";}
            int Number(string name)=>probe.TryGetProperty(name,out var value)&&value.TryGetInt32(out int number)?number:0;
            report["tunFailure"]=new{stage=Safe("stage"),dnsFailure=Safe("dnsFailure"),success,exitMatched,udpBefore=Number("udpBefore"),udpAfter=Number("udpAfter"),tcpFromTun=probe.TryGetProperty("tcpFromTun",out var tcp)&&tcp.ValueKind==JsonValueKind.True};
        }
        return success&&exitMatched;
    }
    // Test-only selector: a negative probe may try the next path; cancellation
    // and unexpected control/guard errors propagate instead of authorizing it.
    static async Task<string> SelectLabPath(Func<CancellationToken,Task<bool>> direct,Func<CancellationToken,Task<bool>> dpi,CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();if(await direct(stop))return "direct";
        stop.ThrowIfCancellationRequested();if(await dpi(stop))return "dpi";
        stop.ThrowIfCancellationRequested();return "vpn";
    }
    internal static async Task<int> Run(string root,string run,bool automatic=false,bool performance=false,bool coverage=false,bool pages=false,bool dnsFailover=false,bool dnsOutage=false,bool dpi=false,bool livePaths=false,bool tcpSegments=false,bool mediaPages=false)
    {
        string stage="prepare";var report=new Dictionary<string,object?>{{"success",false}};NativeGuardedSession? runtime=null;
        try
        {
            if(dpi)Environment.SetEnvironmentVariable("CITA_NATIVE_DPI_LAB","1");
            if(tcpSegments){if(!dpi)throw new InvalidOperationException("TCP segmentation requires DPI laboratory.");Environment.SetEnvironmentVariable("CITA_NATIVE_DPI_STRATEGY","tcp");}
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(100));var stop=deadline.Token;
            // The outer guest-only runner arms independent cleanup before calling
            // this helper; the guard also verifies the exact guest on every call.
            if(!root.StartsWith(@"C:\CitaLab\NativeAwg\DirectGuard-",StringComparison.OrdinalIgnoreCase)||Path.GetFileName(root)!="DirectGuard-"+run)throw new IOException("Wrong policy fixture root.");
            string parent=Path.GetDirectoryName(root)!;string text=await File.ReadAllTextAsync(Path.Combine(parent,"private","peer.conf"),stop);
            if(dnsFailover){var dns=System.Text.RegularExpressions.Regex.Match(text,@"(?mi)^\s*DNS\s*=\s*([^\r\n]+)");if(!dns.Success)throw new IOException("Profile DNS fixture missing.");text=text.Remove(dns.Index,dns.Length).Insert(dns.Index,"DNS = 192.0.2.53, "+dns.Groups[1].Value.Trim());report["unavailableFirstProfileDnsFixture"]=true;}
            if(dnsOutage){var dns=System.Text.RegularExpressions.Regex.Match(text,@"(?mi)^\s*DNS\s*=\s*([^\r\n]+)");if(!dns.Success)throw new IOException("Profile DNS fixture missing.");text=text.Remove(dns.Index,dns.Length).Insert(dns.Index,"DNS = 192.0.2.53");report["allProfileDnsUnavailableFixture"]=true;}
            var match=System.Text.RegularExpressions.Regex.Match(text,@"(?mi)^\s*Endpoint\s*=\s*([^\r\n]+)");if(!System.Net.IPEndPoint.TryParse(match.Groups[1].Value.Trim(),out var endpoint))throw new IOException("Pinned peer required.");
            using var reference=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(parent,"..","server-egress-reference.json"),stop));string expected=reference.RootElement.GetProperty("serverEgressHash").GetString()!;
            var nic=NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.OperationalStatus==OperationalStatus.Up&&n.GetIPProperties().GatewayAddresses.Any(g=>g.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork));
            var policy=JsonSerializer.SerializeToElement(new{Vpn=new[]{"0.0.0.0/0","::/0"},Direct=new[]{"10.0.0.0/8","172.16.0.0/12","192.168.0.0/16","100.64.0.0/10","fc00::/7","fe80::/10","ff00::/8"}});
            var permission=new NativeEndpointPermission(Path.Combine(root,"native-owner.exe"),endpoint!.Address.ToString(),checked((ushort)endpoint.Port));var planner=new NativeWfpGuard(Path.Combine(root,"native-guard.exe"),run,policy,endpoint:permission);await planner.PlanAsync(stop);
            string owner=Guid.NewGuid().ToString("N"),configPath=Path.Combine(root,"policy-core.json");
            var config=new{log=new{disabled=true},inbounds=new[]{new{type="tun",tag="native-tun",interface_name="Cita-NativeLab",address=new[]{"172.30.127.1/30"},route_address=planner.Prefixes.Where(x=>!x.Contains(':')).ToArray(),route_exclude_address=new[]{endpoint.Address+"/32"},auto_route=true,strict_route=false,dns_mode="disabled",stack="gvisor",mtu=1400}},outbounds=new object[]{new{type="cita_native_lab",tag="native-vpn",owner},new{type="direct",tag="native-direct",bind_interface=nic.Name},new{type="direct",tag="native-dpi",bind_interface=nic.Name}},route=new{final="native-vpn",find_process=true,rules=new[]{new{action="sniff",timeout="300ms"}}}};
            await File.WriteAllTextAsync(configPath,JsonSerializer.Serialize(config),stop);
            runtime=new NativeGuardedSession(Path.Combine(root,"native-owner.exe"),Path.Combine(root,"native-core.exe"),Path.Combine(root,"native-guard.exe"),configPath,owner,run,policy,expected,afterStart:async(binding,ct)=>{await File.WriteAllTextAsync(Path.Combine(root,"core-pid.json"),JsonSerializer.Serialize(new{pid=binding.ProcessId}),ct);},endpoint:permission,adaptive:automatic);
            if(automatic&&dpi)runtime.LabSimulateDirectTimeout=true;
            var timer=Stopwatch.StartNew();stage="initialize";await runtime.InitializeAsync(stop);report["initializeMs"]=timer.ElapsedMilliseconds;
            var spec=JsonSerializer.SerializeToElement(new{Session=Guid.NewGuid().ToString("N"),Generation=1UL,Token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),Config=text});timer.Restart();stage="connect";await runtime.ConnectAsync(spec,stop);report["connectMs"]=timer.ElapsedMilliseconds;text="";
            if(livePaths)
            {
                var results=new List<object>();int compared=0;
                foreach(string domain in new[]{"www.youtube.com","www.instagram.com","telegram.org","www.wikipedia.org"})
                {
                    stage="live-observation";var seed=await NativeCoverageLab.Fetch(domain,1,stop);
                    NativeObservedDestination? observation=null;for(int i=0;i<30&&observation==null;i++){observation=runtime.Core.Observations.FirstOrDefault(x=>x.Domain==domain&&!string.IsNullOrWhiteSpace(x.Application));if(observation==null)await Task.Delay(100,stop);}
                    if(observation==null){results.Add(new{domain,observation=false,seed});continue;}
                    stage="live-direct";var priorDirect=runtime.Core.LastPathProbe;bool directPassed=await runtime.CalibrateDirectForLabAsync(observation,stop);string directReason=ReferenceEquals(priorDirect,runtime.Core.LastPathProbe)?"unavailable":runtime.Core.LastPathProbe?.Reason??"unavailable";
                    await runtime.PublishPolicyAsync(new NativePolicyRequest(Guid.NewGuid().ToString("N"),[]),stop);
                    stage="live-dpi";var priorDpi=runtime.Core.LastPathProbe;bool fragmented=await runtime.CalibrateDpiForLabAsync(observation,stop);string dpiReason=ReferenceEquals(priorDpi,runtime.Core.LastPathProbe)?"unavailable":runtime.Core.LastPathProbe?.Reason??"unavailable";
                    await runtime.PublishPolicyAsync(new NativePolicyRequest(Guid.NewGuid().ToString("N"),[]),stop);
                    results.Add(new{domain,observation=true,direct=directPassed,directReason,dpi=fragmented,dpiReason,seed});compared++;
                }
                stage="live-cleanup";for(int i=0;i<30&&runtime.Guard.DirectPermits!=0;i++){await runtime.SweepScopesAsync(stop);await Task.Delay(100,stop);}Check(runtime.Guard.DirectPermits==0&&await runtime.Guard.VerifyAsync(stop));
                report["livePaths"]=results;report["comparedCount"]=compared;report["scope"]="VM anonymous HTTPS root probes, independent Direct and fragmented TLS, no cookies or request replay; not full site availability";Check(compared>0);
                await runtime.DisconnectAsync(stop);report["success"]=true;return 0;
            }
            if(automatic)
            {
                if(!mediaPages){
                var traceLock=new object();var trace=new Dictionary<string,long>();NativeObservedDestination? target=null;int tracePid=runtime.Core.ProcessId;
                var applied=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                runtime.Core.CalibrationObserver=(phase,observed,pid,at)=>{
                    if(pid!=tracePid||observed.Domain!="cloudflare-dns.com"||observed.Address!="1.1.1.1"||observed.Port!=443)return;
                    lock(traceLock){if(phase=="observed"&&target==null)target=observed;if(target!=observed)return;trace.TryAdd(phase,at);if(phase=="applied")applied.TrySetResult();}
                };
                long probeStarted=Environment.TickCount64;
                timer.Restart();stage="automatic-observation";var first=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,domain:true);Check(first.GetProperty("success").GetBoolean());report["firstUnknownDomainWorks"]=true;report["firstControlProbeMs"]=timer.ElapsedMilliseconds;timer.Restart();
                await applied.Task.WaitAsync(TimeSpan.FromSeconds(25),stop);report["firstAutomaticDecisionWaitMs"]=timer.ElapsedMilliseconds;
                lock(traceLock){long seen=trace["observed"],started=trace["started"],probed=trace[dpi?"dpi-probe-passed":"probe-passed"],confirmed=trace["applied"];Check(probeStarted<=seen&&seen<=started&&started<=probed&&probed<=confirmed&&runtime.Core.ProcessId==tracePid);
                    report["targetCalibrationTiming"]=new{matchedTarget=true,sameCore=true,probeStartToObservedMs=seen-probeStarted,queueMs=started-seen,scopeAndDirectProbeMs=probed-started,publicationMs=confirmed-probed,observedToAppliedMs=confirmed-seen};}
                runtime.Core.CalibrationObserver=null;
                stage=dpi?"automatic-dpi":"automatic-direct";var next=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,domain:true);Check(next.GetProperty("success").GetBoolean());for(int i=0;i<30&&runtime.Core.PolicyDirectDials==0;i++)await Task.Delay(100,stop);Check(runtime.Core.PolicyDirectDials>0);report[dpi?"automaticDpiAfterSimulatedDirectFailure":"automaticDomainSelectionVerified"]=true;
                }else report["mediaPolicyScope"]="Automatic policy enabled; pages measured without requiring unrelated direct calibration to succeed.";
                if(performance){stage="performance-baseline";report["performance"]=await NativePerformanceLab.Measure(runtime,root,stop,report);}
                if(coverage){stage="domain-coverage";report["coverage"]=await NativeCoverageLab.Measure(runtime,root,stop);}
                if(pages){stage="page-resources";report["pages"]=await NativePageLab.Measure(runtime,root,stop,mediaPages);}
                if(dnsOutage){stage="dns-outage";var outage=await NativeCoverageLab.MeasureDnsOutage(runtime,root,stop);report["dnsOutage"]=outage;Check(JsonSerializer.SerializeToElement(outage).GetProperty("success").GetBoolean());}
                timer.Restart();await runtime.DisconnectAsync(stop);report["disconnectMs"]=timer.ElapsedMilliseconds;report["success"]=true;return 0;
            }
            string epoch=Guid.NewGuid().ToString("N");NativePolicyRequest Rules(string route)=>new(epoch,[new("cloudflare-dns.com","","tcp",route,443,route is "direct" or "dpi"?["1.1.1.1"]:[],DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds())]);
            if(dpi)
            {
                foreach(string reason in new[]{"dial-failed","dial-timeout","tls-failed","tls-timeout","http-write-failed","http-write-timeout","http-header-failed","http-header-timeout","http-body-failed","http-body-timeout"})Check(NativeGuardedSession.EligibleDpiFailure(new(false,reason)));
                foreach(string reason in new[]{"tls-certificate","http-refused","http-body-empty","legacy-unclassified","reachable"})Check(!NativeGuardedSession.EligibleDpiFailure(new(false,reason)));
                Check(!NativeGuardedSession.EligibleDpiFailure(null)&&!NativeGuardedSession.EligibleDpiFailure(new(true,"tls-failed")));report["dpiFailureEligibilityChecked"]=true;
                stage="retain-vpn-during-dpi";var heldDpi=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,async()=>await runtime.PublishPolicyAsync(Rules("dpi"),stop),domain:true);
                Check(heldDpi.GetProperty("success").GetBoolean()&&heldDpi.GetProperty("reusedTcp").GetBoolean()&&heldDpi.GetProperty("egressHash").GetString()==expected);report["vpnFlowRetained"]=true;
                stage="fresh-dpi";var fresh=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,domain:true);Check(fresh.GetProperty("success").GetBoolean());
                for(int i=0;i<40&&runtime.Core.PolicyDirectDials==0;i++)await Task.Delay(100,stop);Check(runtime.Core.PolicyDirectDials>0);report["dpiFlowEstablished"]=true;
                stage="dpi-withdrawal";var heldDpiFlow=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,async()=>{await runtime.PublishPolicyAsync(new(epoch,[]),stop);await runtime.SweepScopesAsync(stop);Check(runtime.Guard.DirectPermits==1);},domain:true);
                Check(heldDpiFlow.GetProperty("success").GetBoolean()&&heldDpiFlow.GetProperty("reusedTcp").GetBoolean());report["dpiFlowRetained"]=true;
                for(int i=0;i<30&&runtime.Guard.DirectPermits!=0;i++){await runtime.SweepScopesAsync(stop);await Task.Delay(100,stop);}Check(runtime.Guard.DirectPermits==0&&await runtime.Guard.VerifyAsync(stop));report["dpiPermitRetired"]=true;
                stage="dpi-probe-observation";var seed=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,domain:true);Check(RecordTunProof(report,seed,expected));
                NativeObservedDestination? observed=null;for(int i=0;i<40&&observed==null;i++){observed=runtime.Core.Observations.FirstOrDefault(x=>x.Domain=="cloudflare-dns.com"&&x.Address=="1.1.1.1"&&!string.IsNullOrWhiteSpace(x.Application));if(observed==null)await Task.Delay(100,stop);}
                Check(observed!=null);stage="stale-direct-probe";long Revision()=>JsonSerializer.SerializeToElement(runtime.Snapshot).GetProperty("domainPolicyRevision").GetInt64();long previousRevision=Revision();
                Check(!await runtime.CalibrateDirectForLabAsync(observed!,stop,()=>false));
                Check(runtime.Core.LastPathProbe is {Reachable:true,Reason:"reachable"}&&Revision()==previousRevision);
                for(int i=0;i<30&&runtime.Guard.DirectPermits!=0;i++){await runtime.SweepScopesAsync(stop);await Task.Delay(100,stop);}Check(runtime.Guard.DirectPermits==0);
                var staleFallback=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,domain:true);Check(staleFallback.GetProperty("success").GetBoolean()&&staleFallback.GetProperty("egressHash").GetString()==expected);report["staleDirectProbeKeptVpn"]=true;
                stage="cancel-after-direct-probe";bool cancelledProbe=false;try{await runtime.CalibrateDirectForLabAsync(observed!,stop,()=>throw new OperationCanceledException());}catch(OperationCanceledException){cancelledProbe=true;}
                Check(cancelledProbe&&Revision()==previousRevision);for(int i=0;i<30&&runtime.Guard.DirectPermits!=0;i++){await runtime.SweepScopesAsync(stop);await Task.Delay(100,stop);}Check(runtime.Guard.DirectPermits==0);report["cancelledProbeScopeRetired"]=true;
                stage="network-change-during-publication";
                foreach(var fixture in new[]{(failAt:2,throws:false),(failAt:3,throws:false),(failAt:3,throws:true)}){
                    int checks=0;Check(!await runtime.CalibrateDirectForLabAsync(observed!,stop,()=>{checks++;if(checks==fixture.failAt&&fixture.throws)throw new IOException("fixture");return checks<fixture.failAt;}));Check(checks==fixture.failAt);
                    for(int i=0;i<30&&runtime.Guard.DirectPermits!=0;i++){await runtime.SweepScopesAsync(stop);await Task.Delay(100,stop);}Check(runtime.Guard.DirectPermits==0);
                    var fallbackAfterChange=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,domain:true);
                    Check(fallbackAfterChange.GetProperty("success").GetBoolean()&&fallbackAfterChange.GetProperty("egressHash").GetString()==expected);
                }report["publicationChangeRolledBackToVpn"]=true;
                stage="cancel-after-policy-ack";using(var publicationCancel=CancellationTokenSource.CreateLinkedTokenSource(stop)){
                    int checks=0;bool accepted=await runtime.CalibrateDirectForLabAsync(observed!,publicationCancel.Token,()=>{if(++checks==3)publicationCancel.Cancel();return !publicationCancel.IsCancellationRequested;});
                    Check(!accepted&&checks==3);for(int i=0;i<30&&runtime.Guard.DirectPermits!=0;i++){await runtime.SweepScopesAsync(stop);await Task.Delay(100,stop);}Check(runtime.Guard.DirectPermits==0);
                    var fallbackAfterCancel=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,domain:true);Check(RecordTunProof(report,fallbackAfterCancel,expected));report["cancelAfterPolicyAckRolledBack"]=true;
                }
                stage="direct-probe";bool dpiCalled=false;
                var preferred=await SelectLabPath(ct=>runtime.CalibrateDirectForLabAsync(observed!,ct),ct=>{dpiCalled=true;return Task.FromResult(false);},stop);
                Check(preferred=="direct"&&!dpiCalled&&runtime.Core.LastPathProbe is {Reachable:true,Reason:"reachable"});report["independentDirectProbePassed"]=true;report["selectorPreferredConfirmedDirect"]=true;report["directProbeCategoryConfirmed"]=true;
                long directBefore=runtime.Core.PolicyDirectDials;var directFirst=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,domain:true);Check(directFirst.GetProperty("success").GetBoolean());for(int i=0;i<40&&runtime.Core.PolicyDirectDials<=directBefore;i++)await Task.Delay(100,stop);Check(runtime.Core.PolicyDirectDials>directBefore);report["confirmedDirectUsed"]=true;
                await runtime.PublishPolicyAsync(new(epoch,[]),stop);for(int i=0;i<30&&runtime.Guard.DirectPermits!=0;i++){await runtime.SweepScopesAsync(stop);await Task.Delay(100,stop);}Check(runtime.Guard.DirectPermits==0);
                stage="stale-dpi-probe";Check(!await runtime.CalibrateDpiForLabAsync(observed!,stop,()=>false));for(int i=0;i<30&&runtime.Guard.DirectPermits!=0;i++){await runtime.SweepScopesAsync(stop);await Task.Delay(100,stop);}Check(runtime.Guard.DirectPermits==0);report["staleDpiProbeRefused"]=true;
                stage="dpi-probe";var selected=await SelectLabPath(_=>Task.FromResult(false),ct=>runtime.CalibrateDpiForLabAsync(observed!,ct),stop);
                Check(selected=="dpi"&&runtime.Core.LastPathProbe is {Reachable:true,Reason:"reachable"});report["independentDpiProbePassed"]=true;report["selectorDpiAfterSimulatedDirectFailure"]=true;report["dpiProbeCategoryConfirmed"]=true;
                long before=runtime.Core.PolicyDirectDials;stage="dpi-probe-route";var routed=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,domain:true);Check(routed.GetProperty("success").GetBoolean());for(int i=0;i<40&&runtime.Core.PolicyDirectDials<=before;i++)await Task.Delay(100,stop);Check(runtime.Core.PolicyDirectDials>before);report["probePublishedDpiRoute"]=true;
                await runtime.PublishPolicyAsync(new(epoch,[]),stop);for(int i=0;i<30&&runtime.Guard.DirectPermits!=0;i++){await runtime.SweepScopesAsync(stop);await Task.Delay(100,stop);}Check(runtime.Guard.DirectPermits==0);report["probePermitRetired"]=true;
                stage="dpi-unobserved-refused";bool refused=false;var unobserved=observed! with{Application=observed!.Application+".unobserved"};
                try{refused=!await runtime.CalibrateDpiForLabAsync(unobserved,stop);}catch(InvalidDataException){refused=true;}
                Check(refused);await runtime.SweepScopesAsync(stop);Check(runtime.Guard.DirectPermits==0);report["unobservedProbeRefused"]=true;
                stage="vpn-after-refusal";Check(await SelectLabPath(_=>Task.FromResult(false),_=>Task.FromResult(false),stop)=="vpn");
                bool calledAfterError=false;try{await SelectLabPath(_=>Task.FromException<bool>(new IOException("fixture")),_=>{calledAfterError=true;return Task.FromResult(true);},stop);throw new IOException("Probe error suppressed.");}catch(IOException error)when(error.Message=="fixture"){}Check(!calledAfterError);
                using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();bool called=false;try{await SelectLabPath(_=>{called=true;return Task.FromResult(true);},_=>Task.FromResult(true),cancelled.Token);throw new IOException("Cancellation ignored.");}catch(OperationCanceledException){}Check(!called);}report["selectorFailsClosedOnErrorAndCancellation"]=true;
                var fallback=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,domain:true);Check(RecordTunProof(report,fallback,expected));report["vpnAfterRefusal"]=true;
                await runtime.DisconnectAsync(stop);report["success"]=true;return 0;
            }
            stage="retained-vpn-flow";var held=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,async()=>await runtime.PublishPolicyAsync(Rules("direct"),stop),domain:true);
            Check(held.GetProperty("success").GetBoolean()&&held.GetProperty("reusedTcp").GetBoolean()&&held.GetProperty("egressHash").GetString()==expected);report["publicationRetainsExistingVpnTcp"]=true;
            stage="fresh-direct-flow";var direct=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,domain:true);Check(direct.GetProperty("success").GetBoolean());
            for(int i=0;i<40&&runtime.Core.PolicyDirectDials==0;i++)await Task.Delay(100,stop);Check(runtime.Core.PolicyDirectDials>0);report["freshDomainUsesDirect"]=true;
            stage="held-direct-scope";
            var heldDirect=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,async()=>{
                await runtime.PublishPolicyAsync(new(epoch,[]),stop);await runtime.SweepScopesAsync(stop);
                Check(runtime.Guard.DirectPermits==1);report["liveDirectScopeRetained"]=true;
            },domain:true);
            Check(heldDirect.GetProperty("success").GetBoolean()&&heldDirect.GetProperty("reusedTcp").GetBoolean());report["withdrawalRetainsExistingDirectTcp"]=true;
            stage="closed-direct-scope";
            for(int i=0;i<30&&runtime.Guard.DirectPermits!=0;i++){await runtime.SweepScopesAsync(stop);await Task.Delay(100,stop);}
            Check(runtime.Guard.DirectPermits==0&&await runtime.Guard.VerifyAsync(stop));report["closedDirectScopeRevoked"]=true;
            stage="direct-reenable";await runtime.PublishPolicyAsync(Rules("direct"),stop);
            Check((await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,domain:true)).GetProperty("success").GetBoolean());report["retiredScopeReenabled"]=true;
            stage="idle-scope-churn";
            for(int i=0;i<6;i++){
                await runtime.PublishPolicyAsync(new(epoch,[new("idle.example.com","","tcp","direct",checked((ushort)(10443+i)),["1.1.1.1"],DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds())]),stop);
                Check(runtime.Guard.DirectPermits<=2);
            }
            await runtime.PublishPolicyAsync(new(epoch,[]),stop);await runtime.SweepScopesAsync(stop);Check(runtime.Guard.DirectPermits==0);report["idleScopeRotationBounded"]=true;
            stage="blocked-fresh-flow";await runtime.PublishPolicyAsync(Rules("blocked"),stop);var blocked=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,domain:true);Check(!blocked.GetProperty("success").GetBoolean());for(int i=0;i<40&&runtime.Core.PolicyBlockedDials==0;i++)await Task.Delay(100,stop);Check(runtime.Core.PolicyBlockedDials>0);report["blockedDomainRefused"]=true;
            stage="restore-vpn-default";await runtime.PublishPolicyAsync(new(epoch,[]),stop);var restored=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,domain:true);Check(restored.GetProperty("success").GetBoolean()&&restored.GetProperty("egressHash").GetString()==expected);Check(runtime.Core.State=="Active");report["defaultVpnRestored"]=true;
            await runtime.DisconnectAsync(stop);report["success"]=true;
        }
        catch(Exception error){report["failedStage"]=stage;report["errorType"]=error.GetType().Name;report["runtime"]=runtime?.Snapshot;}
        finally{try{if(runtime!=null)await runtime.DisposeAsync();}catch{report["success"]=false;}await File.WriteAllTextAsync(Path.Combine(root,"result.json"),JsonSerializer.Serialize(report));}
        return report["success"] is true?0:1;
    }
}
