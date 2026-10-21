using System.Net;
using System.Text.Json;

namespace RoutePilot.Agent;

internal static class EgressReferenceTests
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    internal static async Task Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"Cita-egress-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string config=Path.Combine(root,"config.json"),reference=Path.Combine(root,"reference.json");
        void Config(string revision,string expected="")=>Storage.Write(config,new{ExpectedEgressIp=expected,WireSockCliPath="fixture",ConnectionRevision=revision});
        string Status(string ip)=>"Connected to server using profile SmartRouting-AWG2\r\nExternal address: "+ip+", Fixture\r\n";
        Task<EgressHealth> Probe(string reported,string actual)=>EgressReference.Verify(config,reference,_=>Task.FromResult(Status(reported)),()=>Task.FromResult(IPAddress.Parse(actual)));
        try
        {
            Config("profile-a");
            Assert(!(await Probe("8.8.8.8","1.1.1.1")).Verified&&!File.Exists(reference),"A mismatch must not create a trusted exit.");
            Assert((await Probe("8.8.8.8","8.8.8.8")).Verified,"The first independently compared exit must be accepted.");
            string first=File.ReadAllText(reference);
            Assert((await Probe("1.1.1.1","8.8.8.8")).Verified,"Changing CLI metadata after resume must not invalidate the same verified VPN exit.");
            Assert((await Probe("","8.8.8.8")).Verified,"Missing CLI exit metadata must not discard an established reference.");
            Assert(File.ReadAllText(reference)==first,"Healthy observations must not rewrite the reference every poll.");
            var mismatch=await Probe("1.1.1.1","1.1.1.1");
            Assert(!mismatch.Verified&&mismatch.Code=="EgressMismatch"&&File.ReadAllText(reference)==first,"Matching new CLI and HTTPS addresses cannot replace an established assertion.");
            bool traced=false;
            var foreign=await EgressReference.Verify(config,reference,_=>Task.FromResult("Connected to server using profile Other"),()=>{traced=true;return Task.FromResult(IPAddress.Parse("8.8.8.8"));});
            Assert(!foreign.Verified&&!traced,"A reference must not authorize a foreign profile.");
            int reads=0;
            var changed=await EgressReference.Verify(config,reference,_=>Task.FromResult(++reads==1?Status("8.8.8.8"):"No connection"),()=>Task.FromResult(IPAddress.Parse("8.8.8.8")));
            Assert(changed.Code=="ProfileChanged"&&!changed.Verified,"Recheck managed ownership after HTTPS even with a reference.");
            Config("profile-b");
            Assert(!(await Probe("1.1.1.1","8.8.8.8")).Verified,"A changed protected configuration cannot inherit the old exit assertion.");
            Assert((await Probe("1.1.1.1","1.1.1.1")).Verified,"A new profile revision must confirm its own exit.");
            Config("profile-b","8.8.4.4");
            Assert((await Probe("1.1.1.1","8.8.4.4")).Verified&&!(await Probe("8.8.8.8","1.1.1.1")).Verified,"An explicit expected exit retains priority.");
            Config("concurrent");File.Delete(reference);
            var concurrent=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>EgressReference.Verify(config,reference,
                image=>Task.FromResult(Status("8.8.8.8")),async()=>{await Task.Delay(40);return IPAddress.Parse("8.8.8.8");}))));
            Assert(concurrent.All(r=>r.Verified),"Concurrent first confirmations must serialize without false failure.");
            Config("competing-first-exits");File.Delete(reference);
            var firstTrace=new TaskCompletionSource<IPAddress>();var secondTrace=new TaskCompletionSource<IPAddress>();
            var firstProof=EgressReference.Verify(config,reference,_=>Task.FromResult(Status("8.8.8.8")),()=>firstTrace.Task);
            var secondProof=EgressReference.Verify(config,reference,_=>Task.FromResult(Status("1.1.1.1")),()=>secondTrace.Task);
            firstTrace.SetResult(IPAddress.Parse("8.8.8.8"));Assert((await firstProof).Verified,"The first complete proof must win.");
            secondTrace.SetResult(IPAddress.Parse("1.1.1.1"));Assert((await secondProof).Code=="EgressMismatch","A concurrent different exit must not replace the winning assertion.");
            string saved=File.ReadAllText(reference);File.WriteAllText(reference,"{broken");
            bool refused=false;try{await Probe("8.8.8.8","8.8.8.8");}catch(JsonException){refused=true;}
            Assert(refused,"A damaged assertion must not silently bootstrap another one.");
            File.WriteAllText(reference,new string(' ',16385));refused=false;
            try{await Probe("8.8.8.8","8.8.8.8");}catch(IOException){refused=true;}
            Assert(refused,"Oversized reference must be bounded.");File.WriteAllText(reference,saved);
            var reconfigured=await EgressReference.Verify(config,reference,_=>Task.FromResult(Status("8.8.8.8")),()=>{Config("changed-during-probe");return Task.FromResult(IPAddress.Parse("8.8.8.8"));});
            Assert(reconfigured.Code=="ProfileChanged"&&!reconfigured.Verified,"A late probe cannot validate a changed configuration.");
            File.Delete(reference);Config("cancelled");using var stop=new CancellationTokenSource();
            refused=false;try{await EgressReference.Verify(config,reference,_=>Task.FromResult(Status("8.8.8.8")),()=>{stop.Cancel();return Task.FromResult(IPAddress.Parse("8.8.8.8"));},stop.Token);}catch(OperationCanceledException){refused=true;}
            Assert(refused&&!File.Exists(reference),"Cancellation must not publish a new assertion.");
            Storage.Write(config,new{EndpointIp="8.8.8.8"});
            await Probe("8.8.8.8","8.8.8.8");
            Assert(!File.Exists(reference),"Legacy endpoint-only checks must never create a managed-profile assertion.");
            Assert(!JsonSerializer.Serialize(new EgressHealth(true,"Verified","test"){VerifiedExit="8.8.8.8"}).Contains("8.8.8.8"),"Health serialization must exclude the assertion address.");
            Console.WriteLine("PASS: protected profile-scoped exit confirmation, CLI drift after resume, real mismatch, explicit priority, concurrency, configuration changes and cancellation.");
        }
        finally{Directory.Delete(root,true);}
    }
}
