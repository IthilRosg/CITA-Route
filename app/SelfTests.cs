using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RoutePilot;

public static class SelfTests
{
    public static void Run(string root)
    {
        ReleaseUpdateTests.Run(root);
        UdpQualityTests.Run();
        Task.Run(()=>ConnectionSetupTests.Run(root)).GetAwaiter().GetResult();
        Task.Run(()=>ConnectionOperationTests.Run(root)).GetAwaiter().GetResult();
        Task.Run(()=>UpdateWorkflowTests.Run(root)).GetAwaiter().GetResult();
        static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
        var healthSession=new EngineSession{Running=true,BridgeReady=true,Id="current",BridgeGeneration=4};
        var cachedHealth=new SessionHealthState{SessionId="current",BridgeGeneration=4,Healthy=true,UpdatedUtc=DateTime.UtcNow};
        Assert(SessionHealthCache.Matches(healthSession,cachedHealth,DateTime.UtcNow),"Current fresh health must be reusable by GUI without duplicate probe.");
        cachedHealth.BridgeGeneration=3;Assert(!SessionHealthCache.Matches(healthSession,cachedHealth,DateTime.UtcNow),"Old bridge result must not prove a new bridge.");
        cachedHealth.BridgeGeneration=4;cachedHealth.UpdatedUtc=DateTime.UtcNow.AddMinutes(-2);Assert(!SessionHealthCache.Matches(healthSession,cachedHealth,DateTime.UtcNow),"Stale health must expire.");
        cachedHealth.UpdatedUtc=DateTime.UtcNow.AddMinutes(1);Assert(!SessionHealthCache.Matches(healthSession,cachedHealth,DateTime.UtcNow),"Future health must not prove connectivity.");
        cachedHealth.UpdatedUtc=DateTime.UtcNow;healthSession.Running=false;Assert(!SessionHealthCache.Matches(healthSession,cachedHealth,DateTime.UtcNow),"Manual Stop must invalidate cached health.");
        var history=new TrafficHistory();
        var first=history.Apply("one",100,200,"2026-10-17");Assert(first.Up==100 && first.Down==200,"First engine snapshot");
        var repeat=history.Apply("one",100,200,"2026-10-17");Assert(repeat.Up==0 && repeat.Down==0,"Duplicate must not double count");
        history.Apply("one",140,260,"2026-10-17");
        history.Apply("two",20,30,"2026-10-17");Assert(history.TotalUpload==160 && history.TotalDownload==290,"Engine restart accounting");
        history.Apply("two",25,40,"2026-10-18");Assert(history.Days["2026-10-18"].Total==15,"Midnight attribution");
        var saved=System.Text.Json.JsonSerializer.Serialize(history,AppPaths.Json);
        var restored=System.Text.Json.JsonSerializer.Deserialize<TrafficHistory>(saved,AppPaths.Json)!;
        var reopened=restored.Apply("two",25,40,"2026-10-18");Assert(reopened.Up==0 && reopened.Down==0,"App restart must not double count");
        string statePath=Path.Combine(root,"runtime","self-test-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            AppPaths.Write(statePath,history);AppPaths.Write(statePath,restored);
            var disk=AppPaths.Read(statePath,()=>new TrafficHistory());
            var delta=disk.Apply("two",25,40,"2026-10-18");
            Assert(delta.Up==0 && delta.Down==0 && disk.TotalDownload==300,"Atomic persistence must retain counter checkpoints");
        }
        finally{File.Delete(statePath);}
        Assert(DomainRules.Normalize(" Example.COM. ")=="example.com","Normalization");
        Assert(DomainRules.Normalize("пример.рф")=="xn--e1afmkfd.xn--p1ai","IDN");
        Assert(DomainRules.Matches("a.youtube.com","youtube.com") && !DomainRules.Matches("notyoutube.com","youtube.com"),"Suffix boundary");
        bool rejected=false;try{DomainRules.Normalize("https://example.com/path");}catch(ArgumentException){rejected=true;}Assert(rejected,"Reject URLs");
        bool conflict=false;
        try{DomainRules.Save(Path.Combine(root,"runtime","must-not-be-created"),new(){new(){Domain="example.com",Target="Напрямую"},new(){Domain="sub.example.com",Target="VPN"}});}catch(ArgumentException){conflict=true;}
        Assert(conflict,"Conflicting parent/child rules must be rejected before writing any files.");
        var rescue=new RescueState{Phase="Restored",Results=new(){new(){Success=true},new(){Success=false,Informational=true}}};
        Assert(rescue.Passed && !rescue.BlocksRouting,"An unavailable candidate is informational when the required path passed.");
        rescue.Results.Add(new(){Success=false});Assert(!rescue.Passed,"A real diagnostic failure cannot be shown as success.");
        string json="""{"uploadTotal":123,"downloadTotal":456,"connections":[{"id":"abbda8c7-432e-41f4-938c-2a72a04f725c","metadata":{"host":"youtube.com","network":"tcp","destinationIP":"1.2.3.4","destinationPort":"443","processPath":"C:\\Browser\\browser.exe"},"upload":42,"download":80,"chains":["vpn"],"rule":"rule-set=russia-inside","start":"2026-10-17T12:00:00Z"}]}""";
        var snapshot=Snapshot.Parse(json);Assert(snapshot.Download==456 && snapshot.Connections[0].IsVpn && snapshot.Connections[0].Process=="browser.exe","Real Clash API shape");
        string x86Json=json.Replace("C:\\\\Browser\\\\browser.exe","C:\\\\Program Files (x86)\\\\Browser\\\\browser.exe (123)");
        Assert(Snapshot.Parse(x86Json).Connections[0].Process=="browser.exe","Parenthesized directory names must not truncate the process path");
        bool invalidApiRejected=false;
        try{Snapshot.Parse("{}");}catch(System.Text.Json.JsonException){invalidApiRejected=true;}
        Assert(invalidApiRejected,"Malformed API must produce a recoverable JSON error");
        string probeConfig=Path.Combine(root,"runtime","self-test-engine-"+Guid.NewGuid().ToString("N")+".json");
        Process? probe=null;
        try
        {
            File.WriteAllText(probeConfig,"""{"log":{"disabled":true},"outbounds":[{"type":"direct","tag":"direct"}],"route":{"final":"direct"}}""");
            var start=new ProcessStartInfo(Path.Combine(root,"runtime","smart-router.exe")){UseShellExecute=false,CreateNoWindow=true};
            foreach(string argument in new[]{"run","-c",probeConfig,"--disable-color"})start.ArgumentList.Add(argument);
            probe=Process.Start(start)!;Thread.Sleep(400);
            using var backend=new Backend(root);
            var session=new EngineSession{Running=true,RouterPid=probe.Id,StartedUtc=DateTime.UtcNow};
            Assert(backend.EngineRunning(session),"A running router must remain detectable without a statistics API");
            session.RouterStartedUtc=DateTime.UtcNow.AddDays(-1);Assert(!backend.EngineRunning(session),"Same-name PID reuse must not count as an owned router.");session.RouterStartedUtc=DateTime.MinValue;
            session.Running=false;Assert(!backend.EngineRunning(session),"A stopped session must not appear active");
            session.Running=true;session.RouterPid=Environment.ProcessId;
            Assert(!backend.EngineRunning(session),"A reused PID from another process must not appear as the router");
        }
        finally
        {
            if(probe!=null){try{if(!probe.HasExited)probe.Kill();probe.WaitForExit(3000);}finally{probe.Dispose();}}
            File.Delete(probeConfig);
        }
        string actionFolder=Path.Combine(root,"runtime","backend-test-'"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(actionFolder,"runtime"));Directory.CreateDirectory(Path.Combine(actionFolder,"logs"));
            File.Copy(Path.Combine(root,"FileStorage.ps1"),Path.Combine(actionFolder,"FileStorage.ps1"));
            string script=Path.Combine(actionFolder,"SmartRouting.ps1");
            File.WriteAllText(script,"param([string]$Action)\nWrite-Output 'operation completed'\n");
            using var backend=new Backend(actionFolder);
            var success=Task.Run(()=>backend.ActionAsync("UpdateRules",false)).GetAwaiter().GetResult();
            Assert(success.Success,"Windows PowerShell operation wrapper must report success, including quoted paths");
            File.WriteAllText(script,"param([string]$Action)\nthrow 'controlled failure'\n");
            var failure=Task.Run(()=>backend.ActionAsync("UpdateRules",false)).GetAwaiter().GetResult();
            Assert(!failure.Success && failure.Message=="controlled failure","Windows PowerShell operation wrapper must preserve failures");
        }
        finally
        {
            string allowed=Path.GetFullPath(Path.Combine(root,"runtime"))+Path.DirectorySeparatorChar;
            if(!Path.GetFullPath(actionFolder).StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unsafe test cleanup path");
            if(Directory.Exists(actionFolder))Directory.Delete(actionFolder,true);
        }
        string uiFolder=Path.Combine(root,"runtime","ui-fixture-"+Guid.NewGuid().ToString("N"));
        try{new MainWindow(uiFolder,true).VerifyPresentation();}
        finally{if(Directory.Exists(uiFolder))Directory.Delete(uiFolder,true);}
        Directory.CreateDirectory(Path.Combine(root,"logs"));File.WriteAllText(Path.Combine(root,"logs","app-self-test.log"),"PASS: counters, restart persistence, midnight, domain validation, IDN, suffix boundaries, API parsing, x86 process paths, malformed API handling, router identity, actual Windows PowerShell setup/Start/Stop reports, cooperative cancellation and cleanup, Unicode/quoted/empty arguments, compact/advanced physical sizes and retained connection state, duplicate-start prevention, stale UI progress rejection.");
    }
}
