using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using RoutePilot.Agent;

internal static class CoreNetworkLab
{
    static async Task Ps(string code,CancellationToken stop)
    {
        var info=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop';"+code))})info.ArgumentList.Add(arg);
        using var p=Process.Start(info)!;_=p.StandardOutput.BaseStream.CopyToAsync(Stream.Null);_=p.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try{await p.WaitForExitAsync(stop);if(p.ExitCode!=0)throw new IOException("Guest network action refused");}
        finally{if(!p.HasExited){p.Kill();await p.WaitForExitAsync();}}
    }
    internal static async Task<int> Run(string root,string config,string owner)
    {
        var observations=new List<object>();bool success=false;string stage="prepare";string? errorType=null;JsonElement? sleepEvidence=null;
        try
        {
            using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(190));var stop=budget.Token;
            string native=Path.Combine(root,"native-awg-lab.exe"),peer=await File.ReadAllTextAsync(Path.Combine(root,"private","peer.conf"),stop);
            string dns=Regex.Match(peer,@"(?mi)^DNS\s*=\s*([0-9.]+)").Groups[1].Value;
            using var reference=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"..","server-egress-reference.json"),stop));string expected=reference.RootElement.GetProperty("serverEgressHash").GetString()!;
            var spec=JsonSerializer.SerializeToElement(new{Session=Guid.NewGuid().ToString("N"),Generation=1UL,Token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),Config=peer});
            await using var backend=new NativeAwgBackend(native);var ready=await backend.StartAsync(spec,stop);
            await using var binding=new NativeCoreBinding(Path.Combine(root,"native-core-lab.exe"),config,native,owner,expected);await binding.StartAsync(stop);
            using(var identity=Process.GetProcessById(binding.ProcessId))await File.WriteAllTextAsync(Path.Combine(root,"private","binding-core-pid.json"),JsonSerializer.Serialize(new{pid=binding.ProcessId,ticks=identity.StartTime.ToUniversalTime().Ticks}),stop);
            await binding.QuarantineAsync(spec,stop);await binding.ActivateAsync(backend,spec,ready,stop);int corePid=binding.ProcessId;
            foreach(string action in new[]{"link-interruption","ipv4-change","os-s1-sleep","vm-suspend-resume"})
            {
                stage=action;var clock=Stopwatch.StartNew();
                var probe=await CoreSafetyLab.Tun(native,dns,stop,async()=>
                {
                    if(action=="link-interruption")await Ps("& 'C:\\CitaLab\\NativeAwg\\Guest-LinkInterruption.ps1'",stop);
                    else if(action=="os-s1-sleep")
                    {
                        try{await Ps("& 'C:\\CitaLab\\NativeAwg\\Guest-SleepWake.ps1'",stop);}
                        finally{string file=Path.Combine(root,"os-sleep-result.json");if(File.Exists(file)){using var doc=JsonDocument.Parse(await File.ReadAllTextAsync(file));sleepEvidence=doc.RootElement.Clone();}}
                    }
                    else if(action=="ipv4-change")
                    {
                        string state=Path.Combine(root,"private","ip-"+Guid.NewGuid().ToString("N")+".json");
                        try
                        {
                            await Ps("& 'C:\\CitaLab\\NativeAwg\\Guest-IPv4Migration.ps1' -Action Begin -StatePath '"+state+"'",stop);
                            // Keep the changed source for actual HTTPS and UDP probes.
                            stage="ipv4-change-active";var changed=await CoreSafetyLab.Tun(native,dns,stop);
                            observations.Add(new{phase="changed-source-probe",probe=changed});
                            if(!changed.GetProperty("success").GetBoolean())throw new IOException("New source unreachable");stage=action;
                        }
                        finally
                        {
                            using var restore=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                            if(File.Exists(state))await Ps("& 'C:\\CitaLab\\NativeAwg\\Guest-IPv4Migration.ps1' -Action Restore -StatePath '"+state+"'",restore.Token);
                        }
                    }
                    else
                    {
                        await File.WriteAllTextAsync(Path.Combine(root,"network-ready.json"),JsonSerializer.Serialize(new{owner,corePid,stage="SuspendReady"}),stop);
                        string signal=Path.Combine(root,"network-resumed.txt");
                        while(!File.Exists(signal))await Task.Delay(200,stop);
                        if((await File.ReadAllTextAsync(signal,stop)).Trim()!=owner)throw new IOException("Resume identity mismatch");
                    }
                });
                bool retained=probe.GetProperty("success").GetBoolean();
                // A remote HTTPS server may retire an idle TCP connection while
                // the machine sleeps. Availability after sleep is separate from
                // preservation of a particular application's old TCP socket.
                bool recoveredAfterSleep=action=="os-s1-sleep"&&probe.GetProperty("sameExit").GetBoolean()&&probe.GetProperty("udpAfter").GetInt32()==3&&probe.GetProperty("tcpFromTun").GetBoolean()&&probe.GetProperty("udpFromTun").GetBoolean();
                observations.Add(new{phase=action,elapsedMs=clock.ElapsedMilliseconds,probe,coreRetained=binding.ProcessId==corePid,tcpRetained=retained,recoveredAfterSleep});
                if(!(retained||recoveredAfterSleep)||binding.ProcessId!=corePid)throw new IOException("Network continuity failed");
            }
            success=true;
        }
        catch(Exception e){errorType=e.GetType().Name;}
        Console.WriteLine(JsonSerializer.Serialize(new{schema=1,success,stage,errorType,observations,sleepEvidence,scope="VM virtual-NIC interruption, IP change, guarded guest S1 sleep/wake and VMware suspend/resume; not physical Wi-Fi/Ethernet or S3"}));return success?0:1;
    }
}
