using System.Diagnostics;
using RoutePilot.Resources;

namespace RoutePilot.Agent;

// Observation only: no kill, service, route, profile or reconnect operations.
public sealed class ResourceMonitorEngine
{
    readonly Dictionary<string,(int Level,int Count,DateTime At)> _pending=new();
    readonly Dictionary<string,int> _clear=new();
    readonly Dictionary<string,ResourceIssue> _active=new();
    readonly Dictionary<string,List<(DateTime At,long Bytes,int Handles)>> _trends=new();
    readonly List<ResourceIncident> _history=new();
    DateTime _lastProcessSample,_lastSample;
    readonly string _id=Guid.NewGuid().ToString("N");
    public ResourceMonitorEngine(IEnumerable<ResourceIncident>? history=null){if(history!=null)_history.AddRange(history.TakeLast(32));}
    public ResourceState Update(ResourceSample sample)
    {
        if(sample.ObservedUtc==default||sample.ObservedUtc<=_lastSample)throw new InvalidDataException("Resource samples must advance.");
        _lastSample=sample.ObservedUtc;
        var detected=new List<ResourceIssue>();
        void Add(string key,string code,string scope,int level)=>detected.Add(new(){Key=key,Code=code,Scope=scope,Level=level,ObservedUtc=code=="CitaResourceGrowth"?sample.ProcessesObservedUtc:sample.ObservedUtc});
        foreach(var pool in sample.Pools)
        {
            if(pool.UsedPercent is {} percent&&percent>=80)Add("ports-"+pool.Family,"UdpPressure",pool.Family,percent>=95?2:1);
            if(pool.Allocation=="ResourceFailure")Add("bind-"+pool.Family,"UdpAllocationFailure",pool.Family,2);
            if(pool.Allocation=="AccessDenied")Add("access-"+pool.Family,"UdpAllocationDenied",pool.Family,1);
        }
        if(sample.MemoryAvailable)
        {
            double commit=sample.CommitLimit>0?100d*sample.CommitTotal/sample.CommitLimit:0;
            long remaining=sample.CommitLimit-sample.CommitTotal;
            if(commit>=90&&remaining<2L*1024*1024*1024)Add("commit","CommitPressure","Windows",commit>=97&&remaining<512L*1024*1024?2:1);
            if(sample.PhysicalTotal>0&&sample.PhysicalAvailable<512L*1024*1024&&100d*sample.PhysicalAvailable/sample.PhysicalTotal<=5)
                Add("physical","PhysicalPressure","Windows",sample.PhysicalAvailable<128L*1024*1024?2:1);
        }
        if(sample.ProcessesObservedUtc>_lastProcessSample&&sample.ProcessesObservedUtc<=sample.ObservedUtc)
        {
            _lastProcessSample=sample.ProcessesObservedUtc;
            var seen=new HashSet<string>();
            foreach(var process in sample.Processes.Where(p=>p.CitaComponent&&p.StartedUtc!=default))
            {
                string key=process.Pid+"-"+process.StartedUtc.Ticks;seen.Add(key);
                if(!_trends.TryGetValue(key,out var trend))_trends[key]=trend=new();
                trend.Add((sample.ProcessesObservedUtc,process.PrivateBytes,process.Handles));
                trend.RemoveAll(x=>x.At<sample.ProcessesObservedUtc.AddMinutes(-15));
                if(trend.Count>32)trend.RemoveRange(0,trend.Count-32);
            }
            foreach(string key in _trends.Keys.Where(k=>!seen.Contains(k)).ToArray())_trends.Remove(key);
        }
        foreach(var process in sample.Processes.Where(p=>p.CitaComponent&&p.StartedUtc!=default))
        {
            string identity=process.Pid+"-"+process.StartedUtc.Ticks;
            if(!_trends.TryGetValue(identity,out var trend)||trend.Count<12||trend[^1].At-trend[0].At<TimeSpan.FromMinutes(10))continue;
            int risingBytes=0,risingHandles=0;
            for(int i=1;i<trend.Count;i++){if(trend[i].Bytes>trend[i-1].Bytes)risingBytes++;if(trend[i].Handles>trend[i-1].Handles)risingHandles++;}
            if((trend[^1].Bytes-trend[0].Bytes>=512L*1024*1024&&risingBytes>=0.8*(trend.Count-1))||
               (trend[^1].Handles-trend[0].Handles>=2048&&risingHandles>=0.8*(trend.Count-1)))
                Add("growth-"+identity,"CitaResourceGrowth",process.Name+" ("+process.Pid+")",1);
        }
        var keys=detected.Select(x=>x.Key).ToHashSet();
        foreach(string key in _pending.Keys.Where(k=>!keys.Contains(k)).ToArray())_pending.Remove(key);
        foreach(var issue in detected)
        {
            _clear.Remove(issue.Key);
            var previous=_pending.GetValueOrDefault(issue.Key);
            int count=previous.Level==issue.Level?previous.Count+(issue.ObservedUtc>previous.At?1:0):1;_pending[issue.Key]=(issue.Level,count,issue.ObservedUtc);
            if(_active.TryGetValue(issue.Key,out var current)&&current.Level==issue.Level){_active[issue.Key]=issue;continue;}
            if(count<2)continue;
            _active[issue.Key]=issue;
            _history.Add(new(){ObservedUtc=sample.ObservedUtc,Code=issue.Code,Scope=issue.Scope,Transition=issue.Level==2?"Critical":"Warning"});
        }
        foreach(string key in _active.Keys.Where(k=>!keys.Contains(k)).ToArray())
        {
            var issue=_active[key];
            bool reliable=issue.Code is "CommitPressure" or "PhysicalPressure"?sample.MemoryAvailable:
                issue.Code=="CitaResourceGrowth"?sample.ProcessesObservedUtc==sample.ObservedUtc&&!sample.Unavailable.Any(x=>x is "CitaProcessCoverage" or "ProcessCoverageLimited"):
                sample.Pools.Any(p=>p.Family==issue.Scope&&p.TableAvailable&&p.RangeAvailable&&p.Allocation=="Available");
            if(!reliable){_clear.Remove(key);continue;}
            int count=_clear.GetValueOrDefault(key)+1;_clear[key]=count;
            if(count<3)continue;
            _active.Remove(key);_clear.Remove(key);
            _history.Add(new(){ObservedUtc=sample.ObservedUtc,Code=issue.Code,Scope=issue.Scope,Transition="Recovered"});
        }
        if(_history.Count>32)_history.RemoveRange(0,_history.Count-32);
        return new(){MonitorId=_id,AgentVersion=typeof(ResourceMonitorEngine).Assembly.GetName().Version?.ToString(3)??"",UpdatedUtc=sample.ObservedUtc,Sample=sample,
            Issues=_active.Values.OrderByDescending(i=>i.Level).Take(32).ToList(),Incidents=_history.ToList(),Pending=detected.Any(i=>!_active.ContainsKey(i.Key))};
    }
}

