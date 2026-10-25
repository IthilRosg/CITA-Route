using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using RoutePilot.Agent;

internal static class PersistentControlLab
{
    internal static async Task<int> Run(string root,string config,string owner)
    {
        bool success=false;string stage="prepare";string? errorType=null;var observations=new List<object>();
        try
        {
            using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(150));var stop=budget.Token;
            string image=Path.Combine(root,"native-awg-lab.exe"),peer=await File.ReadAllTextAsync(Path.Combine(root,"private","peer.conf"),stop),group=(await File.ReadAllTextAsync(config+".guard",stop)).Trim();
            if(!System.Text.RegularExpressions.Regex.IsMatch(group,@"^CitaNativeSafety-[a-f0-9]{32}$"))throw new InvalidDataException();
            using var reference=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"..","server-egress-reference.json"),stop));string expected=reference.RootElement.GetProperty("serverEgressHash").GetString()!;
            var spec=JsonSerializer.SerializeToElement(new{Session=Guid.NewGuid().ToString("N"),Generation=1UL,Token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),Config=peer});
            await using var backend=new NativeAwgBackend(image,persistentControl:true);var ready=await backend.StartAsync(spec,stop);int parent=backend.ProcessId;
            await using var recovery=new NativeCoreRecovery(()=>new NativeCoreBinding(Path.Combine(root,"native-core-lab.exe"),config,image,owner,expected,persistentControl:true),ct=>CoreSafetyLab.Guard(group,ct),afterStart:async(binding,ct)=>
            {
                using var identity=Process.GetProcessById(binding.ProcessId);await File.WriteAllTextAsync(Path.Combine(root,"private","binding-core-pid.json"),JsonSerializer.Serialize(new{pid=identity.Id,ticks=identity.StartTime.ToUniversalTime().Ticks}),ct);
            },beforeRepair:_=>throw new IOException("Healthy lease must not trigger repair"),persistentControl:true);
            await recovery.StartAsync(backend,spec,ready,stop);int core=recovery.ProcessId;
            var command=JsonSerializer.SerializeToElement(new{Session=spec.GetProperty("Session").GetString(),Generation=1UL,Action="Snapshot"});
            stage="control-frame-volume";long bytes=0;int child=0;
            for(int i=0;i<256;i++){var response=await backend.SendAsync(command,stop);if(response.GetProperty("epoch").GetInt32()!=1)throw new IOException("Child changed during control volume test");child=response.GetProperty("childPid").GetInt32();bytes+=response.GetRawText().Length;}
            if(bytes<65536)throw new IOException("Reply volume did not cross legacy session bound");
            stage="steady-health-window";await Task.Delay(TimeSpan.FromSeconds(65),stop);
            var final=await backend.SendAsync(command,stop);if(recovery.State!="Active"||recovery.ProcessId!=core||recovery.Repairs!=0||backend.ProcessId!=parent||final.GetProperty("childPid").GetInt32()!=child)throw new IOException("Healthy persistent session retired or repaired");
            var tun=await CoreSafetyLab.Tun(image,"1.1.1.1",stop);if(!tun.GetProperty("success").GetBoolean())throw new IOException("Persistent session lost traffic");
            observations.Add(new{commands=257,replyBytes=bytes,heldSeconds=65,coreRetained=true,backendRetained=true,childRetained=true,noFalseRecovery=true,osTun=tun});success=true;
        }
        catch(Exception e){errorType=e.GetType().Name;}
        Console.WriteLine(JsonSerializer.Serialize(new{schema=1,success,stage,errorType,observations,scope="Bounded per-message persistent ownership pipes, legacy count/byte boundaries crossed; no production service registration"}));return success?0:1;
    }
}
