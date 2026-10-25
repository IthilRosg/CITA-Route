using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using RoutePilot.Agent;

internal static class NativeSessionLab
{
    static void Assert(bool value){if(!value)throw new IOException("Native session lifecycle mismatch");}
    static JsonElement Spec(string text,ulong generation)=>JsonSerializer.SerializeToElement(new{Session=Guid.NewGuid().ToString("N"),Generation=generation,Token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),Config=text});
    internal static async Task<int> Run(string root,string config,string owner)
    {
        bool success=false;string stage="prepare";string? errorType=null;var observations=new List<object>();
        try
        {
            using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(130));var stop=budget.Token;string image=Path.Combine(root,"native-awg-lab.exe");
            string a=await File.ReadAllTextAsync(Path.Combine(root,"private","peer.conf"),stop),b=await File.ReadAllTextAsync(Path.Combine(root,"private","second-server.conf"),stop);
            string group=(await File.ReadAllTextAsync(config+".guard",stop)).Trim();if(!System.Text.RegularExpressions.Regex.IsMatch(group,@"^CitaNativeSafety-[a-f0-9]{32}$"))throw new InvalidDataException();
            using var reference=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"..","server-egress-reference.json"),stop));string firstExit=reference.RootElement.GetProperty("serverEgressHash").GetString()!,secondExit=(await File.ReadAllTextAsync(Path.Combine(root,"private","second-server-egress.txt"),stop)).Trim();Assert(!firstExit.Equals(secondExit,StringComparison.OrdinalIgnoreCase));
            int failures=0;var repairEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var core=new NativeCoreRecovery(()=>new NativeCoreBinding(Path.Combine(root,"native-core-lab.exe"),config,image,owner,firstExit,expectedEgress:s=>s.GetProperty("Generation").GetUInt64()==2?secondExit:firstExit,persistentControl:true),ct=>CoreSafetyLab.Guard(group,ct),afterStart:async(binding,ct)=>
            {
                using var identity=Process.GetProcessById(binding.ProcessId);await File.WriteAllTextAsync(Path.Combine(root,"private","binding-core-pid.json"),JsonSerializer.Serialize(new{pid=identity.Id,ticks=identity.StartTime.ToUniversalTime().Ticks}),ct);
            },beforeRepair:async ct=>
            {
                Assert(await CoreSafetyLab.OsClosed(image,"1.1.1.1",ct));
                if(++failures==2){repairEntered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);}
            },persistentControl:true);
            await core.StartControllerAsync(stop);await using var profiles=new NativeAwgProfiles(image,binding:core,persistentControl:true);
            var profileA=Spec(a,1);stage="start-A";await profiles.SwitchAsync(profileA,stop);int stableCore=core.ProcessId;var baseline=await CoreSafetyLab.Tun(image,"1.1.1.1",stop);Assert(baseline.GetProperty("success").GetBoolean());
            var profileB=Spec(b,2);stage="switch-independent-B";
            var moved=await CoreSafetyLab.Tun(image,"1.1.1.1",stop,async()=>{await profiles.SwitchAsync(profileB,stop);});Assert(moved.GetProperty("udpAfter").GetInt32()==3&&core.ProcessId==stableCore);
            var fresh=await CoreSafetyLab.Tun(image,"1.1.1.1",stop);Assert(fresh.GetProperty("success").GetBoolean()&&fresh.GetProperty("egressHash").GetString()==secondExit);
            stage="recover-core-B";using(var p=Process.GetProcessById(stableCore)){p.Kill();await p.WaitForExitAsync(stop);}
            while(core.State is not ("Exhausted" or "Closed")&&!(core.ProcessId!=stableCore&&core.State=="Active"))await Task.Delay(100,stop);
            Assert(core.State=="Active"&&core.Repairs==1);var restored=await CoreSafetyLab.Tun(image,"1.1.1.1",stop);Assert(restored.GetProperty("success").GetBoolean()&&restored.GetProperty("egressHash").GetString()==secondExit);
            observations.Add(new{phase=stage,selectedProfileRetained=true,correctExit=true,repairs=core.Repairs});
            stage="stop-during-repair";using(var p=Process.GetProcessById(core.ProcessId)){p.Kill();await p.WaitForExitAsync(stop);};await repairEntered.Task.WaitAsync(TimeSpan.FromSeconds(20),stop);
            var clock=Stopwatch.StartNew();await profiles.StopAsync(profileB.GetProperty("Session").GetString()!,2);await Task.Delay(700,stop);Assert(core.ProcessId==0&&core.State=="Closed"&&await CoreSafetyLab.OsClosed(image,"1.1.1.1",stop));
            observations.Add(new{phase=stage,stopMs=clock.ElapsedMilliseconds,noResurrection=true});
            stage="restart-after-stop";var again=Spec(a,3);await profiles.SwitchAsync(again,stop);var reconnected=await CoreSafetyLab.Tun(image,"1.1.1.1",stop);Assert(reconnected.GetProperty("success").GetBoolean()&&reconnected.GetProperty("egressHash").GetString()==firstExit);
            bool stale=false;try{await profiles.StopAsync(profileB.GetProperty("Session").GetString()!,2);}catch(InvalidDataException){stale=true;}Assert(stale&&core.State=="Active");
            await profiles.StopAsync(again.GetProperty("Session").GetString()!,3);Assert(core.State=="Closed");observations.Add(new{phase=stage,correctExit=true,staleStopRefused=true});success=true;
        }
        catch(Exception e){errorType=e.GetType().Name;}
        Console.WriteLine(JsonSerializer.Serialize(new{schema=1,success,stage,errorType,observations,scope="Shared agent profile lifecycle and automatic core recovery under one serialized controller; VM-only registration"}));return success?0:1;
    }
}
