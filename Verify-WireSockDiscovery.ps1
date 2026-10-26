$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ConnectionEnvironment.ps1')
. (Join-Path $PSScriptRoot 'WireSockCli.ps1')
function Check([bool]$condition,[string]$message){if(-not $condition){throw $message}}
$base=Join-Path $PSScriptRoot ('runtime\discovery-test-'+[guid]::NewGuid().ToString('N'))
try {
 $usage="list`r`nconnect <profile> [-network-lock <mode>] [-exit] [-log-level <level>]`r`ndisconnect`r`nstatus`r`nimport <file>`r`nexport <profile> <file>`r`ndelete <profile>"
 Check (Test-WireSockManagementUsage $usage) 'Modern management capabilities must be explicit in help output.'
 foreach($required in @('list','export','delete','-network-lock','-exit','-log-level')){Check (-not(Test-WireSockManagementUsage $usage.Replace($required,'unsupported'))) 'Missing command or option must not be inferred from the executable name/version.'}
 Check (-not(Test-WireSockManagementUsage 'install run -config filename')) 'The SDK console cannot be passed profile management operations.'
 foreach($name in @('OldRoot','NewRoot\command-line','NewRoot\bin','NewRoot\service','RootCli','Unknown')){New-Item -ItemType Directory (Join-Path $base $name) -Force|Out-Null}
 foreach($name in @('OldRoot\wiresock-client.exe','NewRoot\command-line\wiresock-connect-cli.exe','NewRoot\bin\WireSockConnect.exe','NewRoot\service\wiresock-connect-service.exe','RootCli\wiresock-connect-cli.exe','Unknown\unrelated.exe')){[IO.File]::WriteAllBytes((Join-Path $base $name),[byte[]]@())}
 $fromApp=Resolve-WireSockInstallationHints @((Join-Path $base 'NewRoot\bin\WireSockConnect.exe'))
 Check ($fromApp.CliPath -eq (Join-Path $base 'NewRoot\command-line\wiresock-connect-cli.exe')) 'GUI path must discover sibling CLI.'
 $fromService=Resolve-WireSockInstallationHints @((Join-Path $base 'NewRoot\service\wiresock-connect-service.exe'))
 Check ($fromService.CliPath -eq $fromApp.CliPath) 'Service ImagePath must discover sibling CLI.'
 $rootCli=Resolve-WireSockInstallationHints @((Join-Path $base 'RootCli'))
 Check ($rootCli.Kind -eq 'SecureConnectCli') 'CLI in installation root must be detected without a fixed command-line layout or latest version.'
 $legacy=Resolve-WireSockInstallationHints @((Join-Path $base 'OldRoot'))
 Check ($legacy.Kind -eq 'LegacyClient' -and -not $legacy.CliPath -and $legacy.ExecutablePath) 'Legacy engine must be found and distinguished, never passed modern profile commands.'
 $both=Resolve-WireSockInstallationHints @((Join-Path $base 'OldRoot'),(Join-Path $base 'NewRoot'))
 Check ($both.Kind -eq 'SecureConnectCli') 'A compatible CLI must win over an installed legacy engine.'
 $stale=Resolve-WireSockInstallationHints @((Join-Path $base 'Missing\wiresock-connect-cli.exe'),(Join-Path $base 'RootCli'))
 Check ($stale.CliPath -eq $rootCli.CliPath) 'Stale saved paths must not prevent rediscovery.'
 Check ((Resolve-WireSockInstallationHints @((Join-Path $base 'Unknown\unrelated.exe'))).Kind -eq 'NotFound') 'Unrelated executables must not be accepted.'
 $quoted='"'+(Join-Path $base 'NewRoot\service\wiresock-connect-service.exe')+'" -service --ignored'
 Check ((Get-WireSockImagePath $quoted) -eq (Join-Path $base 'NewRoot\service\wiresock-connect-service.exe')) 'Quoted service paths must exclude arguments.'
 Check ((Get-WireSockImagePath ('"'+$fromApp.CliPath+'",0')) -eq $fromApp.CliPath) 'DisplayIcon resource indexes must be removed.'
 'PASS: GUI/service/root CLI discovery, legacy distinction, coexisting versions, stale paths, unrelated executables and quoted registry paths; no executable was run.'
}finally{
 $resolved=[IO.Path]::GetFullPath($base);$allowed=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runtime'))+'\'
 if(-not $resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe discovery fixture cleanup'}
 if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
