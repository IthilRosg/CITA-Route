using System.Net;

namespace RoutePilot.Agent;

internal static class RouteDiagnosticTests
{
    internal static async Task Run()
    {
        void Assert(bool value,string message){if(!value)throw new Exception(message);}
        var ports=new PolicyPorts();int lookups=0,calls=0;bool current=true;
        Task<IPAddress[]?> Resolve(int port,CancellationToken _){Interlocked.Increment(ref lookups);return Task.FromResult<IPAddress[]?>(new[]{IPAddress.Parse("1.1.1.1")});}
        Task<CandidateResult> Probe(string _,IPAddress ip,int port,CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(port==ports.Vpn?new CandidateResult(true,false,"body",1){Evidence="BodyComplete",HttpStatus=200,BodyBytes=120}:
                PolicyTransport.HttpResponse(403,1,true));
        }
        var result=await RouteDiagnostic.Check("example.com",ports,()=>current,CancellationToken.None,Resolve,Probe);
        Assert(result.Current&&lookups==2&&calls==6&&result.Paths.Single(p=>p.Route=="vpn").Outcome=="ContentConfirmed"&&
            result.Paths.Take(2).All(p=>p.Outcome=="Indeterminate"),"Manual diagnosis must distinguish application challenges, share direct/DPI DNS and confirm each path twice.");
        result=await RouteDiagnostic.Check("example.com",ports,()=>false,CancellationToken.None,Resolve,Probe);
        Assert(!result.Current&&result.Paths.Length==0&&calls==6,"An inactive session must perform no diagnostic requests.");
        result=await RouteDiagnostic.Check("example.com",ports,()=>current,CancellationToken.None,Resolve,(d,ip,p,t)=>{current=false;return Probe(d,ip,p,t);});
        Assert(!result.Current&&result.Paths.Length==0&&result.Code=="SessionChanged","A changed session invalidates all diagnostic results.");
        current=true;
        result=await RouteDiagnostic.Check("example.com",ports,()=>current,CancellationToken.None,
            (_,_)=>Task.FromResult<IPAddress[]?>(new[]{IPAddress.Loopback}),(_,_,_,_)=>throw new Exception("Private address was probed"));
        Assert(result.Paths.All(p=>p.Outcome=="UnsafeAddress"),"Unsafe DNS answers cannot authorize diagnostic network access.");
        Console.WriteLine("PASS: bounded manual route diagnosis, shared DNS, independent body evidence, inactive/changed sessions and private-address refusal; no route mutation.");
    }
}
