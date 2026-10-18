using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RoutePilot;

public static class ConnectionSetupTests
{
    public static void Run(string root)
    {
        string folder=Path.Combine(root,"runtime","setup-test-кириллица ' & (x) $-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(folder,"runtime"));Directory.CreateDirectory(Path.Combine(folder,"logs"));
            File.Copy(Path.Combine(root,"FileStorage.ps1"),Path.Combine(folder,"FileStorage.ps1"));
            // No real CLI, profiles, services or routes are used. Execute the
            // production parent/child command under actual Windows PowerShell.
            File.WriteAllText(Path.Combine(folder,"Configure-Connection.ps1"),"""
                [CmdletBinding()]
                param([ValidateSet('Discover','Apply','Import')][string]$Action='Discover',
                    [string]$SourceProfile='', [string]$CliPath='', [string]$ImportPath='',
                    [string]$ExpectedEgressIp='', [string]$UserSid='', [string]$UserProfile='')
                $ErrorActionPreference='Stop'
                if($Action -eq 'Discover'){
                    @{CliPath=$CliPath;Profiles=@('Fixture');SelectedProfile='Fixture'}|ConvertTo-Json -Compress
                    exit 0
                }
                if($SourceProfile -eq 'Failure'){throw 'Controlled setup failure'}
                @{Action=$Action;SourceProfile=$SourceProfile;CliPath=$CliPath;ImportPath=$ImportPath;
                  ExpectedEgressIp=$ExpectedEgressIp;UserSid=$UserSid;UserProfile=$UserProfile}|
                    ConvertTo-Json -Compress|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'capture.json') -Encoding UTF8
                exit 0
                """,new UTF8Encoding(true));
            using var backend=new Backend(folder);
            string cli=Path.Combine(folder,"WireSock Secure Connect","wiresock-connect-cli.exe");
            foreach(string selectedCli in new[]{"",cli})
            {
                var result=backend.RunAsync("powershell.exe",ConnectionSetup.DiscoveryArguments(folder,selectedCli)).GetAwaiter().GetResult();
                if(result.Code!=0)throw new Exception("Setup discovery parameter binding failed.");
                var inventory=JsonSerializer.Deserialize<ConnectionInventory>(result.Output.Trim(),AppPaths.Json)!;
                if(inventory.CliPath!=selectedCli)throw new Exception("Discovery CLI path changed across the process boundary.");
            }
            string profile="Профиль ' & $(throw 'must not execute') \"quoted\"";
            string import=Path.Combine(folder,"my ' & $profile.conf");
            string sid="S-1-5-21-123",user=Path.Combine(folder,"User ' & (test)");
            foreach(var sample in new[]{
                (Action:"Apply",Profile:profile,Import:"",Egress:""),
                (Action:"Apply",Profile:profile,Import:"",Egress:"203.0.113.9"),
                (Action:"Import",Profile:"",Import:import,Egress:"")})
            {
                string id=Guid.NewGuid().ToString("N");
                string command=ConnectionSetup.OperationCommand(folder,id,sample.Action,cli,sample.Profile,sample.Import,sample.Egress,sid,user);
                var result=backend.RunAsync("powershell.exe",new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(command))}).GetAwaiter().GetResult();
                using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"runtime","connection-operation.json")));
                if(result.Code!=0 || report.RootElement.GetProperty("id").GetString()!=id || !report.RootElement.GetProperty("success").GetBoolean())throw new Exception("Setup operation did not report success: "+sample.Action);
                using var capture=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"capture.json")));
                foreach(var expected in new[]{("Action",sample.Action),("SourceProfile",sample.Profile),("CliPath",cli),("ImportPath",sample.Import),("ExpectedEgressIp",sample.Egress),("UserSid",sid),("UserProfile",user)})
                    if(capture.RootElement.GetProperty(expected.Item1).GetString()!=expected.Item2)throw new Exception("Setup argument changed: "+expected.Item1);
            }
            string failedId=Guid.NewGuid().ToString("N");
            string failure=ConnectionSetup.OperationCommand(folder,failedId,"Apply",cli,"Failure","","",sid,user);
            var failed=backend.RunAsync("powershell.exe",new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(failure))}).GetAwaiter().GetResult();
            using var failureReport=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"runtime","connection-operation.json")));
            if(failed.Code==0 || failureReport.RootElement.GetProperty("id").GetString()!=failedId || failureReport.RootElement.GetProperty("success").GetBoolean())throw new Exception("Setup failure was lost or an old success report was reused.");
        }
        finally
        {
            string allowed=Path.GetFullPath(Path.Combine(root,"runtime"))+Path.DirectorySeparatorChar;
            if(!Path.GetFullPath(folder).StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unsafe setup test cleanup path.");
            if(Directory.Exists(folder))Directory.Delete(folder,true);
        }
    }
}
