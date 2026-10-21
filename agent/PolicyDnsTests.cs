using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace RoutePilot.Agent;

internal static class PolicyDnsTests
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    internal static async Task Run()
    {
        var ports=new PolicyPorts();var calls=new ConcurrentDictionary<int,int>();var observed=new ConcurrentBag<(int Port,IPAddress Address)>();
        async Task<IPAddress[]?> Resolve(int port,CancellationToken stop)
        {calls.AddOrUpdate(port,1,(_,n)=>n+1);await Task.Delay(port==ports.Direct?20:5,stop);return new[]{IPAddress.Parse(port==ports.Direct?"1.1.1.1":"8.8.8.8")};}
        var result=await PolicyTransport.SelectResolved("example.com",ports,CancellationToken.None,resolve:Resolve,probe:(ip,port,_)=>
        {observed.Add((port,ip));return Task.FromResult(new CandidateResult(port!=ports.Dpi,false,"fixture",1));});
        Assert(result.Selection.Route=="direct"&&calls[ports.Direct]==1&&calls[ports.Vpn]==1,"Direct/DPI share one direct DNS query and retain priority over faster VPN DNS.");
        Assert(observed.All(v=>v.Address.Equals(IPAddress.Parse(v.Port==ports.Vpn?"8.8.8.8":"1.1.1.1"))),"Each probe must pin the public address resolved through its own operational DNS path.");
        int directProbes=0;
        result=await PolicyTransport.SelectResolved("example.com",ports,CancellationToken.None,
            resolve:(port,_)=>Task.FromResult<IPAddress[]?>(new[]{IPAddress.Parse(port==ports.Direct?"127.0.0.1":"8.8.8.8")}),
            probe:(_,port,_)=>{if(port!=ports.Vpn)directProbes++;return Task.FromResult(new CandidateResult(true,true,"TLS",1));});
        Assert(result.Selection.Route=="vpn"&&directProbes==0&&result.Selection.Candidates.Take(2).All(c=>c.Outcome==ProbeOutcome.DnsUnavailable),"Private/unavailable direct DNS must not produce a direct success using a VPN DNS answer.");
        int probes=0;
        result=await PolicyTransport.SelectResolved("example.com",ports,CancellationToken.None,
            resolve:(_,_)=>Task.FromResult<IPAddress[]?>(null),probe:(_,_,_)=>{probes++;throw new Exception("No DNS");});
        Assert(result.Selection.Route==null&&result.Selection.Outcome==ProbeOutcome.DnsUnavailable&&probes==0,"An all-DNS outage is not a valid route or a TLS success.");
        int active=0;
        async Task<IPAddress[]?> SlowVpn(int port,CancellationToken stop)
        {if(port==ports.Direct)return new[]{IPAddress.Parse("1.1.1.1")};Interlocked.Increment(ref active);try{await Task.Delay(5000,stop);return new[]{IPAddress.Parse("8.8.8.8")};}finally{Interlocked.Decrement(ref active);}}
        var timer=Stopwatch.StartNew();
        result=await PolicyTransport.SelectResolved("example.com",ports,CancellationToken.None,resolve:SlowVpn,probe:(_,_,_)=>Task.FromResult(new CandidateResult(true,true,"TLS",1)));
        Assert(result.Selection.Route=="direct"&&active==0&&timer.Elapsed<TimeSpan.FromSeconds(2),"Selecting direct must cancel and drain an unused VPN DNS query.");
        using(var stop=new CancellationTokenSource(35))
        {
            bool cancelled=false;
            try{await PolicyTransport.SelectResolved("example.com",ports,stop.Token,resolve:async(_,token)=>{Interlocked.Increment(ref active);try{await Task.Delay(5000,token);return null;}finally{Interlocked.Decrement(ref active);}});}
            catch(OperationCanceledException){cancelled=true;}
            Assert(cancelled&&active==0,"Stop must drain both candidate DNS lookups without starting new probes.");
        }
        int directCalls=0;
        var selected=await PolicyTransport.Select((port,_)=>Task.FromResult(new CandidateResult(port!=ports.Direct||Interlocked.Increment(ref directCalls)==1,false,"intermittent",1)),ports,CancellationToken.None,true);
        Assert(selected.Route=="dpi"&&PolicyTransport.UnstablePreferred(selected),"A failed-flow hint may use fully confirmed DPI after partial direct, preserving DPI priority over VPN.");
        int[] attempts=new int[3];
        selected=await PolicyTransport.Select((port,_)=>{int index=port==ports.Direct?0:port==ports.Dpi?1:2;int count=Interlocked.Increment(ref attempts[index]);return Task.FromResult(new CandidateResult(index==2||count==1,false,"intermittent",1));},ports,CancellationToken.None,true);
        Assert(selected.Route=="vpn"&&PolicyTransport.UnstablePreferred(selected),"A failing flow with partial direct/DPI may use a VPN confirmed twice.");
        selected=await PolicyTransport.Select((port,_)=>Task.FromResult(new CandidateResult(port!=ports.Direct,true,"TLS",1)),ports,CancellationToken.None,true);
        Assert(selected.Route==null&&selected.Outcome==ProbeOutcome.TlsAuthenticationError,"A failure hint cannot bypass a certificate refusal.");
        Console.WriteLine("PASS: per-route public DNS/IP pinning, shared direct/DPI resolution, unavailable/private DNS refusal, cancellation/draining and temporary verified fallback for failed flows.");
    }
}
