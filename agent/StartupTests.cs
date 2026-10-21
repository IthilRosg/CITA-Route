using Cita.Startup;

namespace RoutePilot.Agent;

internal static class StartupTests
{
    internal static void Run()
    {
        static void Assert(bool value,string message){if(!value)throw new Exception(message);}
        foreach(string status in new[]{"Нет подключения\r\n","Не подключено","Отключено","Not connected","NotConnected","No connection","Disconnected","Connected to server using profile Test","Подключено к серверу test"})
        {
            Assert(BootRouting.WireSockReady(0,status),"Known WireSock status must release startup readiness: "+status);
            Assert(!BootRouting.WireSockReady(1,status),"Failed CLI calls cannot release startup readiness.");
        }
        foreach(string status in new[]{"","Fixture service is not ready","Connecting using profile Test","Устанавливается соединение с использованием профиля Test","Error: Not connected","Not connected; service unavailable","Connected to"})
            Assert(!BootRouting.WireSockReady(0,status),"Unconfirmed WireSock status must keep waiting: "+status);
        DateTime boot=DateTime.UtcNow-TimeSpan.FromMinutes(3);
        Assert(StartupPaths.SameBoot(boot.AddMilliseconds(200),boot),"Service restart must not reconnect after manual Stop in the same Windows boot.");
        Assert(!StartupPaths.SameBoot(boot.AddHours(-1),boot),"A new Windows boot must permit the configured startup attempt.");
        Assert(!StartupPaths.SameBoot(default,boot),"Missing startup journal cannot claim a previous attempt.");
        foreach(string phase in new[]{"WaitingForNetwork","Starting","Connected","Stopping","StopFailed","NeedsAttention"})Assert(new StartupState{Phase=phase}.Active,"Uncertain startup ownership must block parallel route operations: "+phase);
        foreach(string phase in new[]{"Disabled","ReadyForBoot","StoppedForBoot"})Assert(!new StartupState{Phase=phase}.Active,"An intentionally inactive startup route cannot block ordinary connection.");
        string root=Path.Combine(Path.GetTempPath(),"CITA startup source");
        var state=new StartupState{SourceRoot=root};
        Assert(state.Owns(root+"\\")&&!state.Owns(root+"-copy"),"A second installation cannot adopt a similarly named startup route.");
        var config=new AgentConfig{WireSockCliPath="cli",WireSockSourceProfile="A",SourceEndpoint="example.invalid:1"};
        string signature=BootRouting.ConnectionSignature(config);
        config.WireSockSourceProfile="B";
        Assert(signature!=BootRouting.ConnectionSignature(config),"Profile change must invalidate the saved startup snapshot.");
        config.WireSockSourceProfile="A";config.ExpectedEgressIp="203.0.113.1";
        Assert(signature!=BootRouting.ConnectionSignature(config),"Egress change must invalidate the saved startup snapshot.");
        var now=DateTime.UtcNow;var began=now.AddSeconds(-30);var id=Guid.NewGuid().ToString("N");
        var failure=new PolicyAckFailure{SessionId=id,Stage="SocksHandshake",Code="ConnectionRefused",UpdatedUtc=now.AddSeconds(-1)};
        var pulse=new PolicyPulse{SessionId=id,Phase="Error",ProgressUtc=now};var ended=new OrdinarySession();
        Assert(BootRouting.RetryPolicyStartup(failure,pulse,ended,began,now),"Fresh acknowledged policy startup failure may retry after cleanup.");
        ended.Running=true;Assert(!BootRouting.RetryPolicyStartup(failure,pulse,ended,began,now),"An active or incompletely cleaned session cannot retry.");ended.Running=false;
        pulse.SessionId="other";Assert(!BootRouting.RetryPolicyStartup(failure,pulse,ended,began,now),"A different policy generation cannot authorize a retry.");pulse.SessionId=id;
        pulse.Phase="Running";Assert(!BootRouting.RetryPolicyStartup(failure,pulse,ended,began,now),"Healthy publication followed by an egress failure cannot authorize a retry.");pulse.Phase="Error";
        foreach(string reason in new[]{"AccessDenied","EgressMismatch","Unknown","ProofMismatch"}){failure.Code=reason;Assert(!BootRouting.RetryPolicyStartup(failure,pulse,ended,began,now),"A security or unknown failure is not startup warmup.");}failure.Code="ConnectionRefused";
        foreach(var stamp in new[]{began.AddTicks(-1),now.AddSeconds(3),now.AddMinutes(-2)}){failure.UpdatedUtc=stamp;Assert(!BootRouting.RetryPolicyStartup(failure,pulse,ended,began,now),"Stale, unrelated and future-dated failures cannot authorize a retry.");}
    }
}
