using System.Diagnostics;
using System.Text.Json;
using RoutePilot.Agent;

internal static class NativeQueueStressLab
{
    internal static object Run()
    {
        var clock=Stopwatch.StartNew();var results=new List<object>();
        NativeObservedDestination Seen(int i)=>new("h"+i.ToString("D5")+".example",i%2==0?"":@"C:\Fixture\app.exe","1.1.1.1",443);
        foreach(int count in new[]{200,1000,10000})
        {
            var schedule=new NativeCalibrationSchedule();var budget=new NativeCalibrationBudget();var selected=new HashSet<NativeObservedDestination>();int attempts=0,maxState=0;long now=1000;
            // Each stable window is drained before rotation: virtual time only.
            foreach(var window in Enumerable.Range(0,count).Select(Seen).Chunk(128))
            {
                for(int tick=0;tick<60000&&window.Any(o=>!selected.Contains(o));tick++,now+=1000)
                {
                    var next=schedule.Select(window,now);maxState=Math.Max(maxState,schedule.Count);
                    if(next==null||!budget.TryAcquire(now))continue;
                    schedule.Mark(next,now);selected.Add(next);attempts++;maxState=Math.Max(maxState,schedule.Count);
                    // Scheduling tracks attempts, not successful reachability.
                    // A failed probe has the same cooldown as any other attempt.
                }
            }
            if(selected.Count!=count||maxState>128)throw new Exception("Stable queue coverage or bound failed.");
            schedule.Clear();if(schedule.Count!=0)throw new Exception("Network epoch retained queue state.");
            results.Add(new{count,selected=selected.Count,attempts,maxState,virtualSeconds=(now-1000)/1000});
        }
        var churn=new NativeCalibrationSchedule();var churnBudget=new NativeCalibrationBudget();int churnAttempts=0,churnMax=0;
        for(int tick=0;tick<10000;tick++)
        {
            var window=Enumerable.Range(tick,128).Select(Seen).ToArray();long now=1000L+tick;
            var next=churn.Select(window,now);if(next!=null&&churnBudget.TryAcquire(now)){churn.Mark(next,now);churnAttempts++;}churnMax=Math.Max(churnMax,churn.Count);
        }
        if(churnAttempts!=32||churnMax>128)throw new Exception("Churn exceeded global budget or window bound.");
        var repeat=new NativeCalibrationSchedule();var due=Seen(0);repeat.Mark(due,1000);
        if(repeat.Select([due],60999)!=null||repeat.Select([due],61000)!=due)throw new Exception("Failed probe cooldown broken.");
        bool oversize=false;try{repeat.Select(Enumerable.Range(0,129).Select(Seen).ToArray(),0);}catch(InvalidDataException){oversize=true;}if(!oversize)throw new Exception("Oversize queue accepted.");
        int refused=0;var valid=new NativeDomainRule("example.com","","tcp","direct",443,["1.1.1.1"],DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds());NativeRoutePolicyPublisher.Validate([valid]);
        foreach(var bad in new[]{valid with{Domain="*.example.com"},valid with{Domain="127.0.0.1"},valid with{Domain="a..com"},valid with{Domain="x\r\nCookie: x"},valid with{Domain="https://example.com"},valid with{Application="browser.exe"},valid with{Application="C:\\app\n.exe"},valid with{Port=0},valid with{Network="all"},valid with{Route="guess"},valid with{Addresses=["127.0.0.1"]},valid with{Addresses=["10.0.0.1"]},valid with{Addresses=["100.64.0.1"]},valid with{Addresses=["169.254.1.1"]},valid with{Addresses=["::1"]},valid with{Addresses=["224.0.0.1"]},valid with{Addresses=["198.18.0.1"]},valid with{Addresses=["198.19.255.255"]},valid with{Addresses=["1.1.1.1","1.1.1.1"]},valid with{ExpiresUnixMilliseconds=0},valid with{ExpiresUnixMilliseconds=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()}})
        {bool rejected=false;try{NativeRoutePolicyPublisher.Validate([bad]);}catch(InvalidDataException){rejected=true;refused++;}if(!rejected)throw new Exception("Invalid policy scope admitted.");}
        foreach(var rules in new[]{new[]{valid,valid with{Domain="EXAMPLE.COM."}},Enumerable.Range(0,129).Select(i=>valid with{Domain="h"+i+".example"}).ToArray()}){bool rejected=false;try{NativeRoutePolicyPublisher.Validate(rules);}catch(InvalidDataException){rejected=true;refused++;}if(!rejected)throw new Exception("Duplicate or oversized policy admitted.");}
        int retryCases=0;var owner=JsonSerializer.SerializeToElement(new{state="RunningUnverified"});
        foreach(int status in new[]{200,301,400,401,403,404,408,429,500,501,502,503,504})foreach(int attempt in new[]{1,2})
        {var probe=JsonSerializer.SerializeToElement(new{success=false,failureCode="HttpStatus",httpStatus=status});bool expected=attempt==1&&status is 500 or 502 or 503 or 504;if(NativeProbePolicy.RetryAnonymousHttps(probe,owner,attempt)!=expected)throw new Exception("HTTP status misclassified for retry.");retryCases++;}
        return new{success=true,stable=results,invalidPolicyCasesRefused=refused,httpRetryCases=retryCases,churn=new{ticks=10000,virtualMilliseconds=10000,attempts=churnAttempts,maxState=churnMax},elapsedMs=clock.ElapsedMilliseconds,
            scope="Deterministic scheduling only, virtual time, no network. Rotating window intentionally retains 128 entries; evicted destinations remain on default VPN, not guaranteed calibrated."};
    }
}
