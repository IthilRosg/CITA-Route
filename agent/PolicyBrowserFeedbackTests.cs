namespace RoutePilot.Agent;

internal static class PolicyBrowserFeedbackTests
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    internal static async Task Run(bool native=true)
    {
        var now=DateTime.UtcNow;var key=new PolicyKey("browser-epoch","asset.example.com",@"C:\Browser\chrome.exe",443);
        Assert(PublicResourceAudit.Allowed("https://srnky.com/OeXSnt")&&!PublicResourceAudit.Allowed("https://srnky.com/other")&&!PublicResourceAudit.Allowed("https://127.0.0.1/"),"Public audit targets are fixed; arbitrary origins and paths cannot enable the fixture.");
        PolicyBrowserFeedback New()=>new();
        string Ticket(PolicyBrowserFeedback channel,DateTime? at=null,long revision=1,int generation=2)=>channel.Begin(key,"GET","Image","direct",revision,generation,at??now)!;
        foreach(string error in new[]{"ERR_BLOCKED_BY_CLIENT","ERR_ABORTED","ERR_CERT_AUTHORITY_INVALID","ERR_SSL_PROTOCOL_ERROR","HTTP_403","ERR_FAILED"})
        {var c=New();Assert(c.Fail(Ticket(c),"net::"+error,false,1,2,now)==null,"Client blocks, cancellation, TLS/authentication and unknown failures must not change routing.");}
        var channel=New();
        var dnsChannel=New();
        foreach(string error in new[]{"ERR_NAME_NOT_RESOLVED","ERR_NAME_RESOLUTION_FAILED","ERR_DNS_TIMED_OUT"})
            Assert(dnsChannel.Fail(Ticket(dnsChannel),error,false,1,2,now)?.Kind=="dns","Browser DNS failures must schedule independent destination checks.");
        Assert(dnsChannel.Avoided(key,now).Count==0,"Repeated DNS errors must not quarantine a transport based on a published route.");
        string context=Guid.NewGuid().ToString("N"),otherContext=Guid.NewGuid().ToString("N");
        var graph=new BrowserDependencies();var sibling=key with{Domain="cdn.example.com"};
        graph.Observe(context,"page.example.com",key,"Image",now);
        graph.Observe(context,"page.example.com",sibling,"Script",now);
        graph.Observe(otherContext,"other.example.com",sibling,"Image",now);
        Assert(graph.Related(context,"page.example.com",key,now).SequenceEqual(new[]{sibling}),"Only observed resources of the same initiator document are related.");
        Assert(graph.Related(otherContext,"page.example.com",key,now).Length==0&&graph.Related(context,"page.example.com",key with{Application=@"C:\Other\chrome.exe"},now).Length==0,"Documents and browser identities cannot share dependency priority.");
        Assert(graph.Related(context,"page.example.com",key with{Epoch="different"},now).Length==0,"Dependencies cannot cross session epochs.");
        graph.Observe(context,"page.example.com",key,"Image",now,false);
        Assert(graph.Snapshot(now).Single(e=>e.Target==key).Failed==1,"Body failure is attached to its exact resource edge.");
        Assert(graph.Snapshot(now.AddMinutes(5)).Length==0,"Dependency relations expire without history persistence.");
        foreach(string parent in new[]{"", "https://page.example.com/path", "host.ts.net", "chatgpt.com"})graph.Observe(context,parent,key,"Image",now);
        Assert(graph.Count==0,"Unknown/URL/pinned initiators do not enter the graph.");
        for(int i=0;i<200;i++)graph.Observe(context,"page.example.com",key with{Domain=$"asset{i}.example.com"},"Image",now);
        Assert(graph.Count==32,"One document cannot dominate the graph.");
        for(int i=0;i<400;i++)graph.Observe(Guid.NewGuid().ToString("N"),"page.example.com",key,"Image",now);
        Assert(graph.Count==256&&graph.Snapshot(now).Length==32,"Global graph and diagnostic export remain bounded.");graph.Clear();Assert(graph.Count==0,"Clear removes dependency context.");
        var depChannel=New();string dt=depChannel.Begin(key,"GET","Image","direct",1,2,now,"page.example.com",context)!;
        Assert(!depChannel.Complete(dt,2,2,now)&&depChannel.Dependencies.Snapshot(now).Single().Completed==0,"A stale publication cannot certify a dependency.");
        dt=depChannel.Begin(key,"GET","Image","direct",1,2,now,"page.example.com",context)!;
        Assert(depChannel.Complete(dt,1,2,now)&&depChannel.Dependencies.Snapshot(now).Single().Completed==1,"Valid completion updates the observed dependency once.");
        depChannel.Clear();Assert(depChannel.Dependencies.Count==0,"Session clear removes the whole graph.");
        var queue=new PolicyWebSignals();var keys=Enumerable.Range(0,5).Select(i=>key with{Domain=$"queued{i}.example.com"}).ToArray();
        foreach(var k in keys)queue.Discover(k,now);
        queue.Prioritize(keys[2]);queue.Prioritize(keys[3]);queue.Prioritize(keys[4]);queue.Prioritize(sibling);
        Assert(queue.Queued==5&&queue.Take(_=>true,now)==keys[2]&&queue.Take(_=>true,now)==keys[3]&&queue.Take(_=>true,now)==keys[0]&&queue.Take(_=>true,now)==keys[4],"Dependency priority neither creates probes nor starves ordinary discovery (two priority slots, then ordinary).");
        Assert(channel.Begin(key,"POST","Fetch","direct",1,2,now)==null&&channel.Begin(key,"GET","Other","direct",1,2,now)==null&&channel.Begin(key with{Application=@"C:\Game\game.exe"},"GET","Image","direct",1,2,now)==null,"Only an identified browser's known GET resources enter feedback.");
        foreach(string resource in new[]{"Document","Script","Stylesheet","Font","Image","Media","XHR"})
        {var c=New();string t=c.Begin(key,"GET",resource,"direct",1,2,now)!;Assert(t!=null&&c.Complete(t,1,2,now),"All supported resources report network completion.");}
        var delivery=New();string done=Ticket(delivery);
        Assert(delivery.Complete(done,1,2,now)&&!delivery.Complete(done,1,2,now)&&delivery.Fail(done,"ERR_CONNECTION_RESET",false,1,2,now)==null,"Completion consumes one ticket without accepting duplicate or contradictory outcomes.");
        Assert(delivery.Deliveries(now).Single().Completed==1&&delivery.Deliveries(now).Single().Failed==0,"Completion is separate from failure and TLS calibration proof.");
        Assert(!delivery.Complete(Ticket(delivery),2,2,now)&&!delivery.Complete(Ticket(delivery),1,3,now)&&!delivery.Complete(Ticket(delivery),1,2,now.AddSeconds(90))&&!delivery.Complete(Ticket(delivery),1,2,now.AddSeconds(-1)),"Changed route/generation and expired/future completion cannot certify a path.");
        Assert(delivery.Deliveries(now.AddMinutes(5)).Length==0,"Delivery evidence expires.");
        delivery.Fail(Ticket(delivery),"ERR_CONNECTION_RESET",false,1,2,now);
        delivery.Fail(Ticket(delivery),"ERR_CONNECTION_RESET",false,1,2,now);
        delivery.Complete(Ticket(delivery),1,2,now);
        Assert(delivery.Avoided(key,now).Contains("direct"),"One success cannot erase repeated body failures or lift quarantine.");
        delivery.Clear();Assert(delivery.Deliveries(now).Length==0,"Generation reset removes delivery evidence.");
        for(int i=0;i<400;i++){string t=delivery.Begin(key with{Domain=$"asset{i}.example.com"},"GET","Image","direct",1,2,now)!;delivery.Complete(t,1,2,now);}
        Assert(delivery.Deliveries(now).Length==32,"Diagnostic delivery snapshots are capped independently of traffic.");
        Assert(channel.Fail(Ticket(channel),"ERR_QUIC_PROTOCOL_ERROR",true,1,2,now)==null,"Browser cancellations cannot become transport failures.");
        Assert(channel.Fail(Ticket(channel),"ERR_CONNECTION_CLOSED",false,2,2,now)==null&&channel.Fail(Ticket(channel),"ERR_CONNECTION_CLOSED",false,1,3,now)==null,"Changed route publication or generation invalidates resource tickets.");
        Assert(channel.Fail(Ticket(channel),"ERR_CONNECTION_CLOSED",false,1,2,now.AddSeconds(91))==null&&channel.Fail(Ticket(channel),"ERR_CONNECTION_CLOSED",false,1,2,now.AddSeconds(-1))==null,"Expired and future tickets cannot affect a route.");
        string one=Ticket(channel);Assert(channel.Fail(one,"ERR_CONNECTION_RESET",false,1,2,now)!=null&&channel.Fail(one,"ERR_CONNECTION_RESET",false,1,2,now)==null,"One request token is consumed once.");
        Assert(channel.Avoided(key,now).Count==0,"One failed resource remains only a check hint.");
        var transport=new PolicyWebSignals();transport.Fault(key,"direct","same-flow",now);
        Assert(transport.Avoided(key,now).Count==0&&channel.Avoided(key,now).Count==0,"Engine and browser reports of one failure are not added together.");
        channel.Fail(Ticket(channel),"ERR_CONTENT_LENGTH_MISMATCH",false,1,2,now);
        Assert(channel.Avoided(key,now).SetEquals(new[]{"direct"})&&channel.Avoided(key with{Domain="other.example.com"},now).Count==0,"Two distinct incomplete resources quarantine only their observed exact route.");
        long tick=0;channel=new(()=>tick);int accepted=0;
        for(int i=0;i<20;i++)if(channel.Fail(Ticket(channel),"ERR_CONNECTION_RESET",false,1,2,now)!=null)accepted++;
        Assert(accepted==8,"Feedback has a bounded eight-event initial burst.");tick=3000;
        Assert(channel.Fail(Ticket(channel),"ERR_CONNECTION_RESET",false,1,2,now)!=null&&channel.Fail(Ticket(channel),"ERR_CONNECTION_RESET",false,1,2,now)==null,"At most twenty new failure hints per minute refill the budget.");
        channel=New();for(int i=0;i<600;i++)channel.Begin(key,"GET","Image","direct",1,2,now);
        Assert(channel.Pending==512,"Incomplete-request storage must be bounded.");channel.Begin(key,"GET","Image","direct",1,2,now.AddSeconds(91));Assert(channel.Pending==1,"Old requests are retired without network probes.");
        channel=New();channel.Fail(Ticket(channel),"ERR_QUIC_PROTOCOL_ERROR",false,1,2,now);
        int calls=0;var selection=await PolicyTransport.Select((_,_)=>{calls++;return Task.FromResult(PolicyTransport.HttpResponse(200,1));},new(),CancellationToken.None);
        var decision=new PolicyDecision(key,selection.Route!,"test",now.AddSeconds(1),now.AddMinutes(5),1);
        var result=new CalibrationResult(selection.Outcome,decision,0,1,selection.Candidates);
        Assert(calls>=2&&selection.Candidates.Single(c=>c.Route==decision.Route).Outcome==ProbeOutcome.Reachable,"Reachable selector outcomes represent two independent probes.");
        Assert(channel.Calibration(key,now.AddSeconds(-1),result,true)&&!channel.Confirmed(decision,now.AddSeconds(2)),"A calibration started before the error must be repeated.");
        channel.Calibration(key,now,result,false);Assert(!channel.Confirmed(decision,now.AddSeconds(2)),"Unaccepted decisions do not authorize TCP compatibility.");
        channel.Calibration(key,now,result with{Outcome=ProbeOutcome.TlsAuthenticationError},true);Assert(!channel.Confirmed(decision,now.AddSeconds(2)),"TLS refusal cannot authorize TCP compatibility.");
        channel.Calibration(key,now,result,true);Assert(channel.Confirmed(decision,now.AddSeconds(2))&&!channel.Confirmed(decision,now.AddMinutes(4)),"Fresh independent TCP proof authorizes temporary, expiring QUIC compatibility.");
        channel.Clear();Assert(channel.Pending==0&&!channel.Confirmed(decision,now.AddSeconds(2))&&channel.Avoided(key,now).Count==0,"Clear removes tickets, route faults and compatibility proofs.");
        Assert(!new PolicyOptions().ExperimentalBrowserFeedback,"User-browser observation remains opt-in until an authenticated adapter is delivered.");
        if(native)await BrowserNativeTests.Run();
        Console.WriteLine("PASS: browser GET-resource identity, cancelled/client/TLS errors, generations, publication tickets, independent evidence, bounded queues/rate and fresh TCP fallback proof.");
    }
}
