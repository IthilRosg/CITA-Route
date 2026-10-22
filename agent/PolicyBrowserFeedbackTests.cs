namespace RoutePilot.Agent;

internal static class PolicyBrowserFeedbackTests
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    internal static async Task Run()
    {
        var now=DateTime.UtcNow;var key=new PolicyKey("browser-epoch","asset.example.com",@"C:\Browser\chrome.exe",443);
        PolicyBrowserFeedback New()=>new();
        string Ticket(PolicyBrowserFeedback channel,DateTime? at=null,long revision=1,int generation=2)=>channel.Begin(key,"GET","Image","direct",revision,generation,at??now)!;
        foreach(string error in new[]{"ERR_BLOCKED_BY_CLIENT","ERR_ABORTED","ERR_CERT_AUTHORITY_INVALID","ERR_SSL_PROTOCOL_ERROR","HTTP_403","ERR_FAILED"})
        {var c=New();Assert(c.Fail(Ticket(c),"net::"+error,false,1,2,now)==null,"Client blocks, cancellation, TLS/authentication and unknown failures must not change routing.");}
        var channel=New();
        Assert(channel.Begin(key,"POST","Fetch","direct",1,2,now)==null&&channel.Begin(key,"GET","Document","direct",1,2,now)==null&&channel.Begin(key with{Application=@"C:\Game\game.exe"},"GET","Image","direct",1,2,now)==null,"Only an identified browser's GET resources enter feedback.");
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
        Console.WriteLine("PASS: browser GET-resource identity, cancelled/client/TLS errors, generations, publication tickets, independent evidence, bounded queues/rate and fresh TCP fallback proof.");
    }
}
