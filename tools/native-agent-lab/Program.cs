using System.Text.Json;
using RoutePilot.Agent;
if(args.Length==3&&args[0]=="--native-policy-session-lab")return await NativePolicySessionLab.Run(Path.GetFullPath(args[1]),args[2]);
if(args.Length==3&&args[0]=="--native-policy-auto-lab")return await NativePolicySessionLab.Run(Path.GetFullPath(args[1]),args[2],true);
if(args.Length==3&&args[0]=="--native-policy-performance-lab")return await NativePolicySessionLab.Run(Path.GetFullPath(args[1]),args[2],true,true);
if(args.Length==3&&args[0]=="--direct-guard-lab")return await DirectGuardLab.Run(Path.GetFullPath(args[1]),args[2]);
if(args.Length==1&&args[0]=="--profile-import-self-test"){await NativeImportTests.Run();return 0;}
if(args.Length==2 && args[0]=="--wfp-os-probe"){using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));try{var proof=await CoreSafetyLab.Tun(Path.Combine(Path.GetFullPath(args[1]),"native-awg-lab.exe"),"1.1.1.1",deadline.Token);Console.WriteLine(proof.GetRawText());return proof.GetProperty("success").GetBoolean()?0:1;}catch(OperationCanceledException){Console.WriteLine("{\"success\":false,\"timeout\":true}");return 1;}}
if(args.Length==4 && args[0]=="--wfp-session-lab")return await WfpSessionLab.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]),args[3]);
if(args.Length==3 && args[0]=="--trust-lab")return TrustLab.Run(Path.GetFullPath(args[1]),args[2]);

if(args.Length==2 && args[0]=="--startup-lab")return await StartupLab.Run(Path.GetFullPath(args[1]));
if(args.Length==2 && args[0]=="--servers-lab")return await ServersLab.Run(Path.GetFullPath(args[1]));
if(args.Length==2 && args[0]=="--workload-lab")return await WorkloadLab.Run(Path.GetFullPath(args[1]));
if(args.Length==2 && args[0]=="--export-second-exit-lab")return await WorkloadLab.ExportSource(Path.GetFullPath(args[1]));
if(args.Length==4 && args[0]=="--core-workload-lab")return await WorkloadLab.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]),args[3]);

if(args.Length==4 && args[0]=="--core-binding-lab")return await CoreBindingLab.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]),args[3]);
if(args.Length==4 && args[0]=="--core-two-servers-lab")return await CoreBindingLab.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]),args[3],true);
if(args.Length==4 && args[0]=="--core-safety-lab")return await CoreSafetyLab.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]),args[3]);
if(args.Length==4 && args[0]=="--core-network-lab")return await CoreNetworkLab.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]),args[3]);
if(args.Length==4 && args[0]=="--persistent-control-lab")return await PersistentControlLab.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]),args[3]);
if(args.Length==4 && args[0]=="--native-session-lab")return await NativeSessionLab.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]),args[3]);

if(args.Length==2 && args[0]=="--profiles-lab")return await ProfilesLab.Run(Path.GetFullPath(args[1]));
if(args.Length==2 && args[0]=="--profiles-races-lab")return await ProfilesLab.Run(Path.GetFullPath(args[1]),true);

