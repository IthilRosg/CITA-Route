using System;

namespace RoutePilot;

internal static class WireSockCompatibilityTests
{
    internal static void Run()
    {
        static void Check(ConnectionInventory value,string code,bool manage,bool install)
        {
            var actual=WireSockCompatibility.Assess(value);
            if(actual.Code!=code||actual.CanManage!=manage||actual.OfferInstall!=install)throw new Exception("WireSock compatibility: "+code);
        }
        ConnectionInventory Cli(string version)=>new(){Kind="SecureConnectCli",CliPath="fixture",ExecutablePath="fixture",Version=version,CliTrusted=true,ManagementReady=true};
        Check(new(){Kind="NotFound"},"Missing",false,true);
        Check(new(){Kind="ApplicationOnly",ExecutablePath="fixture",Version="2.4.23.1"},"Unsupported",false,true);
        Check(new(){Kind="LegacyClient",ExecutablePath="fixture"},"Unsupported",false,true);
        Check(new(){Kind="ApplicationOnly",ExecutablePath="fixture",Version="3.6.1.1"},"Incomplete",false,true);
        Check(Cli("3.4.4.1"),"UpdateAvailable",true,true);
        Check(Cli("3.6.1.1"),"Reviewed",true,false);
        Check(Cli("3.7.2.1"),"Unreviewed",true,false);
        Check(Cli("4.0.0.0"),"Unreviewed",true,false);
        Check(Cli(""),"UnknownVersion",true,false);
        Check(Cli("3.6.1.1-beta"),"UnknownVersion",true,false);
        Check(Cli("2.4.23.1"),"Unsupported",false,true);
        Check(new(){Kind="SecureConnectCli",CliPath="fixture",Version="3.6.1.1"},"Untrusted",false,false);
        Check(new(){Kind="SecureConnectCli",CliPath="fixture",Version="3.4.8.1",CliTrusted=true},"MissingCapabilities",false,true);
        Check(new(){Kind="SecureConnectCli",CliPath="fixture",Version="3.6.1.1",CliTrusted=true},"MissingCapabilities",false,true);
        if(WireSockCompatibility.Parse("3.6.1")!=new Version(3,6,1,0)||WireSockCompatibility.Parse("999999999999.0.0")!=null)throw new Exception("Version normalization failed.");
    }
}
