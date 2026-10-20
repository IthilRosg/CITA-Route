param([switch]$Engine)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'FileStorage.ps1')
. (Join-Path $PSScriptRoot 'WireSockCli.ps1')
. (Join-Path $PSScriptRoot 'WireSockRecovery.ps1')
. (Join-Path $PSScriptRoot 'SessionRecovery.ps1')
function Assert($Value,[string]$Message){if(-not $Value){throw $Message}}
$fixture=Join-Path $PSScriptRoot ('runtime\component-tests-'+[guid]::NewGuid().ToString('N'))
$root=$fixture;$runtime=Join-Path $root 'runtime';$smartProfile='SmartRouting-AWG2'
New-Item -ItemType Directory -Path $runtime,(Join-Path $root 'logs'),(Join-Path $runtime 'rules') -Force | Out-Null
$routerExe=Join-Path $runtime 'smart-router.exe';$bridgeExe=Join-Path $runtime 'vpn-bridge.exe'
try {
    foreach($case in @(
        @('Not connected','Disconnected'),@('NotConnected','Disconnected'),@('Disconnected','Disconnected'),
        @('Connected to 203.0.113.1 using profile SmartRouting-AWG2','Connected'),
        @('Connected to 203.0.113.1 using profile SmartRouting-AWG2-copy','Foreign'),
        @('Connected to 203.0.113.1 using profile MAIN','Foreign'),
        @('Not connected to server using profile SmartRouting-AWG2','Disconnected'),
        @('Connecting using profile SmartRouting-AWG2','Transitioning'),
        @('Disconnected using profile MAIN','Foreign'),@('garbled output','Unknown'),@('','Unknown')
    )){Assert ((Get-WireSockState $case[0] $smartProfile) -eq $case[1]) ('Wrong WireSock status class: '+$case[1])}
    $localized=-join ([char[]]@(0x041f,0x043e,0x0434,0x043a,0x043b,0x044e,0x0447,0x0435,0x043d,0x043e,0x0020,0x043a,0x0020,0x0441,0x0435,0x0440,0x0432,0x0435,0x0440,0x0443))
    $profileWord=-join ([char[]]@(0x043f,0x0440,0x043e,0x0444,0x0438,0x043b,0x044f))
    Assert ((Get-WireSockState ($localized+' 203.0.113.1 '+$profileWord+' '+$smartProfile) $smartProfile) -eq 'Connected') 'Localized status failed.'
    foreach($newline in @("`n","`r`n")){
        foreach($line in @('Connected to server using profile SmartRouting-AWG2',($localized+' '+$profileWord+' '+$smartProfile))){
            $realStatus=$line+$newline+'External address: 203.0.113.1, Test'+$newline
            Assert ((Get-WireSockState $realStatus $smartProfile) -eq 'Connected') 'Windows multiline CLI status was classified as a foreign profile.'
            Assert (Test-WireSockManagedProfile $realStatus $smartProfile) 'Windows multiline CLI identity was lost.'
            Assert ((Get-WireSockState ($realStatus.Replace($smartProfile,$smartProfile+'-copy')) $smartProfile) -eq 'Foreign') 'CRLF weakened exact identity.'
            Assert (-not(Test-WireSockManagedProfile ($realStatus+'Connected to server using profile Other'+$newline) $smartProfile)) 'Conflicting multiline profile identities were accepted.'
        }
    }
    $ruEstablishing=-join([char[]]@(0x0423,0x0441,0x0442,0x0430,0x043d,0x0430,0x0432,0x043b,0x0438,0x0432,0x0430,0x0435,0x0442,0x0441,0x044f,0x0020,0x0441,0x043e,0x0435,0x0434,0x0438,0x043d,0x0435,0x043d,0x0438,0x0435))
    $ruDisconnected=-join([char[]]@(0x041e,0x0442,0x043a,0x043b,0x044e,0x0447,0x0435,0x043d,0x043e))
    Assert ((Get-WireSockState ($ruDisconnected+"`r`n") $smartProfile) -eq 'Disconnected') 'Localized disconnected status was not recognized.'
    foreach($prefix in @('Establishing connection using profile ',($ruEstablishing+' '+$profileWord+' '))){
        Assert ((Get-WireSockState ($prefix+$smartProfile+"`r`nExternal address: 203.0.113.1`r`n") $smartProfile) -eq 'Transitioning') 'The real establishing status was not recognized.'
    }
    foreach($state in @('Unknown','Foreign','Transitioning','Connected')){Assert ((Get-WatchRecoveryAction $true 0 $state 10) -eq 'Observe') 'An ambiguous status would restart the TUN.'}
    Assert ((Get-WatchRecoveryAction $true 0 'Disconnected' 2) -eq 'Observe') 'Single disconnection should be rechecked.'
    Assert ((Get-WatchRecoveryAction $true 0 'Disconnected' 3) -eq 'RepairVpn') 'Confirmed disconnection should repair only VPN.'
    Assert ((Get-WatchRecoveryAction $false 0 'Connected' 0) -eq 'Reconnect') 'A crashed router requires full recovery.'
    Assert ((Get-WatchRecoveryAction $true 2 'Connected' 0) -eq 'Reconnect') 'Confirmed network change must rebind.'

    $clock=[Diagnostics.Stopwatch]::StartNew();$timedOut=$false
    try{Read-HelperOutput (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') '-NoProfile -Command "Start-Sleep -Seconds 30"' | Out-Null}catch{$timedOut=$true}
    Assert ($timedOut -and $clock.Elapsed.TotalSeconds -lt 7) 'A stuck status helper blocked beyond its budget.'
    foreach($encoding in @('utf8','oem866')){
        $first=$localized+' '+$profileWord+' '+$smartProfile
        $script="[Console]::OutputEncoding="+$(if($encoding -eq 'utf8'){'(New-Object Text.UTF8Encoding($false))'}else{'[Text.Encoding]::GetEncoding(866)'})+";[Console]::WriteLine('"+$first+"');[Console]::WriteLine('External address: 203.0.113.1, Test')"
        $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
        $captured=Read-HelperOutput (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') ('-NoProfile -EncodedCommand '+$encoded)
        Assert ($captured.Contains("`r`n") -and (Get-WireSockState $captured $smartProfile) -eq 'Connected') 'Real redirected Windows stdout lost CRLF profile identity or encoding.'
    }
    $script:identityStatus='';$script:identityDisconnects=0
    function Read-WireSockStatus {$script:identityStatus}
    function Invoke-WireSock($Arguments){Assert ($Arguments[0] -eq 'disconnect') 'Unexpected identity-test CLI mutation';$script:identityDisconnects++;$script:identityStatus='Not connected'}
    foreach($identity in @('Connected to server from profile SmartRouting-AWG2-copy','Connected to server from profile Other profile SmartRouting-AWG2',"Connected to server from profile SmartRouting-AWG2`nConnected to server from profile Other",'Connecting from profile Other','Unknown')){
        $script:identityStatus=$identity;$rejected=$false;try{Disconnect-ManagedWireSock}catch{$rejected=$true}
        Assert ($rejected -eq ($identity -eq 'Unknown')) 'Unknown status must require attention; foreign identity remains untouched.'
        Assert ($script:identityDisconnects -eq 0) 'Stop/cleanup disconnected a foreign or unverified profile.'
    }
    $script:identityStatus='Connecting from profile SmartRouting-AWG2';Disconnect-ManagedWireSock
    Assert ($script:identityDisconnects -eq 1) 'Cancellation left the owned handshake running.'
    $script:identityStatus="Connected to server using profile SmartRouting-AWG2`r`nExternal address: 203.0.113.1`r`n";Disconnect-ManagedWireSock
    Assert ($script:identityDisconnects -eq 2) 'Cleanup left the owned CRLF connection running.'

    $script:bridgeRunning=$true;$script:state='Unknown';$script:connects=0;$script:stops=0;$script:starts=0;$script:leases=0
    $script:coordinatorBusy=$false
    function Invoke-AgentCoordinator($Command){if($Command -eq 'begin-operation'){if($script:coordinatorBusy){throw 'Fixture coordinator busy'};$script:leases++}else{$script:leases--}}
    function Get-OurProcesses {
        [pscustomobject]@{Id=10;Path=$routerExe}
        if($script:bridgeRunning){[pscustomobject]@{Id=20;Path=$bridgeExe}}
    }
    function Get-WireSockObservation {[pscustomobject]@{state=$script:state}}
    function Get-SessionNetworkSignature {'network'}
    function Connect-WireSock {$script:connects++;$script:state='Connected'}
    function Stop-Process {param([Parameter(ValueFromPipeline=$true)]$InputObject,[switch]$Force) process {Assert ($InputObject.Id -eq 20) 'Main router was stopped.';$script:bridgeRunning=$false;$script:stops++}}
    function Start-Helper($Exe,$Config){Assert ($Exe -eq $bridgeExe) 'Main router was restarted.';$script:bridgeRunning=$true;$script:starts++;[pscustomobject]@{Id=20}}
    $path=Join-Path $runtime 'session.json'
    Write-JsonFile $path @{id='session';running=$true;routerPid=10;bridgePid=20;bridgeReady=$true;bridgeGeneration=1;networkSignature='network';mode='ProxyOnly'}
    Update-WireSockBridge $true 'session'
    Assert (-not $script:bridgeRunning -and $script:connects -eq 0 -and $script:leases -eq 0) 'Unknown must pause the bridge without reconnect or leaked lease.'
    $script:state='Connected';Update-WireSockBridge $false 'session'
    Assert ($script:bridgeRunning -and $script:starts -eq 1) 'Fresh state under the lease must supersede a stale sample.'
    $state=Read-JsonFile $path
    Assert ($state.id -eq 'session' -and $state.routerPid -eq 10 -and $state.bridgeGeneration -eq 3) 'Component repair replaced the session/router identity.'
    Repair-SessionVpn 'session'
    Assert ($script:stops -eq 1 -and $script:connects -eq 0) 'Already recovered VPN was interrupted again.'
    $script:state='Foreign';Repair-SessionVpn 'session'
    Assert ($script:connects -eq 0) 'An unrelated profile was overridden.'
    $script:state='Disconnected';Repair-SessionVpn 'old-session'
    Assert ($script:connects -eq 0) 'Stale generation connected WireSock.'
    Repair-SessionVpn 'session'
    Assert ($script:connects -eq 1 -and $script:bridgeRunning -and $script:starts -eq 2 -and (Read-JsonFile $path).routerPid -eq 10) 'VPN-only repair failed or restarted direct routing.'
    [IO.File]::WriteAllText((Join-Path $runtime 'stop.request'),'stop')
    $script:state='Disconnected';Repair-SessionVpn 'session';Update-WireSockBridge $false 'session'
    Assert ($script:connects -eq 1 -and $script:bridgeRunning) 'Stop did not suppress late recovery callbacks.'
    Remove-Item -LiteralPath (Join-Path $runtime 'stop.request')

    $guarded=Read-JsonFile $path
    $script:state='Connected'
    Set-UnsafeVpnHold @{sessionId='old-session';bridgeGeneration=$guarded.bridgeGeneration;healthy=$false;code='EgressMismatch'}
    Assert $script:bridgeRunning 'Old-generation health paused a newer bridge.'
    Set-UnsafeVpnHold @{sessionId='session';bridgeGeneration=$guarded.bridgeGeneration;healthy=$false;code='ControlHttpsUnavailable'}
    Assert $script:bridgeRunning 'Control-site failure paused a working bridge.'
    Set-UnsafeVpnHold @{sessionId='session';bridgeGeneration=$guarded.bridgeGeneration;healthy=$false;code='EgressMismatch'}
    Assert (-not $script:bridgeRunning -and (Read-JsonFile $path).vpnHoldReason -eq 'EgressMismatch') 'Mismatched egress was not paused.'
    Update-WireSockBridge $true 'session'
    Assert (-not $script:bridgeRunning) 'Automatic bridge retry bypassed the held VPN exit.'

    # Exercise the actual Watch loop with observations/process launches substituted.
    # This detects integration errors beyond testing the recovery decision table.
    [IO.File]::WriteAllText((Join-Path $runtime 'rules\russia_inside.srs'),'fixture')
    $script:tick=0;$script:launches=0;$script:scenario='Unknown'
    function Test-PolicyProcess {return $script:scenario -notin @('HungPolicy','UnknownPolicy') -or $script:policyRecovered}
    function Get-PolicyObservation {return @{state=$(if($script:scenario -eq 'UnknownPolicy'){'Unknown'}elseif($script:policyRecovered){'Healthy'}else{'Stale'})}}
    function Repair-PolicyController($ExpectedId){Assert ($ExpectedId -eq 'session') 'Policy repair used a stale session';$script:policyRecovered=$true;$script:policyRepairs++;return $true}
    function Invoke-EmergencyBridgePause($Session,[switch]$PersistHold){
        if($script:scenario -eq 'RepairBusy'){
            if($script:coordinatorBusy -and $script:bridgeRunning){$script:busyPauses++}
            $script:bridgeRunning=$false
        }else{Update-WireSockBridge $false $Session.id}
    }
    function Start-Sleep {
        param($Seconds,$Milliseconds)
        $script:tick++
        $script:state=switch($script:scenario){
            'Unknown' {if($script:tick -eq 1){'Unknown'}else{'Connected'}}
            'Disconnected' {if($script:tick -le 3){'Disconnected'}else{'Connected'}}
            'Foreign' {'Foreign'}
            'BridgeBusy' {'Connected'}
            'HungPolicy' {'Connected'}
            'UnknownPolicy' {'Connected'}
            'RepairBusy' {if($script:tick -le 3){'Disconnected'}else{'Unknown'}}
            default {'Disconnected'}
        }
        if($script:tick -ge 8 -or $script:scenario -eq 'Stop'){[IO.File]::WriteAllText((Join-Path $runtime 'stop.request'),'stop')}
    }
    function Start-Process {
        param($FilePath,$WindowStyle,[switch]$PassThru,$WorkingDirectory,$ArgumentList,$RedirectStandardOutput,$RedirectStandardError)
        $position=[Array]::IndexOf($ArgumentList,'-Action');$operation=$ArgumentList[$position+1]
        Assert ($operation -eq 'RepairVpn') 'Watch requested a full reconnect for a VPN-only problem.'
        $script:launches++
        if($script:scenario -eq 'RepairBusy'){
            $script:coordinatorBusy=$true;$script:bridgeRunning=$true
            $process=[pscustomobject]@{HasExited=$false;ExitCode=0};$process|Add-Member ScriptMethod Dispose {};return $process
        }
        $code=0;if($script:scenario -eq 'Busy'){$code=75}else{Repair-SessionVpn 'session'}
        $process=[pscustomobject]@{HasExited=$true;ExitCode=$code};$process|Add-Member ScriptMethod Dispose {};return $process
    }
    foreach($scenario in @('Unknown','Disconnected','Foreign','Limit','Stop','Busy','BridgeBusy','HungPolicy','UnknownPolicy','RepairBusy')){
        $script:scenario=$scenario;$script:tick=0;$script:bridgeRunning=$true
        $script:policyRecovered=$false;$script:policyRepairs=0;$script:busyPauses=0
        $script:coordinatorBusy=$scenario -eq 'BridgeBusy'
        if($script:coordinatorBusy){$script:bridgeRunning=$false}
        $beforeLaunches=$script:launches;$beforeConnects=$script:connects
        Write-JsonFile $path @{id='session';running=$true;routerPid=10;bridgePid=20;bridgeReady=$true;bridgeGeneration=1;networkSignature='network';mode='ProxyOnly'}
        if($scenario -eq 'BridgeBusy'){Write-JsonFile $path @{id='session';running=$true;routerPid=10;bridgePid=20;bridgeReady=$false;bridgeGeneration=1;networkSignature='network';mode='ProxyOnly'}}
        Remove-Item -LiteralPath (Join-Path $runtime 'stop.request'),(Join-Path $runtime 'component-recovery.json') -ErrorAction SilentlyContinue
        if($scenario -eq 'Limit'){Write-JsonFile (Join-Path $runtime 'component-recovery.json') @{sessionId='session';RepairVpn=3}}
        Watch-Session 'fixture.ps1'
        $expectedLaunches=if($scenario -in @('Disconnected','Busy','RepairBusy')){1}else{0}
        $expectedConnects=if($scenario -eq 'Disconnected'){1}else{0}
        Assert ($script:launches-$beforeLaunches -eq $expectedLaunches -and $script:connects-$beforeConnects -eq $expectedConnects) ('Watch recovery scope/budget failed: '+$scenario)
        Assert ((Read-JsonFile $path).routerPid -eq 10 -and $script:leases -eq 0) 'Watch replaced the router or leaked coordinator ownership.'
        if($scenario -eq 'Limit'){Assert ((Read-JsonFile (Join-Path $runtime 'watch-state.json')).phase -eq 'NeedsAttention') 'Persisted retry limit was ignored.'}
        if($scenario -eq 'Busy'){Assert ((Read-JsonFile (Join-Path $runtime 'component-recovery.json')).RepairVpn -eq 0) 'Coordinator rejection consumed a real VPN attempt.'}
        if($scenario -eq 'BridgeBusy'){Assert (-not(Test-Path -LiteralPath (Join-Path $runtime 'component-recovery.json'))) 'Coordinator rejection consumed a bridge attempt.'}
        if($scenario -eq 'HungPolicy'){Assert ($script:policyRepairs -eq 1 -and (Read-JsonFile (Join-Path $runtime 'component-recovery.json')).Policy -eq 1) 'Watch did not repair the confirmed stale policy exactly once.'}
        if($scenario -eq 'UnknownPolicy'){Assert ($script:policyRepairs -eq 0 -and (Read-JsonFile (Join-Path $runtime 'watch-state.json')).phase -eq 'NeedsAttention') 'Unverifiable policy metadata caused a blind process restart.'}
        if($scenario -eq 'RepairBusy'){Assert ($script:busyPauses -gt 0 -and -not $script:bridgeRunning) 'A running component operation suppressed isolated bridge safety.'}
    }
    $script:coordinatorBusy=$false
    Remove-Item -LiteralPath (Join-Path $runtime 'stop.request')
    Write-JsonFile $path @{id='session';running=$true;routerPid=10;bridgePid=20;bridgeReady=$true;bridgeGeneration=3}
    $script:tick=0
    $owner=[IO.File]::Open((Join-Path $runtime 'watch-control.lock'),'OpenOrCreate','ReadWrite','None')
    try{Watch-Session 'fixture.ps1'}finally{$owner.Dispose()}
    Assert ($script:tick -eq 0) 'A duplicate watcher entered the recovery loop.'

    $session=Read-JsonFile $path
    $session|Add-Member -NotePropertyName bridgeStartedUtc -NotePropertyValue ([DateTime]::UtcNow.AddDays(-1).ToString('o'))
    $session|Add-Member -NotePropertyName routerStartedUtc -NotePropertyValue 'invalid'
    Assert (@(Get-SessionBridge $session).Count -eq 0 -and @(Get-SessionRouter $session).Count -eq 0) 'Invalid process identity was adopted.'

    $script:disposed=0;$script:killed=0
    function FakeProbe($Generation,[bool]$Exited=$true){
        $process=[pscustomobject]@{HasExited=$Exited;ExitCode=0}
        $process|Add-Member ScriptMethod Dispose {$script:disposed++}
        $process|Add-Member ScriptMethod Kill {$script:killed++;$this.HasExited=$true}
        $process|Add-Member ScriptMethod WaitForExit {param($Timeout) return $true}
        @{process=$process;sessionId='session';bridgeGeneration=$Generation;startedUtc=[DateTime]::UtcNow.AddSeconds(-21);output=[Threading.Tasks.Task]::FromResult('{"healthy":true}');errorOutput=[Threading.Tasks.Task]::FromResult('')}
    }
    $session=Read-JsonFile $path
    Assert ((Complete-SessionHealthProbe (FakeProbe 1) $session).discarded) 'Old bridge HTTPS success was accepted.'
    Assert ((Complete-SessionHealthProbe (FakeProbe $session.bridgeGeneration) $session).healthy) 'Current HTTPS success was discarded.'
    $timeout=Complete-SessionHealthProbe (FakeProbe $session.bridgeGeneration $false) $session
    Assert (-not $timeout.healthy -and $script:killed -eq 1 -and $script:disposed -eq 3) 'A stuck health process was not cancelled/disposed.'
    1..80|ForEach-Object {Write-WatchState 'session' 'Suspect' ('test-'+$_) 'Unknown'}
    Assert ((Read-JsonFile (Join-Path $root 'data\recovery-events.json')).events.Count -eq 64) 'Recovery events grew without a bound.'
    Write-Output 'PASS: bounded status, exact/localized profile identity, tri-state recovery, coordinator lease, stale sample/generation, Stop priority, VPN-only repair, health cancellation and bounded events. No live network changes.'
}finally{
    $prefix=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runtime\component-tests-'))
    if(-not [IO.Path]::GetFullPath($fixture).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe cleanup.'}
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
if($Engine){
    & (Join-Path $PSScriptRoot 'runtime\agent-build\RoutePilot.Agent.exe') --component-integration $PSScriptRoot
    if($LASTEXITCODE -ne 0){throw 'Real-engine component continuity check failed.'}
}
