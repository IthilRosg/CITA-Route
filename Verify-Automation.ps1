param([switch]$Elevated)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot; $runtime=Join-Path $root 'runtime'
$admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if(-not $admin){
    $command="& '"+$PSCommandPath.Replace("'","''")+"' -Elevated"
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $child=Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -PassThru -Wait -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded)
    exit $child.ExitCode
}
. (Join-Path $root 'FileStorage.ps1')
. (Join-Path $root 'SessionRecovery.ps1')
. (Join-Path $root 'AgentLease.ps1')
Assert-NoAgentRun
$folder=Join-Path $runtime ('automation-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder | Out-Null
$report=@{startedUtc=[DateTime]::UtcNow.ToString('o');success=$false;stages=@();folder=$folder}
$modePath=Join-Path $root 'data\routing-policy.json'; $modeBefore=[IO.File]::ReadAllBytes($modePath)
$cachePath=Join-Path $root 'data\policy-cache.json';$cacheBefore=$null;if(Test-Path -LiteralPath $cachePath){$cacheBefore=[IO.File]::ReadAllBytes($cachePath)}
$route=$null;$engineAttempted=$false
function Stage([string]$Name,[scriptblock]$Action){
    $script:report.currentStage=$Name
    Write-JsonFile (Join-Path $folder 'report.json') $script:report
    $timer=[Diagnostics.Stopwatch]::StartNew()
    & $Action
    $script:report.stages+=@{name=$Name;success=$true;milliseconds=$timer.ElapsedMilliseconds}
    Write-JsonFile (Join-Path $folder 'report.json') $script:report
}
function Run-Action([string]$Action,[string]$Expected=''){
    $output=Join-Path $folder ($Action+'-output.log')
    $command="& '"+(Join-Path $root 'SmartRouting.ps1').Replace("'","''")+"' -Action '"+$Action+"'"
    if($Expected){$command+=" -ExpectedSessionId '"+$Expected.Replace("'","''")+"'"}
    $command+=" *> '"+$output.Replace("'","''")+"'"
    $info=New-Object Diagnostics.ProcessStartInfo('powershell.exe')
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $info.Arguments='-NoProfile -ExecutionPolicy Bypass -EncodedCommand '+[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $child=[Diagnostics.Process]::Start($info)
    try {
        if(-not $child.WaitForExit(80000)){$child.Kill();throw ('Action timed out: '+$Action)}
        if($child.ExitCode -ne 0){throw ('Action failed: '+$Action+'; '+[IO.File]::ReadAllText($output))}
    }finally{$child.Dispose()}
}
function Wait-Until([scriptblock]$Check,[int]$Seconds=45){
    $wait=[Diagnostics.Stopwatch]::StartNew();$last=''
    do{try{if(& $Check){return}}catch{$last=$_.Exception.Message};Start-Sleep -Milliseconds 500}while($wait.Elapsed.TotalSeconds -lt $Seconds)
    throw ('Condition timed out: '+$last)
}
function Session { Read-JsonFile (Join-Path $runtime 'session.json') }
function Policy { Read-JsonFile (Join-Path $runtime 'policy-process.json') }
function Kill-Owned([int]$ProcessId,[string]$ExpectedPath){
    $process=Get-Process -Id $ProcessId
    if($process.Path -ne $ExpectedPath){throw 'The fault injection target belongs to another process.'}
    Stop-Process -Id $ProcessId -Force
}
function Invoke-CurlProbe([string]$Domain,[string]$Proxy='http://127.0.0.1:20809'){
    $status=& curl.exe --silent --show-error --max-time 10 --proxy $Proxy ('https://'+$Domain+'/') -o NUL --write-out '%{http_code}' 2> (Join-Path $folder 'curl-error.log')
    if($LASTEXITCODE -ne 0){throw ('HTTPS failed: '+$Domain)}
    return $status
}
function Vpn-Rule {
    $rules=Read-JsonFile (Join-Path $runtime 'rules\auto-vpn.json')
    return [bool]@($rules.rules | Where-Object {$_.domain -contains 'discord.com' -and $_.process_path -contains (Join-Path $env:windir 'System32\curl.exe')}).Count
}
try {
    if(Get-Process -Name smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'Stop RoutePilot before the automation test.'}
    if((Get-NetAdapter -Name VPNUS -ErrorAction SilentlyContinue).Status -ne 'Up'){throw 'This isolated proxy test requires VPNUS to remain connected.'}
    $wireStatus=& 'C:\Program Files\WireSock Secure Connect\command-line\wiresock-connect-cli.exe' status
    if(($wireStatus -join ' ') -notmatch '^(\u041d\u0435\u0442 \u043f\u043e\u0434\u043a\u043b\u044e\u0447\u0435\u043d\u0438\u044f|Not connected|No connection)\s*$'){throw 'WireSock is already in use.'}
    $settings=Read-JsonFile 'C:\Program Files\RoutePilot\Agent\agent-config.json'
    $prefix=$settings.EndpointIp+'/32'
    if(Get-NetRoute -DestinationPrefix $prefix -ErrorAction SilentlyContinue){throw 'An existing endpoint route must not be replaced.'}
    $physical=Get-NetIPConfiguration | Where-Object {$_.NetAdapter.HardwareInterface -and $_.NetAdapter.Status -eq 'Up' -and $_.IPv4DefaultGateway} | Sort-Object {$_.NetIPv4Interface.InterfaceMetric} | Select-Object -First 1
    $route=@{Prefix=$prefix;Index=$physical.InterfaceIndex;Gateway=$physical.IPv4DefaultGateway.NextHop;Metric=29000+(Get-Random -Maximum 500)}
    New-NetRoute -DestinationPrefix $route.Prefix -InterfaceIndex $route.Index -NextHop $route.Gateway -RouteMetric $route.Metric -PolicyStore ActiveStore | Out-Null
    Remove-Item -LiteralPath $cachePath -Force -ErrorAction SilentlyContinue
    Write-JsonFile $modePath @{Mode='Auto';AllowListedDpi=$true}
    Stage 'start-production-proxy' { $script:engineAttempted=$true;Run-Action ProxyOnly;Wait-Until {(Session).running -and (Test-PolicyProcess)} 20 }
    Stage 'AWG2-egress-and-HTTPS' {
        if((Invoke-CurlProbe 'yandex.ru') -notin @('200','301','302')){throw 'Direct Yandex request failed.'}
        if((Invoke-CurlProbe 'www.youtube.com' 'http://127.0.0.1:20808') -ne '200'){throw 'VPN YouTube request failed.'}
        $health=& 'C:\Program Files\RoutePilot\Agent\RoutePilot.Agent.exe' --session-health | ConvertFrom-Json
        if(-not $health.healthy){throw $health.message}
    }
    Stage 'learn-real-blocked-domain' {
        for($i=0;$i -lt 2;$i++){try{[void](Invoke-CurlProbe 'discord.com')}catch{}}
        Wait-Until { Vpn-Rule } 50
        if((Invoke-CurlProbe 'discord.com') -ne '200'){throw 'Adaptive VPN did not open Discord.'}
    }
    Stage 'policy-crash-cache-reuse' {
        $old=(Policy).Pid;Kill-Owned $old 'C:\Program Files\RoutePilot\Agent\RoutePilot.Agent.exe'
        Wait-Until {(Policy).Pid -ne $old -and (Test-PolicyProcess) -and (Vpn-Rule)} 25
        if((Invoke-CurlProbe 'discord.com') -ne '200'){throw 'Policy restart lost its learned route.'}
    }
    Stage 'bridge-crash-recovery' {
        $before=Session;Kill-Owned $before.bridgePid 'C:\Program Files\RoutePilot\Agent\vpn-bridge.exe'
        Wait-Until {$now=Session;$now.id -eq $before.id -and $now.bridgePid -ne $before.bridgePid -and $now.bridgeReady} 30
        if((Invoke-CurlProbe 'discord.com') -ne '200'){throw 'Bridge recovery lost VPN traffic.'}
    }
    Stage 'router-crash-recovery' {
        $before=Session;Kill-Owned $before.routerPid (Join-Path $runtime 'smart-router.exe')
        Wait-Until {$now=Session;$now.id -ne $before.id -and $now.running -and -not $now.recovering -and (Vpn-Rule)} 65
        if((Invoke-CurlProbe 'discord.com') -ne '200'){throw 'Router recovery lost its learned route.'}
    }
    Stage 'network-generation-rebind' {
        $before=Session;$before.networkSignature='simulated-stale-topology';Write-JsonFile (Join-Path $runtime 'session.json') $before
        Wait-Until {$now=Session;$now.id -ne $before.id -and $now.running -and -not $now.recovering} 65
        if((Invoke-CurlProbe 'discord.com') -ne '200'){throw 'Network rebind did not restore HTTPS.'}
    }
    Stage 'WireSock-disconnect-recovery' {
        $before=Session
        & 'C:\Program Files\WireSock Secure Connect\command-line\wiresock-connect-cli.exe' disconnect *> (Join-Path $folder 'disconnect.log')
        Wait-Until {$now=Session;$now.id -ne $before.id -and $now.running -and -not $now.recovering} 65
        if((Invoke-CurlProbe 'discord.com') -ne '200'){throw 'WireSock reconnect lost its learned route.'}
    }
    Stage 'stop-wins-recovery-race' {
        $before=Session;Kill-Owned $before.routerPid (Join-Path $runtime 'smart-router.exe')
        Wait-Until {(Read-JsonFile (Join-Path $runtime 'watch-state.json')).phase -eq 'Reconnecting'} 15
        Run-Action Stop
        Start-Sleep -Seconds 6
        if((Session).running -or (Get-Process smart-router,vpn-bridge -ErrorAction SilentlyContinue)){throw 'A stopped route restarted itself.'}
        Run-Action Reconnect $before.id
        if((Session).running){throw 'A stale recovery restored a stopped session.'}
    }
    $report.success=$true
}catch{
    $report.error=$_.Exception.Message
}finally{
    try { if($engineAttempted){Run-Action Stop} } catch { $report.success=$false;$report.cleanupError=$_.Exception.Message }
    [IO.File]::WriteAllBytes($modePath,$modeBefore)
    if($null -ne $cacheBefore){[IO.File]::WriteAllBytes($cachePath,$cacheBefore)}else{Remove-Item -LiteralPath $cachePath -Force -ErrorAction SilentlyContinue}
    if($route){
        Get-NetRoute -DestinationPrefix $route.Prefix -InterfaceIndex $route.Index -NextHop $route.Gateway -ErrorAction SilentlyContinue | Where-Object RouteMetric -eq $route.Metric | Remove-NetRoute -Confirm:$false
    }
    $report.completedUtc=[DateTime]::UtcNow.ToString('o')
    $report.vpnusUp=(Get-NetAdapter -Name VPNUS -ErrorAction SilentlyContinue).Status -eq 'Up'
    if(-not $report.vpnusUp){$report.success=$false}
    Write-JsonFile (Join-Path $folder 'report.json') $report
    Write-JsonFile (Join-Path $root 'logs\automation-last.json') $report
}
if(-not $report.success){exit 1}
