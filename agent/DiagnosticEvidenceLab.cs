using System.Diagnostics;
using System.Text.Json;
using Cita.Diagnostics;
using RoutePilot.Resources;

namespace RoutePilot.Agent;

// File-only developer acceptance. No sampler, transport, service or route calls.
internal static class DiagnosticEvidenceLab
{
    internal static void Run(string directory,string? observations=null)
    {
        Directory.CreateDirectory(directory);
        string fixture=Path.Combine(directory,"evidence-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        try
        {
            var timings=new List<double>();int count=0;var now=DateTime.UtcNow.AddHours(-3);string id=Guid.NewGuid().ToString("N");
            long allocated=GC.GetAllocatedBytesForCurrentThread();var clock=Stopwatch.StartNew();
            if(observations!=null)
            {
                using var input=new FileStream(observations,FileMode.Open,FileAccess.Read,FileShare.Read);
                if(input.Length>16*1024*1024)throw new InvalidDataException("Replay input exceeds its limit.");
                using var doc=JsonDocument.Parse(input,new JsonDocumentOptions{MaxDepth=24});
                if(doc.RootElement.ValueKind!=JsonValueKind.Array||doc.RootElement.GetArrayLength()>10000)throw new InvalidDataException("Expected bounded session observations.");
                var rows=doc.RootElement.EnumerateArray().Where(r=>r.TryGetProperty("observedUtc",out var time)&&time.TryGetDateTime(out _)).OrderBy(r=>r.GetProperty("observedUtc").GetDateTime());
                foreach(var row in rows)
                {
                    var started=Stopwatch.GetTimestamp();
                    SessionEvidence.ObserveIncident(fixture,"ordinary",fixture,row,row.GetProperty("observedUtc").GetDateTime().ToUniversalTime());
                    timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);count++;
                }
            }
            else
            {
                for(int i=0;i<360;i++)
                {
                    var at=now.AddSeconds(10*i);var probe=now.AddSeconds(30*(i/3));var started=Stopwatch.GetTimestamp();
                    var snapshot=JsonSerializer.SerializeToElement(new{sessionId=id,bridgeGeneration=1,running=true,consistent=true,
                        health=new{healthy=true,fresh=true,matchesGeneration=true,code="Verified",updatedUtc=probe}});
                    SessionEvidence.ObserveIncident(fixture,"ordinary",fixture,snapshot,at);
                    SessionEvidence.ObserveResources(fixture,new ResourceState{Sample=new ResourceSample{ObservedUtc=at,ProcessesObservedUtc=at,Processes=new(){new()
                        {Pid=42,StartedUtc=now.AddMinutes(-1),ObservedUtc=at,Name="smart-router",CitaComponent=true,PrivateBytes=80*1024*1024,WorkingSetBytes=60*1024*1024,CpuTotalMilliseconds=i*100,Handles=300}}}});
                    timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);count++;
                }
            }
            long bytes=GC.GetAllocatedBytesForCurrentThread()-allocated;
            if(count==0)throw new InvalidDataException("Replay contains no usable observations.");
            string incidentsPath=Path.Combine(fixture,"diagnostic-incidents-ordinary.json");
            using var incidents=JsonDocument.Parse(File.ReadAllText(incidentsPath));
            var evidence=incidents.RootElement.GetProperty("Incidents").EnumerateArray().Select(i=>new
            {detectedUtc=i.GetProperty("DetectedUtc").GetDateTime(),firstFailureUtc=i.GetProperty("FirstFailureUtc").GetDateTime(),
                finishedUtc=i.GetProperty("FinishedUtc").ValueKind==JsonValueKind.String?(DateTime?)i.GetProperty("FinishedUtc").GetDateTime():null,
                phase=i.GetProperty("Phase").GetString(),code=i.GetProperty("Code").GetString()}).ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new{success=true,mode=observations==null?"SyntheticJournalCost":"RecordedStatesOnly",observations=count,
                elapsedMilliseconds=clock.Elapsed.TotalMilliseconds,meanUpdateMilliseconds=timings.DefaultIfEmpty().Average(),p95UpdateMilliseconds=timings.Order().ElementAt(Math.Max(0,(int)(timings.Count*.95)-1)),
                allocatedBytes=bytes,incidentBytes=new FileInfo(incidentsPath).Length,
                resourceHistoryBytes=File.Exists(Path.Combine(fixture,"resource-history.jsonl"))?new FileInfo(Path.Combine(fixture,"resource-history.jsonl")).Length:0,
                incidents=evidence,scope="File-only replay/synthetic journal cost; no live connectivity, sampler overhead, leak or outage-duration proof"}));
        }
        finally
        {
            string parent=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!Path.GetFullPath(fixture).StartsWith(parent,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe diagnostic fixture cleanup.");
            Directory.Delete(fixture,true);
        }
    }
}
