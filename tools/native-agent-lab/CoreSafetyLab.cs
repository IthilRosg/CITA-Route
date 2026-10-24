using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using RoutePilot.Agent;

// VM-only experiment. Destination-scoped physical-NIC firewall rules are armed
// independently by the guest runner before the first core process starts.
internal static class CoreSafetyLab
{
    [DllImport("ntdll.dll")]static extern int NtSuspendProcess(IntPtr process);
    static void Assert(bool value){if(!value)throw new IOException("Core safety gate failed");}
    static async Task<bool> OsClosed(string native,string dns,CancellationToken stop)
    {
        var info=new ProcessStartInfo(@"C:\Windows\System32\curl.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{"--noproxy","*","--connect-timeout","2","--max-time","3","--silent","--output","NUL","https://1.1.1.1/cdn-cgi/trace"})info.ArgumentList.Add(arg);
        using var p=Process.Start(info)!;_=p.StandardOutput.BaseStream.CopyToAsync(Stream.Null);_=p.StandardError.BaseStream.CopyToAsync(Stream.Null);
        var udpTask=StartupLab.Probe(native,"--os-dns-probe",new{Server=dns},stop);await p.WaitForExitAsync(stop);var udp=await udpTask;return p.ExitCode!=0&&!udp.GetProperty("success").GetBoolean();
    }
    internal static async Task<JsonElement> Tun(string image,string dns,CancellationToken stop,Func<Task>? transition=null)
    {
        var info=new ProcessStartInfo(image){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add("--tun-client");
        using var p=Process.Start(info)!;_=p.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{DnsServer=dns}));
            var first=await NativeAwgBackend.ReadReply(p.StandardOutput,stop);
            if(first.GetProperty("stage").GetString()!="RetentionClientReady")return first;
            if(transition!=null)await transition();
            await p.StandardInput.WriteLineAsync("{\"Continue\":true}");p.StandardInput.Close();var final=await NativeAwgBackend.ReadReply(p.StandardOutput,stop);await p.WaitForExitAsync(stop);return final;
        }
        finally{if(!p.HasExited){p.Kill(true);await p.WaitForExitAsync();}}
    }
    static async Task<bool> Guard(string group,CancellationToken stop)
    {
        var info=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        string code="$r=@(Get-NetFirewallRule -Group '"+group+"' -ErrorAction SilentlyContinue|Where-Object {$_.Enabled -eq 'True' -and $_.Direction -eq 'Outbound' -and $_.Action -eq 'Block'});if($r.Count -eq 1){exit 0};exit 1";
        foreach(string arg in new[]{"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(code))})info.ArgumentList.Add(arg);
        using var p=Process.Start(info)!;_=p.StandardOutput.BaseStream.CopyToAsync(Stream.Null);_=p.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try{await p.WaitForExitAsync(stop);return p.ExitCode==0;}finally{if(!p.HasExited){p.Kill();await p.WaitForExitAsync();}}
    }
    internal static async Task<int> Run(string root,string config,string owner)
    {
        var observations=new List<object>();bool success=false;string stage="prepare";string? errorType=null;
        try
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(140));var stop=deadline.Token;
            string native=Path.Combine(root,"native-awg-lab.exe"),core=Path.Combine(root,"native-core-lab.exe"),peer=await File.ReadAllTextAsync(Path.Combine(root,"private","peer.conf"),stop);
            string dns=Regex.Match(peer,@"(?mi)^DNS\s*=\s*([0-9.]+)").Groups[1].Value;
            string group=(await File.ReadAllTextAsync(config+".guard",stop)).Trim();Assert(Regex.IsMatch(group,@"^CitaNativeSafety-[a-f0-9]{32}$"));
            using var reference=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"..","server-egress-reference.json"),stop));string expected=reference.RootElement.GetProperty("serverEgressHash").GetString()!;
            string session=Guid.NewGuid().ToString("N");var spec=JsonSerializer.SerializeToElement(new{Session=session,Generation=1UL,Token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),Config=peer});
            await using var backend=new NativeAwgBackend(native);var ready=await backend.StartAsync(spec,stop);int backendPid=backend.ProcessId;var faultClock=new Stopwatch();
            await using var recovery=new NativeCoreRecovery(()=>new NativeCoreBinding(core,config,native,owner,expected),ct=>Guard(group,ct),afterStart:async(binding,ct)=>
            {
                using var identity=Process.GetProcessById(binding.ProcessId);await File.WriteAllTextAsync(Path.Combine(root,"private","binding-core-pid.json"),JsonSerializer.Serialize(new{pid=identity.Id,ticks=identity.StartTime.ToUniversalTime().Ticks}),ct);
            },beforeRepair:async ct=>
            {
                long detectionMs=faultClock.ElapsedMilliseconds;bool closed=await OsClosed(native,dns,ct);Assert(closed);observations.Add(new{phase="repair-quarantine",detectionMs,protectedHttpsAndUdpClosed=closed});
            });
            await recovery.StartAsync(backend,spec,ready,stop);
            for(int phase=0;phase<3;phase++)
            {
                stage=phase==0?"core-crash":phase==1?"core-hang":"budget-exhaustion";
                var probe=await Tun(native,dns,stop);Assert(probe.GetProperty("success").GetBoolean());int oldPid=recovery.ProcessId;faultClock.Restart();
                using(var process=Process.GetProcessById(oldPid)){if(phase==1)Assert(NtSuspendProcess(process.Handle)==0);else{process.Kill();await process.WaitForExitAsync(stop);}}
                while(recovery.State is not ("Closed" or "Exhausted") && !(recovery.ProcessId!=oldPid&&recovery.State=="Active"))await Task.Delay(100,stop);
                if(phase==2){Assert(recovery.State=="Exhausted"&&recovery.Repairs==2&&await OsClosed(native,dns,stop));observations.Add(new{phase=stage,budgetUsed=recovery.Repairs,noRestart=true,protectedTrafficClosed=true});}
                else{Assert(recovery.State=="Active"&&recovery.Repairs==phase+1);observations.Add(new{phase=stage,automaticRecoveryMs=faultClock.ElapsedMilliseconds,newCore=true,backendRetained=backend.ProcessId==backendPid});}
            }
            await recovery.DisposeAsync();bool verificationAvailable=true;
            await using var guardLoss=new NativeCoreRecovery(()=>new NativeCoreBinding(core,config,native,owner,expected),ct=>verificationAvailable?Guard(group,ct):Task.FromResult(false),afterStart:async(binding,ct)=>
            {
                using var identity=Process.GetProcessById(binding.ProcessId);await File.WriteAllTextAsync(Path.Combine(root,"private","binding-core-pid.json"),JsonSerializer.Serialize(new{pid=identity.Id,ticks=identity.StartTime.ToUniversalTime().Ticks}),ct);
            });
            stage="guard-verifier-unavailable";await guardLoss.StartAsync(backend,spec,ready,stop);int guardedPid=guardLoss.ProcessId;verificationAvailable=false;
            while(guardLoss.State is not ("GuardLost" or "GuardLostUnconfirmed"))await Task.Delay(100,stop);
            Assert(guardLoss.State=="GuardLost"&&guardLoss.ProcessId==guardedPid&&guardLoss.Repairs==0&&await OsClosed(native,dns,stop));
            verificationAvailable=true;await Task.Delay(700,stop);Assert(guardLoss.State=="GuardLost");
            observations.Add(new{phase=stage,coreAndTunRetained=true,nativeOutputClosed=true,noAutomaticReactivation=true,independentGuardStillArmed=true});success=true;
        }
        catch(Exception e){errorType=e.GetType().Name;}
        Console.WriteLine(JsonSerializer.Serialize(new{schema=1,success,stage,errorType,observations,scope="Shared experimental core watchdog and independent VM IPv4 guard; crash, hang, automatic recovery, owner-lifetime budget; no production registration"}));return success?0:1;
    }
}
