using System.Diagnostics;
using RoutePilot.Agent;

internal static class NativePerformanceLab
{
    sealed record ProcessUsage(string Role,int Id,long StartedTicks,long CpuTicks,long PrivateBytes,long WorkingSetBytes,int Handles,int Threads);
    sealed record Sample(string Phase,long ElapsedMs,double IntervalMs,double NativeMachineCpuPercent,double? PersistentMachineCpuPercent,double ControllerMachineCpuPercent,uint ProcessesStarted,uint ActiveProcesses,ulong PeakJobCommittedBytes,ProcessUsage[] Processes,long? ProbeMs);
    internal static async Task<object> Measure(NativeGuardedSession runtime,string root,CancellationToken stop,Dictionary<string,object?> report)
    {
        var samples=new List<Sample>();int cpus=Environment.ProcessorCount;var clock=Stopwatch.StartNew();
        var prior=runtime.ResourceUsage;using var controller=Process.GetCurrentProcess();double lastCpu=controller.TotalProcessorTime.TotalMilliseconds;long last=0;
        ProcessUsage[] Processes()
        {
            var list=new List<ProcessUsage>();
            foreach(var pair in new[]{("native-core","core"),("native-owner","transport"),("native-guard","guard-helper")})
                foreach(var process in Process.GetProcessesByName(pair.Item1))using(process)
                {
                    try{if(!string.Equals(process.MainModule?.FileName,Path.Combine(root,pair.Item1+".exe"),StringComparison.OrdinalIgnoreCase))continue;process.Refresh();list.Add(new(pair.Item2,process.Id,process.StartTime.ToUniversalTime().Ticks,process.TotalProcessorTime.Ticks,process.PrivateMemorySize64,process.WorkingSet64,process.HandleCount,process.Threads.Count));}
                    catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
                }
            controller.Refresh();list.Add(new("lab-controller",controller.Id,controller.StartTime.ToUniversalTime().Ticks,controller.TotalProcessorTime.Ticks,controller.PrivateMemorySize64,controller.WorkingSet64,controller.HandleCount,controller.Threads.Count));return list.ToArray();
        }
        var previousProcesses=Processes().Where(p=>p.Role is "core" or "transport").ToDictionary(p=>(p.Id,p.StartedTicks),p=>p.CpuTicks);
        async Task Take(string phase,bool probe)
        {
            long? probeMs=null;
            if(probe){var timer=Stopwatch.StartNew();var result=await CoreSafetyLab.Tun(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop,domain:true);if(!result.GetProperty("success").GetBoolean()){
                string dns=result.TryGetProperty("dnsFailure",out var failure)&&failure.ValueKind==System.Text.Json.JsonValueKind.String?failure.GetString()??"":"";
                string stage=result.TryGetProperty("stage",out var stageValue)&&stageValue.ValueKind==System.Text.Json.JsonValueKind.String?stageValue.GetString()??"":"";
                report["performanceControlFailure"]=new{sample=samples.Count,phase,stage,dnsFailure=dns,udpBefore=result.TryGetProperty("udpBefore",out var before)&&before.TryGetInt32(out int n)?n:0,udpAfter=result.TryGetProperty("udpAfter",out var after)&&after.TryGetInt32(out int m)?m:0};throw new IOException("Performance control probe failed.");}probeMs=timer.ElapsedMilliseconds;}
            else await Task.Delay(1000,stop);
            var usage=runtime.ResourceUsage;controller.Refresh();double cpu=controller.TotalProcessorTime.TotalMilliseconds;long now=clock.ElapsedMilliseconds;double interval=now-last;
            if(interval<=0||usage.CpuTicks<prior.CpuTicks||usage.ProcessesStarted<prior.ProcessesStarted||usage.PeakCommittedBytes==0)throw new IOException("Native resource accounting invalid.");
            var processes=Processes();var persistent=processes.Where(p=>p.Role is "core" or "transport").ToArray();
            double? persistentCpu=persistent.Length>=2&&persistent.Select(p=>p.Role).ToHashSet().SetEquals(["core","transport"])&&persistent.All(p=>previousProcesses.TryGetValue((p.Id,p.StartedTicks),out long ticks)&&p.CpuTicks>=ticks)?100*persistent.Sum(p=>p.CpuTicks-previousProcesses[(p.Id,p.StartedTicks)])/10000.0/interval/cpus:null;
            samples.Add(new(phase,now,interval,100*(usage.CpuTicks-prior.CpuTicks)/10000.0/interval/cpus,persistentCpu,100*(cpu-lastCpu)/interval/cpus,usage.ProcessesStarted,usage.ActiveProcesses,usage.PeakCommittedBytes,processes,probeMs));prior=usage;last=now;lastCpu=cpu;
            previousProcesses=persistent.ToDictionary(p=>(p.Id,p.StartedTicks),p=>p.CpuTicks);
        }
        for(int i=0;i<12;i++)await Take("idle",false);
        for(int i=0;i<6;i++)await Take("control-probes",true);
        for(int i=0;i<8;i++)await Take("settling",false);
        var idle=samples.Where(s=>s.Phase=="idle").ToArray();var probes=samples.Where(s=>s.ProbeMs!=null).Select(s=>s.ProbeMs!.Value).OrderBy(x=>x).ToArray();
        return new{scope="VM native core/transport/job and lab controller; excludes desktop UI and SCM service host",logicalProcessors=cpus,durationMs=clock.ElapsedMilliseconds,
            cpuDefinition="Machine-normalized CPU; native Windows job includes exited helpers, controller is separate",cacheState="Uncontrolled OS cache; not a cold boot benchmark",sampleCount=samples.Count,
            idleNativeCpuPercent=idle.Sum(x=>x.NativeMachineCpuPercent*x.IntervalMs)/idle.Sum(x=>x.IntervalMs),idleControllerCpuPercent=idle.Sum(x=>x.ControllerMachineCpuPercent*x.IntervalMs)/idle.Sum(x=>x.IntervalMs),
            idlePersistentCpuPercent=idle.All(x=>x.PersistentMachineCpuPercent.HasValue)?idle.Sum(x=>x.PersistentMachineCpuPercent!.Value*x.IntervalMs)/idle.Sum(x=>x.IntervalMs):(double?)null,
            peakJobCommittedBytes=samples.Max(s=>s.PeakJobCommittedBytes),nativePrivatePeakBytes=samples.Max(s=>s.Processes.Where(p=>p.Role!="lab-controller").Sum(p=>p.PrivateBytes)),
            nativePrivateStartBytes=samples[0].Processes.Where(p=>p.Role!="lab-controller").Sum(p=>p.PrivateBytes),nativePrivateEndBytes=samples[^1].Processes.Where(p=>p.Role!="lab-controller").Sum(p=>p.PrivateBytes),
            controlProbeMedianMs=(probes[2]+probes[3])/2.0,controlProbeMaxMs=probes[^1],probeDefinition="Helper startup + two HTTPS requests and UDP control exchange; not page-load latency",samples};
    }
}
