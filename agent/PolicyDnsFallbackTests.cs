using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace RoutePilot.Agent;

internal static class PolicyDnsFallbackTests
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    internal static async Task Run()
    {
        PolicyDnsAnswer Parse(string json)=>PolicyDns.Parse(Encoding.UTF8.GetBytes(json));
        Assert(Parse("{\"Status\":3}") is {Terminal:true,Addresses:null},"NXDOMAIN is a terminal negative answer, not a resolver outage.");
        Assert(Parse("{\"Status\":0,\"Answer\":[]}") is {Terminal:true,Addresses:null},"NODATA must not be replaced with an unrelated provider answer.");
        Assert(!Parse("{\"Status\":2}").Terminal&&!Parse("{\"Status\":5}").Terminal,"SERVFAIL/REFUSED permit bounded resolver fallback.");
        Assert(Parse("{\"Status\":0,\"Answer\":[{\"type\":1,\"data\":\"127.0.0.1\"}]}").Addresses==null,"Probe DNS must still reject a private address.");
        int calls=0;
        var result=await PolicyTransport.ResolveConfigured("fixture.example.com",1,CancellationToken.None,(resolver,_)=>
        {calls++;return Task.FromResult(calls==1?new PolicyDnsAnswer(null,false):new(new[]{resolver.Address},true));});
        Assert(calls==2&&result![0].Equals(PolicyDns.Resolvers[1].Address),"Temporary resolver failure must reach the configured backup.");
        calls=0;
        result=await PolicyTransport.ResolveConfigured("fixture.example.com",1,CancellationToken.None,(_,_)=>{calls++;return Task.FromResult(new PolicyDnsAnswer(null,true));});
        Assert(result==null&&calls==1,"A valid negative answer must not query the backup.");
        using(var stop=new CancellationTokenSource())
        {
            calls=0;bool cancelled=false;
            try{await PolicyTransport.ResolveConfigured("fixture.example.com",1,stop.Token,(_,_)=>{calls++;stop.Cancel();return Task.FromResult(new PolicyDnsAnswer(null,false));});}
            catch(OperationCanceledException){cancelled=true;}
            Assert(cancelled&&calls==1,"Stop must not launch a new resolver query.");
        }
        var first=IPAddress.Parse("1.1.1.1");var second=IPAddress.Parse("8.8.8.8");calls=0;
        var probe=await PolicyTransport.ProbeAddresses(new[]{first,first,second},(ip,_)=>{calls++;return Task.FromResult(new CandidateResult(ip.Equals(second),false,"TCP fixture",1));},CancellationToken.None);
        Assert(probe.Reachable&&calls==2,"A refused first TCP address must permit a distinct backup without probing duplicate answers.");
        foreach(bool certificate in new[]{false,true})
        {
            calls=0;
            probe=await PolicyTransport.ProbeAddresses(new[]{first,second},(_,_)=>{calls++;return Task.FromResult(new CandidateResult(false,certificate,"post-connect refusal",1){ConnectionEstablished=true});},CancellationToken.None);
            Assert(!probe.Reachable&&calls==1,"A TLS/HTTP failure after TCP establishment must not falsely certify an alternate address the data stream cannot use.");
        }
        calls=0;
        await PolicyTransport.ProbeAddresses(Enumerable.Repeat(first,1).Concat(new[]{second,IPAddress.Parse("9.9.9.9"),IPAddress.Parse("1.0.0.1"),IPAddress.Parse("8.8.4.4")}).ToArray(),(_,_)=>{calls++;return Task.FromResult(new CandidateResult(false,false,"refused",1));},CancellationToken.None);
        Assert(calls==4,"A DNS answer must not create an unbounded number of probe attempts.");
        var config=JsonNode.Parse("{\"dns\":{\"servers\":[{\"type\":\"fakeip\",\"tag\":\"fakeip\"},{\"type\":\"udp\",\"tag\":\"dns-tailnet\"},{\"type\":\"https\",\"tag\":\"dns-direct\"}]}}")!;
        PolicyDns.Configure(config);PolicyDns.Configure(config);
        var servers=config["dns"]!["servers"]!.AsArray();
        Assert(servers.Count==8,"Repeated migration must preserve FakeIP/tailnet and distinct route transport tags without duplicate servers.");
        foreach(string route in new[]{"direct","vpn"})
        foreach(string suffix in new[]{"-primary","-backup"})
            Assert(servers.Single(s=>s!["tag"]!.GetValue<string>()=="dns-"+route+suffix)!["detour"]!.GetValue<string>()==route,"DNS backup must never cross the route boundary.");
        Console.WriteLine("PASS: configured DNS fallback, terminal negatives, route-isolated cache/config, bounded address retries and no post-connect address replay.");
    }
}
