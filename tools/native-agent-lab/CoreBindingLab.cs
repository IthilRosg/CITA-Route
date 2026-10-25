using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using RoutePilot.Agent;

internal static class CoreBindingLab
{
    static string stage="prepare";
    static void Assert(bool value,string label){if(!value){stage=label;throw new IOException("Core binding acceptance failed.");}}
    static string Session(JsonElement spec)=>spec.GetProperty("Session").GetString()!;
    static ulong Generation(JsonElement spec)=>spec.GetProperty("Generation").GetUInt64();
    static JsonElement Command(JsonElement spec,string action)=>JsonSerializer.SerializeToElement(new{Session=Session(spec),Generation=Generation(spec),Action=action});
    static JsonElement Spec(string config,ulong generation)=>JsonSerializer.SerializeToElement(new{Session=Guid.NewGuid().ToString("N"),Generation=generation,Token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),Config=config});
    static Process Start(string image,string mode)
    {
        var info=new ProcessStartInfo(image){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add(mode);
        var p=Process.Start(info)??throw new IOException("Acceptance process did not start.");_=p.StandardError.BaseStream.CopyToAsync(Stream.Null);return p;
    }
    static async Task<JsonElement> Tun(string native,string dns,Func<Task>? transition,CancellationToken stop)
    {
        using var p=Start(native,"--tun-client");
        try
        {
            await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{DnsServer=dns}));var ready=await NativeAwgBackend.ReadReply(p.StandardOutput,stop);
            Assert(ready.GetProperty("stage").GetString()=="RetentionClientReady","OS TUN warmup");
            if(transition!=null)await transition();await p.StandardInput.WriteLineAsync("{\"Continue\":true}");p.StandardInput.Close();var result=await NativeAwgBackend.ReadReply(p.StandardOutput,stop);await p.WaitForExitAsync(stop);return result;
        }
        finally{if(!p.HasExited){p.Kill();await p.WaitForExitAsync();}}
    }
    static async Task<bool> Closed(string native,string dns,CancellationToken stop)
    {
        var info=new ProcessStartInfo(@"C:\Windows\System32\curl.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{"--noproxy","*","--connect-timeout","2","--max-time","4","--silent","--output","NUL","https://1.1.1.1/cdn-cgi/trace"})info.ArgumentList.Add(arg);
        using var p=Process.Start(info)!;_=p.StandardOutput.BaseStream.CopyToAsync(Stream.Null);_=p.StandardError.BaseStream.CopyToAsync(Stream.Null);await p.WaitForExitAsync(stop);
        using var udp=Start(native,"--os-dns-probe");await udp.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{Server=dns}));udp.StandardInput.Close();var result=await NativeAwgBackend.ReadReply(udp.StandardOutput,stop);await udp.WaitForExitAsync(stop);
        return p.ExitCode!=0&&udp.ExitCode==1&&!result.GetProperty("success").GetBoolean();
    }
    static async Task RouteReady(CancellationToken stop)
    {
        var info=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{"-NoProfile","-NonInteractive","-Command","for($i=0;$i -lt 50;$i++){if(Get-NetRoute -InterfaceAlias Cita-NativeLab -DestinationPrefix 1.1.1.1/32 -ErrorAction SilentlyContinue){exit 0};Start-Sleep -Milliseconds 200};exit 1"})info.ArgumentList.Add(arg);
        using var p=Process.Start(info)!;_=p.StandardOutput.BaseStream.CopyToAsync(Stream.Null);_=p.StandardError.BaseStream.CopyToAsync(Stream.Null);await p.WaitForExitAsync(stop);Assert(p.ExitCode==0,"Scoped TUN route readiness");
    }
    internal static async Task<int> Run(string root,string configPath,string owner,bool twoServers=false)
    {
        var observations=new List<object>();bool success=false;string? errorType=null;
        Process? direct=null;
        try
        {
            using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(85));var stop=budget.Token;
            string native=Path.Combine(root,"native-awg-lab.exe"),core=Path.Combine(root,"native-core-lab.exe"),peer=await File.ReadAllTextAsync(Path.Combine(root,"private","peer.conf"),stop);
            string dns=Regex.Match(peer,@"(?mi)^\s*DNS\s*=\s*([0-9.]+)").Groups[1].Value;
            using var reference=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"..","server-egress-reference.json"),stop));string expected=reference.RootElement.GetProperty("serverEgressHash").GetString()!;
            string secondPeer=twoServers?await File.ReadAllTextAsync(Path.Combine(root,"private","second-server.conf"),stop):peer;
            string secondExit=twoServers?(await File.ReadAllTextAsync(Path.Combine(root,"private","second-server-egress.txt"),stop)).Trim():expected;
            var activationEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var binding=new NativeCoreBinding(core,configPath,native,owner,expected,quarantined:async(ack,ct)=>
            {
                if(ack.GetProperty("generation").GetUInt64()>=2)
                {
                    bool closed=await Closed(native,dns,ct);observations.Add(new{phase="quarantine",generation=ack.GetProperty("generation").GetUInt64(),closed,tcpClosed=ack.GetProperty("tcpClosed").GetInt32(),udpClosed=ack.GetProperty("udpClosed").GetInt32()});Assert(closed,"Protected traffic escaped quarantine");
                }
            },beforeValidation:async(candidate,spec,ct)=>
            {
                if(Generation(spec)==4){activationEntered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);}
                if(Generation(spec)!=3)return;
                for(int i=0;i<3;i++)
                {
                    await candidate.SendAsync(Command(spec,"CrashChild"),ct);
                    for(int n=0;n<30;n++){var state=await candidate.SendAsync(Command(spec,"Snapshot"),ct);string phase=state.GetProperty("state").GetString()!;if(phase=="Exhausted"||state.GetProperty("epoch").GetInt32()==i+2&&phase=="RunningUnverified")break;await Task.Delay(100,ct);}
                }
                var exhausted=await candidate.SendAsync(Command(spec,"Snapshot"),ct);Assert(exhausted.GetProperty("state").GetString()=="Exhausted","Failure candidate not exhausted");
            },expectedEgress:spec=>Generation(spec)==2?secondExit:expected);
            stage="core-start";await binding.StartAsync(stop);int corePid=binding.ProcessId;
            using(var identity=Process.GetProcessById(corePid)){await File.WriteAllTextAsync(Path.Combine(root,"private","binding-core-pid.json"),JsonSerializer.Serialize(new{pid=corePid,ticks=identity.StartTime.ToUniversalTime().Ticks}),stop);}
            await RouteReady(stop);Assert(await Closed(native,dns,stop),"Initially closed outbound bypassed");
            await using var profiles=new NativeAwgProfiles(native,binding:binding);
            var a=Spec(peer,1);stage="activate-A";var readyA=await profiles.SwitchAsync(a,stop);
            var baseline=await Tun(native,dns,null,stop);Assert(baseline.GetProperty("success").GetBoolean(),"Profile A TUN traffic");
            direct=Start(native,"--proxy-retention-client");await direct.StandardInput.WriteLineAsync("{\"Port\":20939,\"FaultMilliseconds\":3000,\"LiteralIPv4\":true,\"HoldForFinal\":true}");var directReady=await NativeAwgBackend.ReadReply(direct.StandardOutput,stop);Assert(directReady.GetProperty("stage").GetString()=="RetentionClientReady","Direct baseline");
            var b=Spec(secondPeer,2);stage="switch-B";var old=await Tun(native,dns,async()=>{try{await profiles.SwitchAsync(b,stop);}catch{observations.Add(new{phase="candidate-rejection",validation=binding.LastValidation});throw;}},stop);
            Assert(old.GetProperty("dialsAfter").GetInt64()>1&&old.GetProperty("udpAfter").GetInt32()==3,"Old VPN flow did not rebind");
            var stale=await binding.CommandAsync("Activate",Session(a),Generation(a),readyA.GetProperty("port").GetInt32(),a.GetProperty("Token").GetString()!,false,stop);Assert(!stale.GetProperty("success").GetBoolean()&&stale.GetProperty("currentGeneration").GetUInt64()==2&&stale.GetProperty("state").GetString()=="Active","Stale core Activate changed B");
            bool refusedAsError=false;try{await binding.CommandAsync("Activate",Session(a),Generation(a),readyA.GetProperty("port").GetInt32(),a.GetProperty("Token").GetString()!,true,stop);}catch(InvalidDataException){refusedAsError=true;}Assert(refusedAsError,"Core refusal was not reported");
            var goodB=await Tun(native,dns,null,stop);Assert(goodB.GetProperty("success").GetBoolean(),"Profile B TUN traffic");
            Assert(string.Equals(goodB.GetProperty("egressHash").GetString(),secondExit,StringComparison.OrdinalIgnoreCase),"Profile B exit mismatch");
            await direct.StandardInput.WriteLineAsync("{\"Continue\":true}");var directMiddle=await NativeAwgBackend.ReadReply(direct.StandardOutput,stop);Assert(directMiddle.GetProperty("stage").GetString()=="RetentionAfterLoss","Direct failed at B");
            var c=Spec(peer,3);bool refused=false;stage="reject-C";
            var rejected=await Tun(native,dns,async()=>{try{await profiles.SwitchAsync(c,stop);}catch(InvalidDataException){refused=true;}},stop);
            Assert(refused&&!rejected.GetProperty("sameExit").GetBoolean()&&binding.LastState.GetProperty("state").GetString()=="Closed","Failed candidate was activated or rolled back");
            Assert(await Closed(native,dns,stop),"Failed candidate leaked protected traffic");await profiles.StopAsync(Session(c),Generation(c));
            var d=Spec(peer,4);stage="cancel-before-activation";var pending=profiles.SwitchAsync(d,stop);await activationEntered.Task.WaitAsync(TimeSpan.FromSeconds(15),stop);
            Assert(binding.LastState.GetProperty("state").GetString()=="Quarantined","Candidate was not quarantined before validation");await profiles.StopAsync(Session(d),Generation(d));bool canceled=false;try{await pending;}catch(OperationCanceledException){canceled=true;}
            var delayed=await binding.CommandAsync("Activate",Session(d),Generation(d),readyA.GetProperty("port").GetInt32(),d.GetProperty("Token").GetString()!,false,stop);
            Assert(canceled&&!delayed.GetProperty("success").GetBoolean()&&delayed.GetProperty("state").GetString()=="Closed","Cancelled activation resurrected");
            observations.Add(new{phase="cancelled-activation",generation=4,closed=true,lateActivateRefused=true});
            await direct.StandardInput.WriteLineAsync("{\"Continue\":true}");direct.StandardInput.Close();var directFinal=await NativeAwgBackend.ReadReply(direct.StandardOutput,stop);await direct.WaitForExitAsync(stop);
            Assert(directFinal.GetProperty("success").GetBoolean()&&directFinal.GetProperty("dialsAfter").GetInt64()==1,"Direct TCP reset during handoff");
            Assert(binding.ProcessId==corePid,"Core process replaced");await RouteReady(stop);
            observations.Add(new{phase="accepted",coreProcessRetained=true,profileATraffic=true,profileBTraffic=true,staleActivateRefused=true,failedCandidateClosed=true,directTcpDials=1,samePeerSequentialLeases=!twoServers,distinctServerProfiles=twoServers});success=true;
        }
        catch(Exception e){errorType=e.GetType().Name;}
        finally{if(direct!=null){if(!direct.HasExited){direct.Kill();await direct.WaitForExitAsync();}direct.Dispose();}}
        Console.WriteLine(JsonSerializer.Serialize(new{schema=1,success,failedStage=success?null:stage,errorType,observations}));return success?0:1;
    }
}
