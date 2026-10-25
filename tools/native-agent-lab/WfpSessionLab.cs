using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using RoutePilot.Agent;

internal static class WfpSessionLab
{
    static void Assert(bool value){if(!value)throw new IOException("WFP/native lifecycle mismatch.");}
    static JsonElement Spec(string config,ulong generation)=>JsonSerializer.SerializeToElement(new{Session=Guid.NewGuid().ToString("N"),Generation=generation,Token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),Config=config});
    internal static async Task<int> Run(string root,string config,string owner)
    {
        bool success=false;string stage="prepare";string? errorType=null;var observations=new List<object>();NativeWfpGuard? guard=null;
        try
        {
            using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(140));var stop=budget.Token;
            using var plan=JsonDocument.Parse(await File.ReadAllTextAsync(config+".wfp",stop));string run=plan.RootElement.GetProperty("Run").GetString()!;
            string image=Path.Combine(root,"native-awg-lab.exe"),guardImage=Path.Combine(root,"native-guard-lab.exe");
            using var reference=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"..","server-egress-reference.json"),stop));string exit=reference.RootElement.GetProperty("serverEgressHash").GetString()!;
            int closedWindows=0;
            await using var runtime=new NativeGuardedSession(image,Path.Combine(root,"native-core-lab.exe"),guardImage,config,owner,run,plan.RootElement.GetProperty("Policy").Clone(),exit,afterStart:async(binding,ct)=>
            {
                using var p=Process.GetProcessById(binding.ProcessId);await File.WriteAllTextAsync(Path.Combine(root,"private","binding-core-pid.json"),JsonSerializer.Serialize(new{pid=p.Id,ticks=p.StartTime.ToUniversalTime().Ticks}),ct);
            },beforeRepair:async ct=>{Assert(await CoreSafetyLab.OsClosed(image,"1.1.1.1",ct));closedWindows++;});
            guard=runtime.Guard;var core=runtime.Core;await runtime.InitializeAsync(stop);
            string text=await File.ReadAllTextAsync(Path.Combine(root,"private","peer.conf"),stop);var first=Spec(text,1);
            stage="activate";await runtime.ConnectAsync(first,stop);var baseline=await CoreSafetyLab.Tun(image,"1.1.1.1",stop);Assert(baseline.GetProperty("success").GetBoolean());
            stage="reject-wrong-binding";var uplink=NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.GetIPProperties().GatewayAddresses.Any(g=>g.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork));
            var wrong=await StartupLab.Probe(guardImage,"--binding-negative-lab",new{Action="BindTunnel",Run=run,ExpectedPolicy=guard.Digest,InterfaceIndex=uplink.GetIPProperties().GetIPv4Properties().Index},stop);
            Assert(!wrong.GetProperty("success").GetBoolean()&&await guard.VerifyAsync(stop));
            var stale=await StartupLab.Probe(guardImage,"--binding-negative-lab",new{Action="CloseTunnel",Run=run,ExpectedPolicy=new string('0',64)},stop);Assert(!stale.GetProperty("success").GetBoolean()&&await guard.VerifyAsync(stop));
            observations.Add(new{phase=stage,physicalInterfaceRefused=true,wrongPolicyRefused=true});
            stage="core-crash";int old=core.ProcessId;using(var p=Process.GetProcessById(old)){p.Kill();await p.WaitForExitAsync(stop);}
            while(core.State is not ("Closed" or "Exhausted" or "GuardLost")&&!(core.ProcessId!=old&&core.State=="Active"))await Task.Delay(100,stop);
            Assert(core.State=="Active"&&core.Repairs==1&&closedWindows==1&&guard.Bindings==2);var restored=await CoreSafetyLab.Tun(image,"1.1.1.1",stop);Assert(restored.GetProperty("success").GetBoolean());
            observations.Add(new{phase=stage,noPhysicalFallback=true,rebound=true,correctExit=restored.GetProperty("egressHash").GetString()==exit});
            stage="stop-and-reconnect";await runtime.PauseAsync(first.GetProperty("Session").GetString()!,1,stop);Assert(await CoreSafetyLab.OsClosed(image,"1.1.1.1",stop));
            var next=Spec(text,2);await runtime.ConnectAsync(next,stop);var connected=await CoreSafetyLab.Tun(image,"1.1.1.1",stop);Assert(connected.GetProperty("success").GetBoolean());
            await runtime.PauseAsync(next.GetProperty("Session").GetString()!,2,stop);Assert(await guard.VerifyAsync(stop));
            observations.Add(new{phase=stage,closedUntilExplicitBind=true,reconnected=true});
            await runtime.DisconnectAsync(stop);observations.Add(new{phase="kernel-job-retirement",allChildrenRetired=true});success=true;
        }
        catch(Exception e){errorType=e.GetType().Name;}
        finally
        {
            if(guard!=null)try{using var clean=new CancellationTokenSource(TimeSpan.FromSeconds(20));await guard.RemoveAsync(clean.Token);}catch{success=false;errorType=(errorType??"")+"/GuardCleanupUnconfirmed";}
        }
        Console.WriteLine(JsonSerializer.Serialize(new{schema=1,success,stage,errorType,guardFailure=guard?.LastFailureStage,guardReason=guard?.LastFailureReason,observations,scope="Shared agent controller, compiled policy and persistent TUN-bound WFP protection"}));return success?0:1;
    }
}
