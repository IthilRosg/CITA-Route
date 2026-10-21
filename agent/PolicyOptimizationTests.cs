using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace RoutePilot.Agent;

internal static class PolicyOptimizationTests
{
    static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
    internal static async Task Run(string temporary)
    {
        var ports=new PolicyPorts();
        int active=0,maximum=0,completed=0;
        async Task<CandidateResult> SlowDirect(int port,CancellationToken stop)
        {
            int current=Interlocked.Increment(ref active);maximum=Math.Max(maximum,current);
            try{await Task.Delay(port==ports.Direct?55:5,stop);return new(true,true,"valid TLS",1);}
            finally{Interlocked.Decrement(ref active);Interlocked.Increment(ref completed);}
        }
        var selection=await PolicyTransport.Select(SlowDirect,ports,CancellationToken.None);
        Assert(selection.Route=="direct" && active==0 && maximum<=3 && completed>=4,"Fast VPN must not beat slower reachable direct; all child probes must be drained.");
        selection=await PolicyTransport.Select((port,_)=>Task.FromResult(new CandidateResult(port==ports.Direct,port!=ports.Dpi,"TLS",1)),ports,CancellationToken.None);
        Assert(selection.Route=="direct","An unused candidate's certificate failure must not break confirmed direct.");
        selection=await PolicyTransport.Select((port,_)=>Task.FromResult(new CandidateResult(port!=ports.Direct,true,"TLS",1)),ports,CancellationToken.None);
        Assert(selection.Route==null && selection.Outcome==ProbeOutcome.TlsAuthenticationError,"A preferred path certificate failure must not be bypassed using VPN.");
        int directCalls=0;
        selection=await PolicyTransport.Select((port,_)=>Task.FromResult(new CandidateResult(port!=ports.Direct || Interlocked.Increment(ref directCalls)==1,false,"intermittent",1)),ports,CancellationToken.None);
        Assert(selection.Route==null && selection.Outcome==ProbeOutcome.Indeterminate,"One successful direct probe must prevent false fallback after a transient failure.");
        selection=await PolicyTransport.Select((_,_)=>Task.FromResult(new CandidateResult(false,false,"down",1)),ports,CancellationToken.None);
        Assert(selection.Route==null && selection.Outcome==ProbeOutcome.Unavailable,"All-down is not a direct success.");
        using(var cancel=new CancellationTokenSource(35))
        {
            bool cancelled=false;active=0;
            try{await PolicyTransport.Select(async(_,token)=>{Interlocked.Increment(ref active);try{await Task.Delay(5000,token);return new(true,true,"TLS",1);}finally{Interlocked.Decrement(ref active);}},ports,cancel.Token);}
            catch(OperationCanceledException){cancelled=true;}
            Assert(cancelled && active==0,"Cancellation must drain every parallel socket task.");
        }
        await Pinning();
        Console.WriteLine("PASS: parallel direct priority, partial-success/certificate policy, bounded concurrency, cancellation, IP pinning and original TLS SNI.");

        string root=Path.Combine(temporary,"optimization");
        foreach(string part in new[]{"runtime/rules","private","data","logs"})Directory.CreateDirectory(Path.Combine(root,part));
        var session=new PolicySession{Id=Guid.NewGuid().ToString("N"),Running=true,RouterPid=Environment.ProcessId};
        Storage.Write(Path.Combine(root,"runtime/session.json"),session);
        Storage.Write(Path.Combine(root,"runtime/bridge.json"),new{outbounds=new[]{new{bind_interface=System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().First().Name}}});
        File.WriteAllText(Path.Combine(root,"force-direct.txt"),"");File.WriteAllText(Path.Combine(root,"force-vpn.txt"),"");
        var pending=new TaskCompletionSource<CalibrationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options=new PolicyOptions{Mode="Auto",AllowListedDpi=true};
        var worker=new PolicyWorker(root,session,options,(_,_)=>pending.Task);
        worker.Status("Auto");
        string epoch=Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json")).GetProperty("epoch").GetString()!;
        var key=new PolicyKey(epoch,"learned.example.com",Environment.ProcessPath!,443);
        var now=DateTime.UtcNow;
        var old=new PolicyDecision(key,"vpn","verified",now.AddMinutes(-14),now.AddSeconds(30),1);
        worker.StoreDecision(old);worker.Publish();
        worker.RegisterFailure(key,"log:one",CancellationToken.None);worker.RegisterFailure(key,"api:the-same-flow",CancellationToken.None);worker.DrainFailures(CancellationToken.None);worker.Status("Auto");
        var state=Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json"));
        Assert(state.GetProperty("pending").GetInt32()==1 && state.GetProperty("decisions").GetArrayLength()==1,"Two sources must coalesce into one job and retain the existing route.");
        worker.Clear();pending.SetResult(new(ProbeOutcome.Reachable,old with{CheckedUtc=now,ExpiresUtc=now.AddMinutes(15)},1,2,Array.Empty<CandidateTiming>()));
        await worker.CompletePending();worker.Status("Auto");
        Assert(Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json")).GetProperty("decisions").GetArrayLength()==0,"Clear must reject an old task even when that task ignores cancellation.");
        Assert(worker.Binding(old with{ExpiresUtc=now.AddSeconds(-1)},now)=="vpn" && worker.Binding(old with{ExpiresUtc=now.AddMinutes(-3)},now)=="vpn","Expired confidence must retain the active protected direction instead of blocking or silently directing the site.");
        Storage.Write(Path.Combine(root,"runtime/policy-active.json"),new PolicyActiveSnapshot(epoch,options.Mode,options.AllowListedDpi,"",new[]{old with{CheckedUtc=now.AddMinutes(-20),ExpiresUtc=now.AddMinutes(-5)}}));
        var recovered=new PolicyWorker(root,session,options);recovered.LoadActiveBindings();recovered.Status("Auto");
        Assert(Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json")).GetProperty("decisions").GetArrayLength()==1,"A worker restart inside the SAME epoch must retain its expired protected direction without extending confidence.");
        var nextSession=new PolicySession{Id=Guid.NewGuid().ToString("N"),Running=true};
        var isolatedEpoch=new PolicyWorker(root,nextSession,options);isolatedEpoch.LoadActiveBindings();isolatedEpoch.Status("Auto");
        Assert(Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json")).GetProperty("decisions").GetArrayLength()==0,"An active-session snapshot must never cross session/network epochs.");
        pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
        worker=new(root,session,options,(_,_)=>pending.Task);worker.StoreDecision(old);worker.Touch(key,DateTime.UtcNow);worker.DrainFailures(CancellationToken.None);worker.Status("Auto");
        Assert(Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json")).GetProperty("pending").GetInt32()==1,"An actively used route must refresh before its hard expiry without waiting for a failure.");
        var refreshed=old with{CheckedUtc=DateTime.UtcNow,ExpiresUtc=DateTime.UtcNow.AddMinutes(15)};
        pending.SetResult(new(ProbeOutcome.Reachable,refreshed,1,2,Array.Empty<CandidateTiming>()));await worker.CompletePending();worker.Status("Auto");
        Assert(Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json")).GetProperty("decisions")[0].GetProperty("ExpiresUtc").GetDateTime()>now.AddMinutes(14),"Only a successful refresh may extend the cache TTL.");
        foreach(string change in new[]{"stop","session","manual"})
        {
            pending=new(TaskCreationOptions.RunContinuationsAsynchronously);worker=new(root,session,options,(_,_)=>pending.Task);
            worker.RegisterFailure(key,"one",CancellationToken.None);worker.DrainFailures(CancellationToken.None);
            if(change=="stop")File.WriteAllText(Path.Combine(root,"runtime/stop.request"),"stop");
            if(change=="session")Storage.Write(Path.Combine(root,"runtime/session.json"),new PolicySession{Id="another-session",Running=true});
            if(change=="manual")File.WriteAllText(Path.Combine(root,"force-direct.txt"),key.Domain);
            pending.SetResult(new(ProbeOutcome.Reachable,refreshed,1,2,Array.Empty<CandidateTiming>()));await worker.CompletePending();worker.Status("Auto");
            Assert(Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json")).GetProperty("decisions").GetArrayLength()==0,"A late result must be rejected after "+change);
            File.Delete(Path.Combine(root,"runtime/stop.request"));Storage.Write(Path.Combine(root,"runtime/session.json"),session);File.WriteAllText(Path.Combine(root,"force-direct.txt"),"");
        }
        worker=new(root,session,options);worker.StoreDecision(old);
        File.WriteAllText(Path.Combine(root,"force-direct.txt"),key.Domain);worker.Publish();
        Assert(Storage.Read<JsonElement>(Path.Combine(root,"runtime/rules/auto-vpn.json")).GetProperty("rules").GetArrayLength()==1,"Saving a manual rule must not prematurely remove the previous automatic path before the engine loads the higher-priority manual file.");
        File.WriteAllText(Path.Combine(root,"force-direct.txt"),"");
        var scheduled=new List<string>();
        worker=new(root,session,options,(candidate,_)=>{scheduled.Add(candidate.Domain);return Task.FromResult(new CalibrationResult(ProbeOutcome.Unavailable,null,0,0,Array.Empty<CandidateTiming>()));});
        worker.StoreDecision(old);worker.Touch(key,DateTime.UtcNow);
        for(int i=0;i<6;i++)worker.RegisterFailure(key with{Domain="busy"+i+".example.com"},"hint",CancellationToken.None);
        for(int i=0;i<4;i++){worker.DrainFailures(CancellationToken.None);worker.Status("Auto");Assert(Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json")).GetProperty("pending").GetInt32()<=2,"Refresh and foreground jobs share the same two-job bound.");await worker.CompletePending();}
        Assert(scheduled.Take(5).Contains(key.Domain),"A busy foreground queue must not starve a due refresh.");
        worker=new(root,session,options,(_,_)=>Task.FromResult(new CalibrationResult(ProbeOutcome.Reachable,refreshed with{Route="direct"},1,2,Array.Empty<CandidateTiming>())));
        worker.StoreDecision(old);worker.Touch(key,DateTime.UtcNow);worker.DrainFailures(CancellationToken.None);await worker.CompletePending();worker.Status("Auto");
        var deferred=Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json"));
        Assert(deferred.GetProperty("decisions")[0].GetProperty("Route").GetString()=="vpn" && deferred.GetProperty("measurements")[0].GetProperty("Disposition").GetString()=="AwaitingStableReturn","A preferred return must retain the old binding and expose its deferred result.");
        Assert(deferred.GetProperty("measurements")[0].GetProperty("DecisionCheckedUtc").ValueKind==JsonValueKind.Null,"Deferred evidence must never be marked as published.");
        worker.MaybeStatus("Auto");byte[] statusBefore=File.ReadAllBytes(Path.Combine(root,"runtime/policy-state.json"));
        for(int i=0;i<100;i++)worker.MaybeStatus("Auto");
        Assert(File.ReadAllBytes(Path.Combine(root,"runtime/policy-state.json")).SequenceEqual(statusBefore),"Unchanged background status must not rewrite the snapshot on each poll.");
        worker.MaybeStatus("Error");Assert(Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json")).GetProperty("phase").GetString()=="Error","A phase change bypasses the status throttle.");
        Console.WriteLine("PASS: duplicate-source hint coalescing, make-before-break decisions, stale-result rejection, confidence expiry without blocking and same-epoch active recovery.");
        Console.WriteLine("PASS: proactive refresh extends TTL only on success; Stop, session changes and manual rules reject late results.");

        var timings=new List<long>();
        for(int i=0;i<30;i++)
        {
            var timer=Stopwatch.StartNew();
            selection=await PolicyTransport.Select(async(port,token)=>{await Task.Delay(port==ports.Vpn?3:30,token);return new(port==ports.Vpn,false,"controlled transport",1);},ports,CancellationToken.None);
            Assert(selection.Route=="vpn","Parallel calibration must reach VPN after two direct and DPI failures.");timings.Add(timer.ElapsedMilliseconds);
        }
        timings.Sort();
        Storage.Write(Path.Combine(temporary,"parallel-benchmark.json"),new{kind="controlled scheduling; not live website latency",runs=timings.Count,medianMilliseconds=timings[timings.Count/2],p95Milliseconds=timings[(int)Math.Ceiling(timings.Count*.95)-1],serializedProbeDelayMilliseconds=126});
        Console.WriteLine("PASS: 30 controlled calibrations; median="+timings[15]+"ms p95="+timings[28]+"ms; serialized configured probe delay=126ms (not a live-network speed claim).");
    }
    static async Task Pinning()
    {
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        using var rsa=RSA.Create(2048);
        var request=new CertificateRequest("CN=pinned.example.com",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var san=new SubjectAlternativeNameBuilder();san.AddDnsName("pinned.example.com");request.CertificateExtensions.Add(san.Build());
        using var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddMinutes(5));
        using var certificate=new X509Certificate2(generated.Export(X509ContentType.Pfx));
        string? sni=null,serverError=null;byte[]? target=null;
        var server=Task.Run(async()=>
        {
            try
            {
                using var socket=await listener.AcceptTcpClientAsync(stop.Token);await using var stream=socket.GetStream();
                byte[] hello=new byte[3];await stream.ReadExactlyAsync(hello,stop.Token);await stream.WriteAsync(new byte[]{5,0},stop.Token);
                byte[] command=new byte[10];await stream.ReadExactlyAsync(command,stop.Token);target=command;
                await stream.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,0},stop.Token);
                await using var tls=new SslStream(stream,true);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificateSelectionCallback=(_,name)=>{sni=name;return certificate;}},stop.Token);
            }
            catch(Exception ex)when(ex is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException){serverError=ex.Message;}
        });
        try
        {
            var result=await PolicyTransport.Probe("pinned.example.com",IPAddress.Parse("1.1.1.1"),((IPEndPoint)listener.LocalEndpoint).Port,stop.Token);
            await server;
            Assert(target is {Length:10} && target[3]==1 && target.Skip(4).Take(4).SequenceEqual(new byte[]{1,1,1,1}) && sni=="pinned.example.com" && !result.Reachable && result.Definitive,"The probe must pin IPv4, retain original SNI and reject the certificate. target="+(target==null?"none":Convert.ToHexString(target))+" sni="+sni+" reachable="+result.Reachable+" definitive="+result.Definitive+" detail="+result.Detail+" server="+serverError);
        }
        finally{stop.Cancel();listener.Stop();try{await server;}catch{}}
    }
}
