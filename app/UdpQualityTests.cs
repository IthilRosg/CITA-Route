using System;
using RoutePilot.Quality;

namespace RoutePilot;

public static class UdpQualityTests
{
    public static void Run()
    {
        static void Assert(bool condition,string text){if(!condition)throw new Exception(text);}
        var now=DateTime.UtcNow;
        var options=new UdpQualityOptions{Enabled=true,Address="1.1.1.1",Port=38565,Key=new string('a',64)};
        var session=new EngineSession{Id="fixture",Running=true,SourceProfile="profile",NetworkSignature="network",BridgeGeneration=3,BridgeReady=true};
        var state=new UdpQualityState{SessionId=session.Id,SourceProfile=session.SourceProfile,NetworkSignature=session.NetworkSignature,BridgeGeneration=3,ConfigurationId=options.Identity,UpdatedUtc=now,
            Direct=new(){Status="Measured",Sent=10,Received=8,LossPercent=20,UpdatedUtc=now,WindowStartedUtc=now.AddSeconds(-6)},
            Vpn=new(){Status="Measured",Sent=10,Received=9,LossPercent=10,UpdatedUtc=now,WindowStartedUtc=now.AddSeconds(-6)}};
        (string Text,string Detail) Show(bool vpn=false)=>UdpQualityPresentation.Describe(state,options,session,true,vpn,now);
        Assert(Show().Text=="20%" && Show(true).Text=="10%","Direct and VPN mini statistics must remain independent.");
        state.SessionId="old";Assert(!Show().Text.Contains('%'),"Old session measurements must never appear as current.");state.SessionId=session.Id;
        state.BridgeGeneration--;Assert(!Show(true).Text.Contains('%'),"Old bridge measurements cannot appear current.");state.BridgeGeneration++;
        state.NetworkSignature="old";Assert(!Show().Text.Contains('%'),"Another network invalidates quality results.");state.NetworkSignature=session.NetworkSignature;
        state.UpdatedUtc=now.AddSeconds(-16);Assert(!Show().Text.Contains('%'),"Stopped collector heartbeat must expire.");state.UpdatedUtc=now;
        state.Direct.UpdatedUtc=now.AddSeconds(-26);Assert(!Show().Text.Contains('%'),"A recent heartbeat cannot revive an old measurement.");state.Direct.UpdatedUtc=now;
        state.Direct.Received=0;state.Direct.LossPercent=100;Assert(!Show().Text.Contains('%'),"No replies must not show 100% loss.");
        state.Direct.Received=8;state.Direct.LossPercent=0;Assert(!Show().Text.Contains('%'),"Inconsistent persisted statistics must fail closed.");
        state.Direct.LossPercent=20;session.Running=false;Assert(Show().Text=="не измерено","Manual Stop invalidates all displayed probe statistics.");session.Running=true;
        options.Key=new string('b',64);Assert(!Show().Text.Contains('%'),"Changing the reflector key invalidates prior statistics.");
    }
}