if(args.Length==1 && args[0]=="--self-test")
{
    if(!NativeBootIdentity.MayResume(true,"boot-a","boot-a")||NativeBootIdentity.MayResume(true,"boot-a","boot-b")||NativeBootIdentity.MayResume(true,"","")||NativeBootIdentity.MayResume(false,"boot-a","boot-a"))throw new Exception("Native boot ownership failed.");
    RepairBudgetTests.Run();
    var schedule=new NativeCalibrationSchedule();
    NativeObservedDestination Seen(int i)=>new("h"+i+".example.com","","1.1.1.1",443);
    var wakeup=new NativeObservationWakeup();
    var observedEvents=new List<NativeObservedDestination>();wakeup.Observe([Seen(0)],observedEvents.Add);wakeup.Observe([Seen(0)],observedEvents.Add);
    if(observedEvents.Count!=1||observedEvents[0]!=Seen(0))throw new Exception("Observation trace must identify new tuples exactly once.");wakeup.Reset();
    if(!wakeup.Observe([Seen(0),Seen(1)])||wakeup.Pending!=1||wakeup.Observe([Seen(1),Seen(0)]))throw new Exception("Observation signal must ignore snapshot reordering.");
    for(int i=2;i<128;i++)wakeup.Observe([Seen(i)]);
    if(wakeup.Pending!=1||!await wakeup.WaitAsync(CancellationToken.None,TimeSpan.Zero)||await wakeup.WaitAsync(CancellationToken.None,TimeSpan.Zero))throw new Exception("Observation burst must coalesce without a busy loop.");
    wakeup.Reset();var waiting=wakeup.WaitAsync(CancellationToken.None,TimeSpan.FromSeconds(10));wakeup.Observe([Seen(0)]);if(!await waiting)throw new Exception("A waiting calibration missed its observation.");
    if(wakeup.Observe([Seen(0)]))throw new Exception("Unchanged observations must not retrigger calibration.");
    wakeup.Reset();if(!wakeup.Observe([Seen(0)]))throw new Exception("Core replacement retained stale observation identity.");
    bool signalBounded=false;try{wakeup.Observe(Enumerable.Range(0,129).Select(Seen).ToArray());}catch(InvalidDataException){signalBounded=true;}if(!signalBounded)throw new Exception("Observation wakeup exceeded its input budget.");
    wakeup.Reset();using(var cancelled=new CancellationTokenSource()){var wait=wakeup.WaitAsync(cancelled.Token);cancelled.Cancel();bool stopped=false;try{await wait;}catch(OperationCanceledException){stopped=true;}if(!stopped)throw new Exception("Observation wait ignored Stop.");}
    var window=Enumerable.Range(0,128).Select(Seen).ToArray();
    foreach(var item in window)schedule.Mark(item,1000);
    if(schedule.Select(window,1001)!=null||schedule.Count!=128)throw new Exception("Retry schedule ignored cooldown.");
    var rotated=Enumerable.Range(1,128).Select(Seen).ToArray();
    if(schedule.Select(rotated,1001)!=Seen(128)||schedule.Count!=127)throw new Exception("New observations must replace old retry state.");
    schedule.Mark(Seen(128),1001);
    if(schedule.Select(rotated,61000)==Seen(128)||schedule.Count!=128)throw new Exception("Calibration must prioritize oldest due entries.");
    schedule.Clear();if(schedule.Count!=0)throw new Exception("Old network retry state retained.");
    var calibrationBudget=new NativeCalibrationBudget();for(int i=0;i<32;i++)if(!calibrationBudget.TryAcquire(1000))throw new Exception("Calibration startup budget refused.");
    if(calibrationBudget.TryAcquire(1000)||calibrationBudget.TryAcquire(60999)||calibrationBudget.TryAcquire(999)||!calibrationBudget.TryAcquire(61000))throw new Exception("Calibration must renew its bounded minute budget without clock rollback bursts.");
    var observationFrame=JsonSerializer.Serialize(new{observations=Enumerable.Range(0,128).Select(i=>new{Domain="h"+i+".example.com",Application=@"C:\"+new string('x',480)+".exe",Address="1.1.1.1",Port=443}).ToArray()});
    using(var frameStream=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(observationFrame+"\n{\"next\":true}\n")))
    using(var frameReader=new StreamReader(frameStream))
    {
        var large=await NativeAwgBackend.ReadReply(frameReader,CancellationToken.None,262144);var next=await NativeAwgBackend.ReadReply(frameReader,CancellationToken.None);
        if(large.GetProperty("observations").GetArrayLength()!=128||!next.GetProperty("next").GetBoolean())throw new Exception("Bounded observation framing lost alignment.");
    }
    var owner=JsonSerializer.SerializeToElement(new{state="RunningUnverified"});
    foreach(string code in new[]{"Closed","ConnectionReset","Timeout","CertificateRejected","Canceled","Unclassified"})
    {
        var probe=JsonSerializer.SerializeToElement(new{success=false,failureCode=code});bool expected=code is "Closed" or "ConnectionReset" or "Timeout";
        if(NativeProbePolicy.RetryAnonymousHttps(probe,owner,1)!=expected||NativeProbePolicy.RetryAnonymousHttps(probe,owner,2))throw new Exception("Anonymous HTTPS retry scope failed.");
        if(NativeProbePolicy.RetryAnonymousHttps(probe,JsonSerializer.SerializeToElement(new{state="Exhausted"}),1))throw new Exception("Exhausted owner must remain closed.");
    }
    using var spec=JsonDocument.Parse(JsonSerializer.Serialize(new{Session=new string('a',32),Generation=7UL,Token=new string('b',64),Config="fixture"}));
    var identity=NativeAwgBackend.ValidateStart(spec.RootElement);
    foreach(var command in new[]{new{Session=identity.Session,Generation=6UL,Action="Stop"},new{Session=new string('c',32),Generation=7UL,Action="Stop"},new{Session=identity.Session,Generation=7UL,Action="RestartNetwork"}})
    {
        using var doc=JsonDocument.Parse(JsonSerializer.Serialize(command));bool refused=false;try{NativeAwgBackend.ValidateCommand(doc.RootElement,identity.Session,identity.Generation);}catch(InvalidDataException){refused=true;}if(!refused)throw new Exception("Ownership test failed.");
    }
    Console.WriteLine("PASS: backend command identity and action scope.");return 0;
}
if(args.Length!=2||args[0]!="--owner-agent-lab")return 2;
await using var backend=new NativeAwgBackend(Path.Combine(Path.GetFullPath(args[1]),"native-awg-lab.exe"));
try
{
    using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(100));
    string? first=await Console.In.ReadLineAsync(budget.Token);if(first==null||first.Length>131072)return 1;
    using(var doc=JsonDocument.Parse(first)){var ready=await backend.StartAsync(doc.RootElement,budget.Token);Console.WriteLine(ready.GetRawText());}
    for(int i=0;i<64;i++)
    {
        string? line=await Console.In.ReadLineAsync(budget.Token);if(line==null)return 0;if(line.Length>2048)return 1;
        using var doc=JsonDocument.Parse(line);var reply=await backend.SendAsync(doc.RootElement,budget.Token);Console.WriteLine(reply.GetRawText());if(doc.RootElement.GetProperty("Action").GetString()=="Stop")return 0;
    }
}
catch{Console.Error.WriteLine("Native backend laboratory operation failed.");return 1;}
return 1;