public static class ResourceMonitor
{
    public static async Task Run(string directory,string appRoot,CancellationToken stop)
    {
        var sampler=new WindowsResourceSampler(appRoot);
        var engine=new ResourceMonitorEngine(ResourceStateCache.History(Path.Combine(directory,"resource-state.json")));
        while(!stop.IsCancellationRequested)
        {
            // The shared rescue service may remain after uninstall. Do not
            // inspect processes while its configured client is absent.
            if(!File.Exists(Path.Combine(appRoot,"RoutePilot.exe"))&&!File.Exists(Path.Combine(appRoot,"runtime","smart-router.exe")))
            {
                engine=new ResourceMonitorEngine(ResourceStateCache.History(Path.Combine(directory,"resource-state.json")));
                try{await Task.Delay(TimeSpan.FromSeconds(10),stop);}catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
                continue;
            }
            ResourceSample sample;
            try{sample=await sampler.Capture(stop);}
            catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
            catch{sample=new(){ObservedUtc=DateTime.UtcNow,Unavailable=new(){"SamplerUnavailable"}};}
            ResourceState state;
            try{state=engine.Update(sample);}
            catch(InvalidDataException){engine=new ResourceMonitorEngine(ResourceStateCache.History(Path.Combine(directory,"resource-state.json")));state=engine.Update(sample);}
            try{Storage.Write(Path.Combine(directory,"resource-state.json"),state);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
            try{Cita.Diagnostics.SessionEvidence.ObserveResources(directory,state);}catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException){}
            // Independent observation survives a missing watcher or a closed UI.
            // It records evidence only; it never starts or repairs a route.
            try
            {
                Cita.Diagnostics.SessionEvidence.Observe(appRoot,"ordinary",directory,DateTime.UtcNow);
                if(File.Exists(Path.Combine(Cita.Startup.StartupPaths.Client,"runtime","session.json")))
                    Cita.Diagnostics.SessionEvidence.Observe(Cita.Startup.StartupPaths.Client,"startup",directory,DateTime.UtcNow);
            }
            catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException){}
            int seconds=sample.Pools.Any(p=>p.UsedPercent>=70||p.Allocation=="ResourceFailure")?2:10;
            try{await Task.Delay(TimeSpan.FromSeconds(seconds),stop);}catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
        }
    }
}
