using Cita.Startup;

namespace RoutePilot.Agent;

internal static class StartupTests
{
    internal static void Run()
    {
        static void Assert(bool value,string message){if(!value)throw new Exception(message);}
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
    }
}
