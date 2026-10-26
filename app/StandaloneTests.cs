using System;
using System.IO;
namespace RoutePilot;
internal static class StandaloneTests
{
    internal static void Run(string root)
    {
        if(AppPaths.FindRoot()!=root)throw new Exception("Standalone root discovery must use native package markers.");
        NativeComponentBundleTests.Run(root);NativeStatisticsPresentationTests.Run();
        using var product=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"product.json")));
        if(product.RootElement.GetProperty("edition").GetString()!="native")throw new Exception("Standalone distribution is not marked native.");
        foreach(string name in new[]{"SmartRouting.ps1","WireSockCli.ps1","WireSockProfiles.ps1","WireSockRecovery.ps1","Configure-Connection.ps1","Install-Agent.ps1","runtime/vpn-bridge.exe","runtime/smart-router.exe"})
            if(File.Exists(Path.Combine(root,name)))throw new Exception("Legacy dependency present in standalone package: "+name);
        if(NativeComponentDownload.Bundled(root,default)==null)throw new Exception("Standalone must include its own verified transport.");
        var window=new MainWindow(root,true);window.VerifyStandalonePresentation();
    }
}
