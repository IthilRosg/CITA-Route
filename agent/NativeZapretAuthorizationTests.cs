namespace RoutePilot.Agent;

internal static class NativeZapretAuthorizationTests
{
    internal static Task Run()
    {
        static void Check(bool value,string message){if(!value)throw new IOException(message);}
        const string epoch="0123456789abcdef0123456789abcdef";long tick=500,wall=1000000;bool current=true;
        string[] addresses=["8.8.8.8"];
        var rule=new NativeDomainRule("Example.org.",@"C:\apps\browser.exe","tcp","zapret",443,addresses,wall+60000);
        var snapshot=NativeZapretAuthorization.CreateCandidate(epoch,7,[rule],v=>current&&v==7,wall,tick,()=>tick);
        var request=new NativeConnectorRequest(epoch,"",Guid.NewGuid().ToString("N"),wall,"example.org",@"c:\APPS\BROWSER.EXE","8.8.8.8",443,"tcp");
        Check(snapshot.Allows(request),"Exact candidate scope was not recognized.");
        addresses[0]="1.1.1.1";
        Check(snapshot.Allows(request)&&!snapshot.Allows(request with{Address="1.1.1.1"}),"Caller mutation changed the accepted destination.");
        foreach(var invalid in new[]{request with{Epoch=new string('a',32)},request with{Domain="sub.example.org"},request with{Domain="example.org.."},request with{Application=@"C:\apps\other.exe"},request with{Address="8.8.4.4"},request with{Network="udp"},request with{Port=80}})
            Check(!snapshot.Allows(invalid),"Connector authorization widened the exact scope.");
        current=false;Check(!snapshot.Allows(request),"Stale revision accepted.");current=true;
        tick+=60000;Check(!snapshot.Allows(request),"Expired scope accepted.");
        bool Refused(NativeDomainRule[] candidate)
        {try{NativeZapretAuthorization.CreateCandidate(epoch,7,candidate,_=>true,wall,500);return false;}catch(InvalidDataException){return true;}}
        var proved=NativeZapretAuthorization.CreateCandidate(epoch,7,[rule with{CalibrationId=Guid.NewGuid().ToString("N"),Addresses=["8.8.8.8"]}],_=>true,wall,500,()=>500);
        Check(proved.Allows(request)&&Refused([rule with{CalibrationId="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}]),"Private Zapret proof identity validation failed.");
        Check(Refused([rule with{Application=""}])&&Refused([rule with{Domain="example.org.."}])&&Refused([rule with{Addresses=["192.168.1.1"]}])&&Refused([rule with{Addresses=["26.1.2.3"]}]),"Unsafe scope was accepted.");
        Check(Refused([rule with{Network="udp"}])&&Refused([rule with{Route="direct"}])&&Refused([rule with{Forced=true}])&&Refused([rule with{ExpiresUnixMilliseconds=wall}]),"Unsupported decision was accepted.");
        Check(Refused([rule,rule]),"Duplicate scope was accepted.");
        Check(Refused([rule,rule with{Domain="EXAMPLE.ORG",Application=@"c:\APPS\BROWSER.EXE",Addresses=["8.8.4.4"]}]),"Duplicate policy keys with disjoint addresses were accepted.");
        Check(Refused([rule with{Domain=new string('a',10000)}])&&Refused([rule with{Domain="\u212A.example"}])&&Refused([rule with{Application=@"C:\пользователь\browser.exe"}]),"Oversized or unsupported Unicode scope was accepted.");
        var failing=NativeZapretAuthorization.CreateCandidate(epoch,7,[rule],_=>throw new Exception("current unavailable"),wall,500,()=>500);
        Check(!failing.Allows(request),"Authorization errors must fail closed.");
        return Task.CompletedTask;
    }
}
