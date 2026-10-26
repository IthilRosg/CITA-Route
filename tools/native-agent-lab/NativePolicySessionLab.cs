using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using RoutePilot.Agent;

internal static class NativePolicySessionLab
{
    static void Check(bool value){if(!value)throw new IOException("Native policy route proof failed.");}
    internal static async Task<int> Run(string root,string run,bool automatic=false,bool performance=false,bool coverage=false,bool pages=false,bool dnsFailover=false,bool dnsOutage=false)
    {
        string stage="prepare";var report=new Dictionary<string,object?>{{"success",false}};NativeGuardedSession? runtime=null;
        try
        {
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
            var config=new{log=new{disabled=true},inbounds=new[]{new{type="tun",tag="native-tun",interface_name="Cita-NativeLab",address=new[]{"172.30.127.1/30"},route_address=planner.Prefixes.Where(x=>!x.Contains(':')).ToArray(),route_exclude_address=new[]{endpoint.Address+"/32"},auto_route=true,strict_route=false,dns_mode="disabled",stack="gvisor",mtu=1400}},outbounds=new object[]{new{type="cita_native_lab",tag="native-vpn",owner},new{type="direct",tag="native-direct",bind_interface=nic.Name}},route=new{final="native-vpn",rules=new[]{new{action="sniff",timeout="300ms"}}}};
            await File.WriteAllTextAsync(configPath,JsonSerializer.Serialize(config),stop);
            runtime=new NativeGuardedSession(Path.Combine(root,"native-owner.exe"),Path.Combine(root,"native-core.exe"),Path.Combine(root,"native-guard.exe"),configPath,owner,run,policy,expected,afterStart:async(binding,ct)=>{await File.WriteAllTextAsync(Path.Combine(root,"core-pid.json"),JsonSerializer.Serialize(new{pid=binding.ProcessId}),ct);},endpoint:permission,adaptive:automatic);
            var timer=Stopwatch.StartNew();stage="initialize";await runtime.InitializeAsync(stop);report["initializeMs"]=timer.ElapsedMilliseconds;
            var spec=JsonSerializer.SerializeToElement(new{Session=Guid.NewGuid().ToString("N"),Generation=1UL,Token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),Config=text});timer.Restart();stage="connect";await runtime.ConnectAsync(spec,stop);report["connectMs"]=timer.ElapsedMilliseconds;text="";
            if(automatic)
            {
                var traceLock=new object();var trace=new Dictionary<string,long>();NativeObservedDestination? target=null;int tracePid=runtime.Core.ProcessId;
                var applied=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                runtime.Core.CalibrationObserver=(phase,observed,pid,at)=>{
                    if(pid!=tracePid||observed.Domain!="cloudflare-dns.com"||observed.Address!="1.1.1.1"||observed.Port!=443)return;
                    lock(traceLock){if(phase=="observed"&&target==null)target=observed;if(target!=observed)return;trace.TryAdd(phase,at);if(phase=="applied")applied.TrySetResult();}
                };
                long probeStarted=Environment.TickCount64;
                timer.Restart();stage="automatic-observation";var first=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,domain:true);Check(first.GetProperty("success").GetBoolean());report["firstUnknownDomainWorks"]=true;report["firstControlProbeMs"]=timer.ElapsedMilliseconds;timer.Restart();
                await applied.Task.WaitAsync(TimeSpan.FromSeconds(25),stop);report["firstAutomaticDecisionWaitMs"]=timer.ElapsedMilliseconds;
                lock(traceLock){long seen=trace["observed"],started=trace["started"],probed=trace["probe-passed"],confirmed=trace["applied"];Check(probeStarted<=seen&&seen<=started&&started<=probed&&probed<=confirmed&&runtime.Core.ProcessId==tracePid);
                    report["targetCalibrationTiming"]=new{matchedTarget=true,sameCore=true,probeStartToObservedMs=seen-probeStarted,queueMs=started-seen,scopeAndDirectProbeMs=probed-started,publicationMs=confirmed-probed,observedToAppliedMs=confirmed-seen};}
                runtime.Core.CalibrationObserver=null;
                stage="automatic-direct";var next=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,domain:true);Check(next.GetProperty("success").GetBoolean());for(int i=0;i<30&&runtime.Core.PolicyDirectDials==0;i++)await Task.Delay(100,stop);Check(runtime.Core.PolicyDirectDials>0);report["automaticDomainSelectionVerified"]=true;
                if(performance){stage="performance-baseline";report["performance"]=await NativePerformanceLab.Measure(runtime,root,stop);}
                if(coverage){stage="domain-coverage";report["coverage"]=await NativeCoverageLab.Measure(runtime,root,stop);}
                if(pages){stage="page-resources";report["pages"]=await NativePageLab.Measure(runtime,root,stop);}
                if(dnsOutage){stage="dns-outage";report["dnsOutage"]=await NativeCoverageLab.MeasureDnsOutage(runtime,root,stop);}
                timer.Restart();await runtime.DisconnectAsync(stop);report["disconnectMs"]=timer.ElapsedMilliseconds;report["success"]=true;return 0;
            }
            string epoch=Guid.NewGuid().ToString("N");NativePolicyRequest Rules(string route)=>new(epoch,[new("cloudflare-dns.com","","tcp",route,443,route=="direct"?["1.1.1.1"]:[],DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds())]);
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
