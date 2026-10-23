using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RoutePilot.Agent;

internal static class PolicyFirstUseTests
{
    internal static async Task Run()
    {
        static void Check(bool condition,string message){if(!condition)throw new IOException(message);}
        var poll=new PolicyPollBudget();Check(poll.Take(100)&&!poll.Take(200)&&poll.Take(600)&&poll.Take(1),"Fast gate wakeups must not poll the full connection API at 40 Hz; rollback must recover.");
        string root=Path.Combine(Path.GetTempPath(),"CitaFirstUse-"+Guid.NewGuid().ToString("N"));
        foreach(var part in new[]{"runtime/rules","data","private"})Directory.CreateDirectory(Path.Combine(root,part));
        try
        {
            File.WriteAllText(Path.Combine(root,"force-direct.txt"),"");File.WriteAllText(Path.Combine(root,"force-vpn.txt"),"");
            const string secret="0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            File.WriteAllText(Path.Combine(root,"private/controller-token.txt"),secret);
            Storage.Write(Path.Combine(root,"runtime/bridge.json"),new{outbounds=new[]{new{bind_interface=NetworkInterface.GetAllNetworkInterfaces().First().Name}}});
            var session=new PolicySession{Id="gate-test",Running=true,RouterPid=Environment.ProcessId,BridgeGeneration=1};
            Storage.Write(Path.Combine(root,"runtime/session.json"),session);
            var worker=new PolicyWorker(root,session,new(){Mode="Auto",FirstUseGate=true});
            var key=new PolicyKey(worker.FirstUseEpoch,"gate.fixture.invalid",Path.Combine(Environment.SystemDirectory,"cmd.exe"),443);
            var request=new PolicyFirstUse.Request(1,Guid.NewGuid().ToString("N"),session.RouterPid,key.Domain,key.Application,"");
            request=request with{Mac=PolicyFirstUse.Sign(secret,PolicyFirstUse.RequestText(request))};
            Check(PolicyFirstUse.Authentic(secret,request),"Valid authenticated metadata must pass.");
            Check(!PolicyFirstUse.Authentic(secret,request with{Domain="other.invalid"})&&!PolicyFirstUse.Authentic(secret,request with{Mac=new string('0',64)}),"Tampering must fail.");
            var ready=new TaskCompletionSource<CandidateTiming>(TaskCreationOptions.RunContinuationsAsynchronously);
            using(var cancel=new CancellationTokenSource(2000))
            {
                int vpnCalls=0;
                var selection=PolicyTransport.Select(async(port,ct)=>
                {
                    if(port==20808){Interlocked.Increment(ref vpnCalls);return new CandidateResult(true,true,"body",1){Evidence="BodySample",BodyBytes=65536};}
                    await Task.Delay(Timeout.Infinite,ct);throw new IOException("unreachable");
                },new(),cancel.Token,candidateReady:proof=>{if(proof.Route=="vpn")ready.TrySetResult(proof);});
                var proof=await ready.Task.WaitAsync(cancel.Token);
                Check(vpnCalls==2&&proof.Evidence=="BodySample"&&!selection.IsCompleted,"Early proof requires two requests and can precede the full preferred-path decision.");
                cancel.Cancel();try{await selection;}catch(OperationCanceledException){}
            }
            await using var gate=new PolicyFirstUse(worker,root,session,0);
            using var client=new TcpClient();await client.ConnectAsync("127.0.0.1",gate.Port);
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request,Storage.Wire)+"\n"));
            using var budget=new CancellationTokenSource(4000);
            await gate.Signal.WaitAsync(budget.Token);gate.Pump();
            Check(gate.Accepted==1,"Trusted request must be admitted once.");
            gate.Candidate(key,worker.FirstUseGeneration,1,new("vpn",1,ProbeOutcome.Reachable){Evidence="Headers"});gate.Pump();
            Check(gate.Proven==0,"Headers cannot release a body-verified route.");
            gate.Candidate(key,worker.FirstUseGeneration-1,1,new("vpn",1,ProbeOutcome.Reachable){Evidence="BodySample"});gate.Pump();
            Check(gate.Proven==0,"Old generation proof must be refused.");
            gate.Candidate(key,worker.FirstUseGeneration,0,new("vpn",1,ProbeOutcome.Reachable){Evidence="BodySample"});gate.Pump();
            Check(gate.Proven==0,"Old bridge proof must be refused.");
            gate.Candidate(key,worker.FirstUseGeneration,1,new("vpn",1,ProbeOutcome.Reachable){Evidence="BodySample"});
            await Task.Delay(320,budget.Token);gate.Pump();
            using var reader=new StreamReader(client.GetStream());string? line=await reader.ReadLineAsync(budget.Token);
            using var response=JsonDocument.Parse(line!);string route=response.RootElement.GetProperty("Route").GetString()!;
            string epoch=response.RootElement.GetProperty("Epoch").GetString()!;
            Check(route=="vpn"&&gate.Proven==1,"Independent body evidence releases the first flow without user request replay.");
            Check(response.RootElement.GetProperty("Mac").GetString()==PolicyFirstUse.Sign(secret,$"1\n{request.Nonce}\n{request.RouterPid}\nvpn\n{epoch}"),"Reply must authenticate nonce, process, route and epoch.");
            using(var next=new TcpClient())
            {
                var nextRequest=request with{Nonce=Guid.NewGuid().ToString("N")};nextRequest=nextRequest with{Mac=PolicyFirstUse.Sign(secret,PolicyFirstUse.RequestText(nextRequest))};
                await next.ConnectAsync("127.0.0.1",gate.Port);
                await next.GetStream().WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(nextRequest,Storage.Wire)+"\n"));
                await gate.Signal.WaitAsync(budget.Token);gate.Pump();
                using var nextReader=new StreamReader(next.GetStream());string? nextLine=await nextReader.ReadLineAsync(budget.Token);
                using var nextReply=JsonDocument.Parse(nextLine!);
                Check(nextReply.RootElement.GetProperty("Route").GetString()=="vpn","Subresources arriving before final rule publication must reuse the recent independently proven path.");
            }
            worker.Clear();Check(!worker.FirstUseEligible(key with{Epoch="old"}),"Old session key cannot enter gate.");
            worker.StoreDecision(new(key,"direct","Only the root headers were confirmed",DateTime.UtcNow,DateTime.UtcNow.AddMinutes(1),1));
            using(var neutral=new TcpClient())
            {
                var neutralRequest=request with{Nonce=Guid.NewGuid().ToString("N")};neutralRequest=neutralRequest with{Mac=PolicyFirstUse.Sign(secret,PolicyFirstUse.RequestText(neutralRequest))};
                await neutral.ConnectAsync("127.0.0.1",gate.Port);
                await neutral.GetStream().WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(neutralRequest,Storage.Wire)+"\n"));
                await gate.Signal.WaitAsync(budget.Token);gate.Pump();
                using var neutralReader=new StreamReader(neutral.GetStream());string? neutralLine=await neutralReader.ReadLineAsync(budget.Token);
                using var neutralReply=JsonDocument.Parse(neutralLine!);
                Check(neutralReply.RootElement.GetProperty("Route").GetString()==""&&gate.DefaultSettled==1,"A neutral root response releases the ordinary default without pretending body proof or retaining old-generation VPN evidence.");
            }
            var now=DateTime.UtcNow;
            var readySession=new QualitySession{Running=true,Id=session.Id,BridgeGeneration=1,BridgeReady=true};
            var healthy=new QualityHealth{SessionId=session.Id,BridgeGeneration=1,Healthy=true,UpdatedUtc=now};
            var watching=new QualityHealth{SessionId=session.Id,WireSockState="Connected",UpdatedUtc=now};
            void HealthFiles(){Storage.Write(Path.Combine(root,"runtime/session.json"),readySession);Storage.Write(Path.Combine(root,"runtime/session-health.json"),healthy);Storage.Write(Path.Combine(root,"runtime/watch-state.json"),watching);}
            HealthFiles();Check(PolicyFirstUse.HealthyVpn(root,session.Id,1,now),"Current verified transport can carry a provisional first flow.");
            healthy.UpdatedUtc=now.AddSeconds(-91);HealthFiles();Check(!PolicyFirstUse.HealthyVpn(root,session.Id,1,now),"Stale HTTPS cannot authorize provisional transport.");
            healthy.UpdatedUtc=now.AddSeconds(3);HealthFiles();Check(!PolicyFirstUse.HealthyVpn(root,session.Id,1,now),"Future health must fail closed.");
            healthy.UpdatedUtc=now;healthy.BridgeGeneration=0;HealthFiles();Check(!PolicyFirstUse.HealthyVpn(root,session.Id,1,now),"Old bridge health cannot authorize provisional transport.");
            healthy.BridgeGeneration=1;healthy.SessionId="old";HealthFiles();Check(!PolicyFirstUse.HealthyVpn(root,session.Id,1,now),"Old session health cannot authorize provisional transport.");
            healthy.SessionId=session.Id;healthy.Healthy=false;HealthFiles();Check(!PolicyFirstUse.HealthyVpn(root,session.Id,1,now),"Failed HTTPS must refuse provisional transport.");
            healthy.Healthy=true;watching.UpdatedUtc=now.AddSeconds(-26);HealthFiles();Check(!PolicyFirstUse.HealthyVpn(root,session.Id,1,now),"Stale watchdog must refuse provisional transport.");
            watching.UpdatedUtc=now;readySession.Recovering=true;HealthFiles();Check(!PolicyFirstUse.HealthyVpn(root,session.Id,1,now),"Recovery must refuse provisional transport.");
            readySession.Recovering=false;HealthFiles();
            string safety=Path.Combine(root,"runtime/bridge-safety.json");
            Storage.Write(safety,new BridgeSafety{SessionId=session.Id,Hold=true});Check(!PolicyFirstUse.HealthyVpn(root,session.Id,1,now),"Safety hold overrides healthy transport.");
            File.WriteAllText(safety,"broken");Check(!PolicyFirstUse.HealthyVpn(root,session.Id,1,now),"Malformed safety state cannot authorize fallback.");File.Delete(safety);
            using(var provisional=new TcpClient())
            {
                var provisionalRequest=request with{Nonce=Guid.NewGuid().ToString("N"),Domain="provisional.fixture.invalid"};
                provisionalRequest=provisionalRequest with{Mac=PolicyFirstUse.Sign(secret,PolicyFirstUse.RequestText(provisionalRequest))};
                await provisional.ConnectAsync("127.0.0.1",gate.Port);
                await provisional.GetStream().WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(provisionalRequest,Storage.Wire)+"\n"));
                using var fallbackBudget=new CancellationTokenSource(4000);
                await gate.Signal.WaitAsync(fallbackBudget.Token);gate.Pump();await Task.Delay(1050,fallbackBudget.Token);gate.Pump();
                using var provisionalReader=new StreamReader(provisional.GetStream());
                using var provisionalReply=JsonDocument.Parse((await provisionalReader.ReadLineAsync(fallbackBudget.Token))!);
                Check(provisionalReply.RootElement.GetProperty("Route").GetString()=="vpn"&&gate.ProvisionalVpn==1&&gate.Proven==2,"Bounded transport fallback must be counted separately from domain proof.");
                Check(worker.FirstUseApplied(key with{Domain=provisionalRequest.Domain})==null,"Provisional transport must not persist a proven domain route.");
            }
            Storage.Write(Path.Combine(root,"runtime/session.json"),session);
            File.WriteAllText(Path.Combine(root,"force-direct.txt"),"fixture.invalid");Check(!worker.FirstUseEligible(key),"Manual rule overrides gate.");
        }
        finally{Directory.Delete(root,true);}
        Console.WriteLine("PASS: first-use metadata/reply authentication, body-only proof, generation/bridge isolation, bounded admission and manual priority.");
    }
}
