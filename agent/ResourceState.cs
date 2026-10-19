using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RoutePilot.Resources;

public sealed class ResourceProcess
{
    public int Pid{get;set;}
    public DateTime StartedUtc{get;set;}
    public string Name{get;set;}="";
    public long PrivateBytes{get;set;}
    public int Handles{get;set;}
    public int UdpPorts{get;set;}
    public bool CitaComponent{get;set;}
}
public sealed class ResourcePool
{
    public string Family{get;set;}="IPv4";
    public bool TableAvailable{get;set;}
    public bool RangeAvailable{get;set;}
    public bool ReservationsAvailable{get;set;}
    public int Start{get;set;}
    public int Count{get;set;}
    public int Reserved{get;set;}
    public int Used{get;set;}
    public int Endpoints{get;set;}
    public string Allocation{get;set;}="NotMeasured";
    public int Capacity=>Math.Max(0,Count-(ReservationsAvailable?Reserved:0));
    public double? UsedPercent=>TableAvailable&&RangeAvailable&&Capacity>0?Math.Min(100,100d*Used/Capacity):null;
}
public sealed class ResourceSample
{
    public DateTime ObservedUtc{get;set;}
    public DateTime ProcessesObservedUtc{get;set;}
    public bool MemoryAvailable{get;set;}
    public long PhysicalTotal{get;set;}
    public long PhysicalAvailable{get;set;}
    public long CommitTotal{get;set;}
    public long CommitLimit{get;set;}
    public uint SystemHandles{get;set;}
    public int UnresolvedOwners{get;set;}
    public List<ResourcePool> Pools{get;set;}=new();
    public List<ResourceProcess> Processes{get;set;}=new();
    public List<string> Unavailable{get;set;}=new();
    public long DurationMs{get;set;}
}
public sealed class ResourceIssue
{
    public string Key{get;set;}="";
    public string Code{get;set;}="";
    public string Scope{get;set;}="";
    public int Level{get;set;}
    public DateTime ObservedUtc{get;set;}
}
public sealed class ResourceIncident
{
    public DateTime ObservedUtc{get;set;}
    public string Code{get;set;}="";
    public string Scope{get;set;}="";
    public string Transition{get;set;}="";
}
public sealed class ResourceState
{
    public int Schema{get;set;}=1;
    public string MonitorId{get;set;}="";
    public string AgentVersion{get;set;}="";
    public DateTime UpdatedUtc{get;set;}
    public ResourceSample Sample{get;set;}=new();
    public List<ResourceIssue> Issues{get;set;}=new();
    public List<ResourceIncident> Incidents{get;set;}=new();
    public bool Pending{get;set;}
    public int Level=>Issues.Select(x=>x.Level).DefaultIfEmpty(0).Max();
    public bool Complete=>Sample.MemoryAvailable&&!Sample.Unavailable.Any(x=>x is "CitaProcessCoverage" or "ProcessCoverageLimited")&&Sample.Pools.Count==2&&Sample.Pools.All(p=>p.TableAvailable&&p.RangeAvailable&&p.ReservationsAvailable&&p.Allocation=="Available");
}
public static class ResourceStateCache
{
    public static List<ResourceIncident> History(string path)
    {
        try
        {
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(input.Length>131072)return new();
            var state=JsonSerializer.Deserialize<ResourceState>(input,new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            return state?.Schema==1&&state.Incidents is {Count:<=32}?state.Incidents.Where(p=>p!=null&&p.Code is {Length:<=50}&&p.Scope is {Length:<=80}&&p.Transition is {Length:<=20}).ToList():new();
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException){return new();}
    }
    public static bool Fresh(DateTime value,DateTime now)=>value!=default&&value<=now.AddSeconds(5)&&value>=now.AddSeconds(-45);
    public static ResourceState? Read(string path,DateTime now)
    {
        try
        {
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(input.Length>131072)return null;
            var state=JsonSerializer.Deserialize<ResourceState>(input,new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            return Valid(state,now)?state:null;
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException){return null;}
    }
    public static bool Valid(ResourceState? state,DateTime now)=>state is {Schema:1,Sample:not null,Issues:not null,Incidents:not null}&&
        Guid.TryParseExact(state.MonitorId,"N",out _)&&Fresh(state.UpdatedUtc,now)&&Fresh(state.Sample.ObservedUtc,now)&&
        state.Sample.Pools is {Count:<=2}&&state.Sample.Processes is {Count:<=24}&&state.Sample.Unavailable is {Count:<=12}&&state.Sample.UnresolvedOwners is >=0 and <=8&&
        state.Issues.Count<=32&&state.Incidents.Count<=32&&state.Sample.PhysicalAvailable>=0&&state.Sample.PhysicalTotal>=state.Sample.PhysicalAvailable&&
        state.Sample.CommitTotal>=0&&state.Sample.CommitLimit>=state.Sample.CommitTotal&&
        (!state.Sample.MemoryAvailable||(state.Sample.PhysicalTotal>0&&state.Sample.CommitLimit>0))&&
        state.Sample.Pools.Select(p=>p?.Family).Distinct().Count()==state.Sample.Pools.Count&&
        state.Sample.Pools.All(p=>p!=null&&p.Family is "IPv4" or "IPv6"&&p.Start>=0&&p.Count>=0&&p.Start+(long)p.Count<=65536&&p.Used>=0&&p.Used<=65535&&p.Reserved>=0&&p.Reserved<=p.Count&&(!p.RangeAvailable||(p.Start>0&&p.Count>0)))&&
        state.Sample.Processes.All(p=>p!=null&&p.Name!=null&&p.Name.Length<=80&&p.Pid>=0&&p.PrivateBytes>=0&&p.Handles>=0)&&
        state.Issues.All(p=>p!=null&&p.Key is {Length:<=100}&&p.Code is {Length:<=50}&&p.Scope is {Length:<=80}&&p.Level is 1 or 2&&p.ObservedUtc<=state.UpdatedUtc.AddSeconds(5))&&
        state.Incidents.All(p=>p!=null&&p.Code is {Length:<=50}&&p.Scope is {Length:<=80}&&p.Transition is {Length:<=20});
}
