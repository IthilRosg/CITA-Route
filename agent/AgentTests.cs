using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RoutePilot.Agent;

public static class AgentTests
{
    public static async Task Run()
    {
        StartupTests.Run();
        ResourceGuardTests.Run();
        ComponentLivenessTests.Run();
        SystemRouterTests.Run();
        SessionEvidenceTests.Run();
        await EgressReferenceTests.Run();
        await PolicyStartupTests.Run();
        await PolicyPublicationTests.Run();
        await DetachedCommandTests.Run();
        await PolicyDnsTests.Run();
        void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
        foreach(string newline in new[]{"\n","\r\n"})
        foreach(string line in new[]{"Connected to server using profile SmartRouting-AWG2","Подключено к серверу с использованием профиля SmartRouting-AWG2"})
        {
            string status=line+newline+"External address: 8.8.8.8, Test"+newline;
            Assert(NetworkProbe.ManagedProfileConnected(status),"Real multiline CLI status must support Windows CRLF and LF.");
            Assert(!NetworkProbe.ManagedProfileConnected(status.Replace("SmartRouting-AWG2","SmartRouting-AWG2-copy")),"CRLF must not weaken exact profile identity.");
            Assert(!NetworkProbe.ManagedProfileConnected(status+"Connected to server using profile Other"+newline),"Conflicting CRLF profiles cannot prove ownership.");
        }
        foreach(string state in new[]{"Establishing connection using profile ","Disconnecting from server using profile ","Устанавливается соединение с использованием профиля ","Отключение от сервера с использованием профиля ","Не подключено к серверу с использованием профиля "})
            Assert(!NetworkProbe.ManagedProfileConnected(state+"SmartRouting-AWG2\r\nExternal address: 8.8.8.8\r\n"),"Transitional/negative localized CLI status cannot prove a VPN connection.");
        Assert(!NetworkProbe.ManagedProfileConnected("Connected to server from profile SmartRouting-AWG2\nConnected to server from profile Other"),"Conflicting profile identities must not prove the managed VPN.");
        Assert(!NetworkProbe.ManagedProfileConnected("Connected to server from profile Other profile SmartRouting-AWG2"),"A profile keyword inside a foreign name must not hide the real name.");
        Assert(!NetworkProbe.ManagedProfileConnected("Not connected to server using profile SmartRouting-AWG2"),"Negative status must not match the Connected substring.");
        using(var values=JsonDocument.Parse("{\"ExpectedEgressIp\":\"8.8.8.8\",\"WireSockCliPath\":\"test\"}"))
        {
            string selected="Connected to server using profile SmartRouting-AWG2";
            var mismatch=await NetworkProbe.VerifyConfiguredEgressDetailed(values.RootElement,_=>Task.FromResult(selected),()=>Task.FromResult(System.Net.IPAddress.Parse("8.8.4.4")));
            Assert(mismatch.Code=="EgressMismatch" && !mismatch.Verified,"Exit mismatch needs its own reason, not control HTTPS failure.");
            var outage=await NetworkProbe.VerifyConfiguredEgressDetailed(values.RootElement,_=>Task.FromResult(selected),()=>Task.FromException<System.Net.IPAddress>(new IOException("fixture")));
            Assert(outage.Code=="ControlHttpsUnavailable","Control-site failure must not imply a mismatched exit.");
            var unavailable=await NetworkProbe.VerifyConfiguredEgressDetailed(values.RootElement,_=>Task.FromException<string>(new IOException("fixture")),()=>throw new Exception("No probe is allowed after unknown status"));
            Assert(unavailable.Code=="StatusUnavailable","Unknown CLI status must not be treated as disconnection.");
        }
        Assert(NetworkProbe.ManagedProfileConnected("Подключено к серверу с использованием профиля SmartRouting-AWG2\nВнешний адрес: 8.8.8.8, Test"),"Selected managed profile status");
        Assert(!NetworkProbe.ManagedProfileConnected("Connected to server using profile Other-SmartRouting-AWG2"),"A different profile cannot prove the selected VPN");
        Assert(NetworkProbe.ReportedEgress("External IP address: 8.8.4.4, Test")=="8.8.4.4","NAT exit differs from UDP endpoint");
        Assert(NetworkProbe.ReportedEgress("Внешний адрес: 1.1.1.1, Test")=="1.1.1.1","Localized exit discovery");
        Assert(NetworkProbe.ReportedEgress("External address: 999.1.1.1")==null,"Invalid status IP rejected");
        using(var nat=JsonDocument.Parse("{\"EndpointIp\":\"203.0.113.20\",\"ExpectedEgressIp\":\"\",\"WireSockCliPath\":\"test\"}"))
        {
            string status="Connected to server using profile SmartRouting-AWG2\nExternal IP address: 8.8.4.4, Test";
            var verified=await NetworkProbe.VerifyConfiguredEgress(nat.RootElement,_=>Task.FromResult(status),()=>Task.FromResult(System.Net.IPAddress.Parse("8.8.4.4")));
            Assert(verified.Verified,"VPN NAT egress must be independent of its UDP endpoint");
            var mismatch=await NetworkProbe.VerifyConfiguredEgress(nat.RootElement,_=>Task.FromResult(status),()=>Task.FromResult(System.Net.IPAddress.Parse("1.1.1.1")));
            Assert(!mismatch.Verified,"Reachable HTTPS with a wrong exit must not pass");
            int reads=0;
            var changed=await NetworkProbe.VerifyConfiguredEgress(nat.RootElement,_=>Task.FromResult(++reads==1?status:"No connection"),()=>Task.FromResult(System.Net.IPAddress.Parse("8.8.4.4")));
            Assert(!changed.Verified,"Profile switch during an HTTPS probe must invalidate its result");
        }
        string temporary=Path.Combine(Path.GetTempPath(),"RoutePilot-agent-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
        try
        {
            await PolicyWorkerTests.Run(temporary);
            await PolicyOptimizationTests.Run(temporary);
            await UdpQualityTests.Run(temporary);
            await WireSockStatisticsTests.Run();
            var store=new RunStore(temporary);store.Begin(TestKind.DryRun,1,2,5);string old=store.State.Id;
            Assert(store.State.Phase==RunPhase.Armed,"Run must be durably armed before work.");
            Assert(store.Transition(old,RunPhase.Testing,"worker"),"Start transition.");
            bool refused=false;try{store.Begin(TestKind.DryRun,1);}catch(InvalidOperationException){refused=true;}
            Assert(refused,"Concurrent runs must be rejected.");
            Assert(!store.Transition("wrong-generation",RunPhase.Restoring,"late callback"),"Old callback must be rejected.");
            var restarted=new RunStore(temporary);Assert(restarted.State.Active && restarted.State.Id==old,"Pending state must survive service restart.");
            Assert(restarted.Transition(old,RunPhase.Restoring,"watchdog"),"Recovery transition.");
            Assert(restarted.Transition(old,RunPhase.RecoveryFailed,"network unavailable"),"Failed recovery must remain visible.");
            refused=false;try{restarted.Begin(TestKind.DryRun,1);}catch(InvalidOperationException){refused=true;}
            Assert(refused,"Failed recovery must block the next test.");
            Assert(restarted.Transition(old,RunPhase.Restoring,"retry") && restarted.Transition(old,RunPhase.Restored,"done"),"Recovery must be retryable.");
            restarted.Begin(TestKind.DryRun,1);Assert(restarted.State.Generation==2 && restarted.State.Id!=old,"Run identity must not be reused.");
            Assert(!restarted.Transition(old,RunPhase.Restoring,"late"),"An old watcher cannot recover a new run.");
            var roundtrip=JsonSerializer.Deserialize<AgentReply>(JsonSerializer.Serialize(new AgentReply(true,"ok",restarted.State),Storage.Wire),Storage.Json);
            Assert(roundtrip?.State?.Id==restarted.State.Id,"Wire state enum and casing round trip.");
            string compact=JsonSerializer.Serialize(new AgentReply(true,"line\ninside",restarted.State),Storage.Wire);
            Assert(!compact.Contains('\n'),"A line-framed response cannot contain framing newlines.");
            using var framed=new MemoryStream(Encoding.UTF8.GetBytes(compact+"\n"));
            Assert(await PipeMessages.Read(framed,CancellationToken.None)==compact,"Pipe framing round trip.");
            using var oversized=new MemoryStream(Encoding.UTF8.GetBytes(new string('x',32769)));
            refused=false;try{await PipeMessages.Read(oversized,CancellationToken.None);}catch(InvalidDataException){refused=true;}
            Assert(refused,"Oversized IPC request must be rejected.");
            using var current=Process.GetCurrentProcess();var identity=ProcessIdentity.From(current);
            Assert(identity.Matches(),"Owned process identity.");
            Assert(!(identity with{StartedUtc=identity.StartedUtc.AddSeconds(-1)}).Matches(),"PID reuse guard must check creation time.");
            Assert(!(identity with{Path=identity.Path+".wrong"}).Matches(),"PID guard must check the image path.");
            Assert(UserLaunch.Quote("a b")=="\"a b\"" && UserLaunch.Quote("a\\")=="\"a\\\\\"","Windows argument quoting.");
            refused=false;try{Tools.Profile("../another-profile");}catch(InvalidDataException){refused=true;}
            Assert(refused,"Run IDs must not become profile names or filesystem traversal.");
            Assert(Tools.Profile(restarted.State.Id).StartsWith("RoutePilot-Probe-"),"Namespaced temporary profile.");
            string state=Path.Combine(temporary,"atomic.json");Storage.Write(state,new{value=1});Storage.Write(state,new{value=2});
            using(var locked=new FileStream(state,FileMode.Open,FileAccess.Read,FileShare.Read))
            {refused=false;try{Storage.Write(state,new{value=3});}catch(IOException){refused=true;}Assert(refused,"Locked journal replacement must fail safely.");}
            Assert(File.ReadAllText(state).Contains('2') && !Directory.EnumerateFiles(temporary,"*.tmp").Any(),"Failed replacement must preserve the journal and remove temporary files.");
            string framedJson=await Tools.PowerShell("$ProgressPreference='Continue'; Write-Progress -Activity 'test' -Status 'test'; @{Index=5;Gateway='192.0.2.1'} | ConvertTo-Json -Compress");
            using var nativeJson=JsonDocument.Parse(framedJson);Assert(nativeJson.RootElement.GetProperty("Index").GetInt32()==5,"PowerShell progress on stderr must not corrupt stdout JSON.");
            Assert(PolicySafety.Domain("пример.рф")=="xn--e1afmkfd.xn--p1ai" && PolicySafety.Domain("http://example.com")==null && PolicySafety.Domain("127.0.0.1")==null && PolicySafety.Domain("printer.local")==null,"Policy probes must normalize IDN and reject URLs, literal/local destinations.");
            Assert(!PolicySafety.Public(System.Net.IPAddress.Parse("192.168.1.1")) && !PolicySafety.Public(System.Net.IPAddress.Parse("198.18.0.1")) && !PolicySafety.Public(System.Net.IPAddress.Parse("100.64.0.1")) && PolicySafety.Public(System.Net.IPAddress.Parse("1.1.1.1")),"Local, shared and FakeIP destinations cannot enter public probes.");
            var key=new PolicyKey("epoch-a","example.com",@"C:\Browser\browser.exe",443);
            var decision=new PolicyDecision(key,"dpi","test",DateTime.UtcNow,DateTime.UtcNow.AddMinutes(15),1);
            Assert(PolicySafety.Live(decision,"epoch-a",DateTime.UtcNow) && !PolicySafety.Live(decision,"epoch-b",DateTime.UtcNow) && !PolicySafety.Live(decision,"epoch-a",DateTime.UtcNow.AddMinutes(16)),"Network generation and TTL invalidate adaptive decisions.");
            Assert(key!=key with{Application=@"C:\Other\other.exe"} && !PolicySafety.Valid(key with{Protocol="udp"}) && !PolicySafety.Valid(key with{Port=80}),"Policy decisions must be scoped by application, TCP and destination port.");
            var calls=new List<int>();
            var selected=await PolicyWorker.SelectRoute((port,_)=>{calls.Add(port);return Task.FromResult(new CandidateResult(true,true,"HTTP 403: valid TLS",3));},CancellationToken.None);
            Assert(selected?.Route=="direct" && calls.Count(p=>p==20829)==2,"A valid HTTP denial must keep direct despite parallel fallback probes.");
            calls.Clear();
            selected=await PolicyWorker.SelectRoute((port,_)=>{calls.Add(port);return Task.FromResult(new CandidateResult(port==20808,false,"probe",4));},CancellationToken.None);
            Assert(selected?.Route=="vpn" && calls.SequenceEqual(new[]{20829,20829,20828,20828,20808,20808}),"Only two independently successful probes select a fallback; failed DPI must reach VPN.");
            calls.Clear();
            selected=await PolicyWorker.SelectRoute((port,_)=>{calls.Add(port);return Task.FromResult(new CandidateResult(false,true,"certificate failure",1));},CancellationToken.None);
            Assert(selected==null && calls.Count(p=>p==20829)==1,"Certificate errors must not be hidden by changing routes.");
            Console.WriteLine("PASS: journal restart, generation guards, exclusive runs, bounded IPC framing, process identity, Windows quoting, profile namespace, atomic failure preservation.");
            Console.WriteLine("PASS: adaptive policy scopes, public DNS/FakeIP safety, TTL/network invalidation, HTTP denial handling, bounded candidate selection, certificate failures.");
        }
        finally
        {
            string prefix=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar+"RoutePilot-agent-tests-";
            if(!Path.GetFullPath(temporary).StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unsafe cleanup path.");
            Directory.Delete(temporary,true);
        }
    }
}
