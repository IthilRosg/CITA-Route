namespace RoutePilot.Agent;

internal static class PolicyBodyProbeTests
{
    sealed class Transfer(int bytes,bool stall,int delay=0):Stream
    {
        int remaining=bytes;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)
        {
            if(remaining==0){if(stall)await Task.Delay(Timeout.Infinite,cancellationToken);return 0;}
            if(delay>0)await Task.Delay(delay,cancellationToken);
            int n=Math.Min(remaining,buffer.Length);remaining-=n;buffer.Span[..n].Clear();return n;
        }
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush()=>throw new NotSupportedException();public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long v)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
    }
    internal static async Task Run()
    {
        void Assert(bool ok,string text){if(!ok)throw new Exception(text);}
        async Task<PolicyBodyProbe.Result> Read(int bytes,long? expected,bool stall=false,int delay=0)
        {using var stream=new Transfer(bytes,stall,delay);using var budget=new CancellationTokenSource(2000);return await PolicyBodyProbe.Read(stream,expected,budget.Token,CancellationToken.None,TimeSpan.FromMilliseconds(80));}
        foreach(int boundary in new[]{15917,24576,32768})
        {var body=await Read(boundary,100000,true);Assert(!body.Success&&body.Evidence=="BodyStalled"&&body.Bytes==boundary,"Partial body cannot pass on headers/first TLS records.");}
        var full=await Read(40000,40000);Assert(full.Success&&full.Evidence=="BodyComplete","Full declared response.");
        var small=await Read(120,120);Assert(small.Success&&small.Bytes==120,"Small complete response must not pretend to be a large sample.");
        var shortBody=await Read(400,500);Assert(!shortBody.Success&&shortBody.Evidence=="BodyIncomplete","Premature EOF must fail.");
        var sample=await Read(100000,100000);Assert(sample.Success&&sample.Evidence=="BodySample"&&sample.Bytes==65536,"Hard bounded body budget.");
        var slow=await Read(16384,16384,delay:20);Assert(slow.Success,"Progressing slow transfer is not an idle failure.");
        using(var budget=new CancellationTokenSource(25))using(var stream=new Transfer(100000,true,20))
        {var unknown=await PolicyBodyProbe.Read(stream,100000,budget.Token,CancellationToken.None);Assert(!unknown.Success&&unknown.Evidence=="BodyBudgetExceeded","Global budget cannot claim idle failure.");}
        using(var stop=new CancellationTokenSource(20))using(var stream=new Transfer(16000,true))
        {try{await PolicyBodyProbe.Read(stream,100000,stop.Token,stop.Token);throw new Exception("Stop ignored");}catch(OperationCanceledException){}}
        var ports=new PolicyPorts();
        var selection=await PolicyTransport.Select((port,_)=>Task.FromResult(port==ports.Vpn?new CandidateResult(true,false,"body",1){Evidence="BodySample",BodyBytes=65536}:new CandidateResult(false,false,"stalled",1){Evidence="BodyStalled",BodyBytes=15917}),ports,CancellationToken.None);
        Assert(selection.Route=="vpn"&&selection.Candidates.Take(2).All(c=>c.Outcome==ProbeOutcome.ApplicationFailure)&&selection.Candidates[2].BodyBytes==65536,"Two stalled body checks must select verified alternate.");
        var key=new PolicyKey("test","example.com",@"C:\test\chrome.exe",443);var at=DateTime.UtcNow;
        var previous=new PolicyDecision(key,"vpn","body",at,at.AddMinutes(5),1){Evidence="BodySample"};
        Assert(!new PolicyStability().Accept(previous,previous with{Route="direct",Evidence="Headers"},selection.Candidates,at),"HEAD cannot erase body evidence.");
        Assert(!new PolicyStability().Accept(previous,previous with{Evidence="Headers"},selection.Candidates,at),"HEAD cannot refresh body TTL on the same route.");
        Assert(PolicyWorker.NewBodyPublication(previous,0,1,at.AddSeconds(-1)),"A first resource may retry when body proof was published after it began, even before its eventual failure.");
        Assert(!PolicyWorker.NewBodyPublication(previous,1,1,at.AddSeconds(-1)),"Same route revision cannot trigger replay.");
        Assert(!PolicyWorker.NewBodyPublication(previous,-1,1,at.AddSeconds(-1)),"Legacy watches have no body-retry identity.");
        Assert(!PolicyWorker.NewBodyPublication(previous,0,1,at.AddSeconds(1)),"Cached proof predating request is not a repair.");
        Assert(!PolicyWorker.NewBodyPublication(previous with{Evidence="Headers"},0,1,at.AddSeconds(-1)),"Header publication cannot repair a body failure.");
        Console.WriteLine("PASS: partial-body plateaus, EOF/length, bounded sampling, slow progress, unknown budget, Stop and two-probe body failover.");
    }
}
