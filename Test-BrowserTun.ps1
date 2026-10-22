#requires -Version 5.1
# Run INSIDE the prepared VMware guest. Never switches the installed pilot.
$ErrorActionPreference='Stop'
$browserMaker=[string](Get-ItemProperty 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS').SystemManufacturer
if($browserMaker -notmatch 'VMware'){throw 'This acceptance runner is restricted to a VMware guest.'}
if(-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'An elevated guest token is required.'}
$browserPilot=Join-Path $env:USERPROFILE 'CITA Test\Client\client'
function Read-BrowserPilot {Get-Content -LiteralPath (Join-Path $browserPilot 'runtime\session.json') -Raw|ConvertFrom-Json}
function Get-BrowserNetwork {
    $routes=@(Get-NetRoute -PolicyStore ActiveStore|Where-Object {$_.DestinationPrefix -in @('0.0.0.0/0','0.0.0.0/1','128.0.0.0/1','::/0','::/1','8000::/1')}|Sort-Object InterfaceIndex,DestinationPrefix,NextHop|ForEach-Object {('{0}|{1}|{2}|{3}' -f $_.InterfaceIndex,$_.DestinationPrefix,$_.NextHop,$_.RouteMetric)})
    $dns=@(Get-DnsClientServerAddress|Where-Object {$_.InterfaceAlias -ne 'Cita-WebLab'}|Sort-Object InterfaceIndex,AddressFamily|ForEach-Object {('{0}|{1}|{2}' -f $_.InterfaceIndex,$_.AddressFamily,($_.ServerAddresses -join ','))})
    @{routes=($routes -join ';');dns=($dns -join ';')}
}
$browserReport=@{success=$false;startedUtc=[datetime]::UtcNow.ToString('o')};$browserRun=$null;$browserStarted=$false
try {
    $browserBefore=Read-BrowserPilot
    if(-not $browserBefore.running){throw 'An active ordinary pilot session is required.'}
    if(Get-NetRoute -DestinationPrefix '203.0.113.231/32' -ErrorAction SilentlyContinue|Where-Object {$_.InterfaceAlias -eq 'Cita-WebLab'}){throw 'A scoped browser test is already active.'}
    $browserNetwork=Get-BrowserNetwork
    $browserManifest=Get-Content 'C:\CitaLab\Adaptive\browser\stage.json' -Raw|ConvertFrom-Json
    if($browserManifest.files.Count -ne 6){throw 'Unexpected fixture manifest.'}
    foreach($browserFile in $browserManifest.files){if((Get-FileHash -LiteralPath $browserFile.guest).Hash -ne $browserFile.sha256){throw 'Fixture artifact differs from the staging manifest.'}}
    $browserRoot='C:\CitaLab\BrowserTun\'+[guid]::NewGuid().ToString('N');$browserReport.labRoot=$browserRoot
    $browserRun=Start-Process 'C:\CitaLab\Adaptive\browser\RoutePilot.Agent.exe' -ArgumentList ('--browser-tun-lab "'+$browserRoot+'"') -WindowStyle Hidden -PassThru -RedirectStandardOutput 'C:\CitaLab\browser-tun.out' -RedirectStandardError 'C:\CitaLab\browser-tun.err'
    $null=$browserRun.Handle;$browserStarted=$true
    if(-not $browserRun.WaitForExit(235000)){$browserRun.Kill();throw 'Browser acceptance exceeded its deadline.'}
    $browserRun.Refresh();$browserReport.exitCode=$browserRun.ExitCode
    if(Test-Path ($browserRoot+'\result.json')){$browserReport.result=Get-Content ($browserRoot+'\result.json') -Raw|ConvertFrom-Json}
    if($browserRun.ExitCode -ne 0){throw ([IO.File]::ReadAllText('C:\CitaLab\browser-tun.err'))}
    $browserReport.success=$true
} catch {$browserReport.error=$_.Exception.Message}
finally {
    if($browserRun){$browserRun.Dispose()}
    if($browserStarted){
        $browserRemaining=@(Get-NetRoute -DestinationPrefix '203.0.113.231/32' -ErrorAction SilentlyContinue|Where-Object {$_.InterfaceAlias -eq 'Cita-WebLab'})
        $browserReport.gracefulScopedRouteCleanup=($browserRemaining.Count -eq 0)
        foreach($browserRoute in $browserRemaining){Remove-NetRoute -InterfaceIndex $browserRoute.InterfaceIndex -DestinationPrefix '203.0.113.231/32' -NextHop $browserRoute.NextHop -Confirm:$false}
        $browserAfterNetwork=Get-BrowserNetwork;$browserReport.defaultRoutesUnchanged=($browserNetwork.routes -eq $browserAfterNetwork.routes);$browserReport.dnsUnchanged=($browserNetwork.dns -eq $browserAfterNetwork.dns)
        $browserAfter=Read-BrowserPilot;$browserReport.pilotPreserved=($browserAfter.running -and $browserAfter.id -eq $browserBefore.id -and $browserAfter.bridgeGeneration -eq $browserBefore.bridgeGeneration -and $browserAfter.routerPid -eq $browserBefore.routerPid -and $browserAfter.bridgePid -eq $browserBefore.bridgePid)
        if(-not $browserReport.gracefulScopedRouteCleanup -or -not $browserReport.defaultRoutesUnchanged -or -not $browserReport.dnsUnchanged -or -not $browserReport.pilotPreserved){$browserReport.success=$false;$browserReport.cleanupMismatch=$true}
    }
}
$browserReport.finishedUtc=[datetime]::UtcNow.ToString('o')
$browserReport|ConvertTo-Json -Depth 12|Set-Content 'C:\CitaLab\browser-tun-result.json' -Encoding UTF8
