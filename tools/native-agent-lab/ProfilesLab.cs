using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using RoutePilot.Agent;

internal static class ProfilesLab
{
    static string? failedCheck;
    static readonly List<object> observations=new();
    static void Assert(bool value,string stage){if(!value){failedCheck=stage;throw new IOException(stage);}}
    static string Session(JsonElement spec)=>spec.GetProperty("Session").GetString()!;
    static ulong Generation(JsonElement spec)=>spec.GetProperty("Generation").GetUInt64();
    static string Token(JsonElement spec)=>spec.GetProperty("Token").GetString()!;
    static JsonElement Command(JsonElement spec,string action)=>JsonSerializer.SerializeToElement(new{Session=Session(spec),Generation=Generation(spec),Action=action});
    static JsonElement Spec(string config,ulong generation,string? session=null,string? token=null)=>JsonSerializer.SerializeToElement(new{Session=session??Guid.NewGuid().ToString("N"),Generation=generation,Token=token??Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),Config=config});
    static Process Start(string image,string mode)
    {
        var info=new ProcessStartInfo(image){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add(mode);
        var p=Process.Start(info)??throw new IOException("Probe did not start.");_=p.StandardError.BaseStream.CopyToAsync(Stream.Null);return p;
    }
    static async Task<JsonElement> Probe(string image,string mode,object spec,CancellationToken stop)
    {
        using var p=Start(image,mode);
        try{await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(spec));p.StandardInput.Close();var reply=await NativeAwgBackend.ReadReply(p.StandardOutput,stop);await p.WaitForExitAsync(stop);return reply;}
        finally{if(!p.HasExited){p.Kill();await p.WaitForExitAsync();}}
    }
    static async Task<bool> Refused(Func<Task> action){try{await action();return false;}catch(InvalidDataException){return true;}catch(InvalidOperationException){return true;}}
    static bool Gone(int pid){try{using var p=Process.GetProcessById(pid);return p.HasExited;}catch(ArgumentException){return true;}}
    static JsonElement State(NativeAwgProfiles profiles)=>JsonSerializer.SerializeToElement(profiles.Snapshot());
    static async Task Handoff(string image,string config,string dns,string expected,List<object> cases,CancellationToken stop)
    {
        int starts=0,oldPid=0,oldChild=0;bool retiredBeforeNew=false,holdReply=false;
        var replyEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var replyRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var profiles=new NativeAwgProfiles(image,beforeStart:_=>{if(++starts>1)retiredBeforeNew=Gone(oldPid)&&Gone(oldChild);return Task.CompletedTask;},beforeReply:async token=>{if(holdReply){replyEntered.TrySetResult();await replyRelease.Task.WaitAsync(token);holdReply=false;}});
        var a=Spec(config,1);var readyA=await profiles.SwitchAsync(a,stop);int portA=readyA.GetProperty("port").GetInt32();oldPid=State(profiles).GetProperty("processId").GetInt32();
        var stateA=await profiles.SendAsync(Command(a,"Snapshot"),stop);oldChild=stateA.GetProperty("childPid").GetInt32();
        var goodA=await Probe(image,"--proxy-client",new{Port=portA,Token=Token(a),LiteralIPv4=true},stop);
        observations.Add(new{name="initial-https",success=goodA.GetProperty("success").GetBoolean(),stage=goodA.GetProperty("stage").GetString(),exitMatched=goodA.TryGetProperty("egressHash",out var hashA)&&hashA.GetString()==expected});
        if(!goodA.GetProperty("success").GetBoolean())
        {
            var unchanged=await profiles.SendAsync(Command(a,"Snapshot"),stop);Assert(unchanged.GetProperty("epoch").GetInt32()==1&&State(profiles).GetProperty("processId").GetInt32()==oldPid,"Warmup owner changed");
            await Task.Delay(250,stop);goodA=await Probe(image,"--proxy-client",new{Port=portA,Token=Token(a),LiteralIPv4=true},stop);
            observations.Add(new{name="initial-https-repeat",success=goodA.GetProperty("success").GetBoolean(),stage=goodA.GetProperty("stage").GetString()});
        }
        Assert(goodA.GetProperty("success").GetBoolean()&&goodA.GetProperty("egressHash").GetString()==expected,"Initial HTTPS");
        var hostname=await Probe(image,"--proxy-client",new{Port=portA,Token=Token(a)},stop);
        observations.Add(new{name="separate-hostname-probe",success=hostname.GetProperty("success").GetBoolean(),stage=hostname.GetProperty("stage").GetString(),httpStatus=hostname.TryGetProperty("httpStatus",out var httpStatus)?httpStatus.GetInt32():0});
        using var held=Start(image,"--proxy-retention-client");
        await held.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{Port=portA,Token=Token(a),FaultMilliseconds=3000,LiteralIPv4=true}));var heldReady=await NativeAwgBackend.ReadReply(held.StandardOutput,stop);Assert(heldReady.GetProperty("stage").GetString()=="RetentionClientReady","Held TCP baseline");
        var heldUdp=Start(image,"--proxy-udp-client");
        try
        {
            await heldUdp.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{Port=portA,Token=Token(a),Server=dns,Hold=true}));var udpReady=await NativeAwgBackend.ReadReply(heldUdp.StandardOutput,stop);
            observations.Add(new{name="initial-udp",stage=udpReady.GetProperty("stage").GetString(),queries=udpReady.TryGetProperty("queries",out var queries)?queries.GetInt32():3});
            if(udpReady.GetProperty("stage").GetString()!="UdpClientReady")
            {
                if(!heldUdp.HasExited){heldUdp.Kill();await heldUdp.WaitForExitAsync();}heldUdp.Dispose();
                var unchanged=await profiles.SendAsync(Command(a,"Snapshot"),stop);Assert(unchanged.GetProperty("epoch").GetInt32()==1&&State(profiles).GetProperty("processId").GetInt32()==oldPid,"UDP warmup owner changed");
                await Task.Delay(250,stop);heldUdp=Start(image,"--proxy-udp-client");await heldUdp.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{Port=portA,Token=Token(a),Server=dns,Hold=true}));udpReady=await NativeAwgBackend.ReadReply(heldUdp.StandardOutput,stop);
                observations.Add(new{name="initial-udp-repeat",stage=udpReady.GetProperty("stage").GetString(),queries=udpReady.TryGetProperty("queries",out var repeated)?repeated.GetInt32():3});
            }
            Assert(udpReady.GetProperty("stage").GetString()=="UdpClientReady","Held UDP baseline");
            bool invalidRejected=await Refused(async()=>{await profiles.SwitchAsync(Spec("invalid private configuration",2),stop);});
            Assert(invalidRejected&&State(profiles).GetProperty("processId").GetInt32()==oldPid,"Bad profile disturbed current owner");
            holdReply=true;var delayed=profiles.SendAsync(Command(a,"Snapshot"),stop);await replyEntered.Task.WaitAsync(stop);
            var b=Spec(config,3);var switching=profiles.SwitchAsync(b,stop);replyRelease.TrySetResult();bool lateReplyRejected=await Refused(async()=>{await delayed;});
            var readyB=await switching;int portB=readyB.GetProperty("port").GetInt32();Assert(retiredBeforeNew&&lateReplyRejected,"Retirement or delayed reply fence");
            await held.StandardInput.WriteLineAsync("{\"Continue\":true}");held.StandardInput.Close();var heldResult=await NativeAwgBackend.ReadReply(held.StandardOutput,stop);await held.WaitForExitAsync(stop);Assert(!heldResult.GetProperty("success").GetBoolean()&&!heldResult.GetProperty("sameExit").GetBoolean(),"Retired TCP crossed profiles");
            await heldUdp.StandardInput.WriteLineAsync("{\"Continue\":true}");heldUdp.StandardInput.Close();var oldUdp=await NativeAwgBackend.ReadReply(heldUdp.StandardOutput,stop);await heldUdp.WaitForExitAsync(stop);Assert(oldUdp.GetProperty("success").GetBoolean()&&oldUdp.GetProperty("noReplyAfterClose").GetBoolean(),"Retired UDP crossed profiles");
            bool staleSend=await Refused(async()=>{await profiles.SendAsync(Command(a,"CrashChild"),stop);});
            bool staleStop=await Refused(()=>profiles.StopAsync(Session(a),Generation(a)));
            bool oldTokenRejected=await Refused(async()=>{await profiles.SwitchAsync(Spec(config,4,token:Token(a)),stop);});
            bool oldSessionRejected=await Refused(async()=>{await profiles.SwitchAsync(Spec(config,4,session:Session(a)),stop);});
            var badAuth=await Probe(image,"--proxy-client",new{Port=portB,Token=Token(a),LiteralIPv4=true},stop);
            var goodB=await Probe(image,"--proxy-client",new{Port=portB,Token=Token(b),LiteralIPv4=true},stop);
            var udpB=await Probe(image,"--proxy-udp-client",new{Port=portB,Token=Token(b),Server=dns},stop);
            observations.Add(new{name="new-profile-udp",success=udpB.GetProperty("success").GetBoolean(),stage=udpB.GetProperty("stage").GetString(),queries=udpB.GetProperty("queries").GetInt32(),failure=udpB.TryGetProperty("dnsFailure",out var udpFailure)?udpFailure.GetString():null});
            if(!udpB.GetProperty("success").GetBoolean())
            {
                var same=await profiles.SendAsync(Command(b,"Snapshot"),stop);Assert(same.GetProperty("epoch").GetInt32()==1,"New UDP warmup owner changed");await Task.Delay(250,stop);
                udpB=await Probe(image,"--proxy-udp-client",new{Port=portB,Token=Token(b),Server=dns},stop);
                observations.Add(new{name="new-profile-udp-repeat",success=udpB.GetProperty("success").GetBoolean(),stage=udpB.GetProperty("stage").GetString(),queries=udpB.GetProperty("queries").GetInt32(),failure=udpB.TryGetProperty("dnsFailure",out var repeatedFailure)?repeatedFailure.GetString():null});
            }
            var stateB=await profiles.SendAsync(Command(b,"Snapshot"),stop);
            Assert(staleSend&&staleStop&&oldTokenRejected&&oldSessionRejected,"New profile identity fencing");
            Assert(!badAuth.GetProperty("success").GetBoolean(),"Old credential accepted");
            Assert(goodB.GetProperty("success").GetBoolean()&&goodB.GetProperty("egressHash").GetString()==expected,"New profile HTTPS");
            Assert(udpB.GetProperty("success").GetBoolean(),"New profile UDP");
            Assert(stateB.GetProperty("budgetUsed").GetInt32()==0,"New profile unexpected recovery");
            await profiles.StopAsync(Session(b),Generation(b));Assert(State(profiles).GetProperty("phase").GetString()=="Stopped","Final profile Stop");
            cases.Add(new{name="handoff-and-fencing",success=true,invalidPreservedOld=invalidRejected,oldRetiredBeforeStart=retiredBeforeNew,oldStreamClosed=true,oldUdpClosed=true,staleCommandsRefused=staleSend&&staleStop,lateReplyRejected,credentialsRotated=true,newHttps=true,newUdp=true,samePeerSequentialLeases=true,literalHttpsTarget=true});
        }
        finally{foreach(var p in new[]{held,heldUdp})if(!p.HasExited){p.Kill();await p.WaitForExitAsync();}heldUdp.Dispose();}
    }
    static async Task StopRace(string image,string config,List<object> cases,bool afterStart,CancellationToken stop)
    {
        int calls=0;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Barrier(CancellationToken token){if(++calls>1){entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);}}
        await using var profiles=new NativeAwgProfiles(image,beforeStart:afterStart?null:Barrier,beforePublish:afterStart?Barrier:null);
        var a=Spec(config,10);await profiles.SwitchAsync(a,stop);int oldPid=State(profiles).GetProperty("processId").GetInt32();var old=await profiles.SendAsync(Command(a,"Snapshot"),stop);int child=old.GetProperty("childPid").GetInt32();
        var b=Spec(config,11);Task<JsonElement> switching=profiles.SwitchAsync(b,stop);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(12),stop);Assert(Gone(oldPid)&&Gone(child),"Old owner survived race barrier");
            bool oldStop=await Refused(()=>profiles.StopAsync(Session(a),Generation(a)));Assert(oldStop&&!switching.IsCompleted,"Stale Stop cancelled replacement");
            await profiles.StopAsync(Session(b),Generation(b));bool canceled=false;try{await switching;}catch(OperationCanceledException){canceled=true;}
            var state=State(profiles);Assert(canceled&&state.GetProperty("phase").GetString()=="Stopped"&&state.GetProperty("processId").GetInt32()==0,"Replacement resurrected after Stop");
            var residual=Process.GetProcessesByName("native-awg-lab");int live=0;foreach(var p in residual){try{if(!p.HasExited)live++;}finally{p.Dispose();}}
            observations.Add(new{name=afterStart?"stop-before-publication":"stop-before-start",cancelled=canceled,phase=state.GetProperty("phase").GetString(),activeProcess=state.GetProperty("processId").GetInt32(),processCandidates=residual.Length,liveProcesses=live});
            Assert(live==0,"Unpublished candidate process survived");
            cases.Add(new{name=afterStart?"stop-before-publication":"stop-before-start",success=true,oldRetired=true,staleStopRefused=true,replacementCancelled=true,processesStopped=true});
        }
        finally{if(!switching.IsCompleted){await profiles.StopAsync(Session(b),Generation(b));try{await switching;}catch(OperationCanceledException){}}}
    }
    internal static async Task<int> Run(string root,bool racesOnly=false)
    {
        var cases=new List<object>();bool success=false;string? errorType=null;string stage="prepare";
        try
        {
            using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(100));string image=Path.Combine(root,"native-awg-lab.exe");string config=await File.ReadAllTextAsync(Path.Combine(root,"private","peer.conf"),budget.Token);
            string dns=config.Split('\n').Select(x=>x.Trim()).First(x=>x.StartsWith("DNS",StringComparison.Ordinal)&&x.Contains('=')).Split('=',2)[1].Trim();
            using var reference=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"..","server-egress-reference.json"),budget.Token));string expected=reference.RootElement.GetProperty("serverEgressHash").GetString()!;
            if(!racesOnly){stage="handoff";await Handoff(image,config,dns,expected,cases,budget.Token);}stage="stop-before-start";await StopRace(image,config,cases,false,budget.Token);stage="stop-before-publication";await StopRace(image,config,cases,true,budget.Token);success=true;
        }
        catch(Exception e){errorType=e.GetType().Name;}
        Console.WriteLine(JsonSerializer.Serialize(new{schema=1,success,scope="Backend-only sequential leases using one VM peer; no core/TUN profile rebinding",cases,observations,errorType,failedStage=success?null:stage,failedCheck}));return success?0:1;
    }
}
