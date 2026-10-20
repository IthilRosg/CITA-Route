$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'FileStorage.ps1')
. (Join-Path $PSScriptRoot 'WireSockCli.ps1')
. (Join-Path $PSScriptRoot 'WireSockRecovery.ps1')
. (Join-Path $PSScriptRoot 'SessionRecovery.ps1')
function Assert($Value,[string]$Message){if(-not $Value){throw $Message}}
$root=Join-Path $PSScriptRoot ('runtime\liveness-tests-'+[guid]::NewGuid().ToString('N'));$runtime=Join-Path $root 'runtime'
New-Item -ItemType Directory -Path $runtime,(Join-Path $root 'logs') -Force | Out-Null
$expected=Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe'
$script:alive=$true;$script:reuse=$false;$script:starts=0;$script:helperCalls=0;$script:helperCode='BridgePaused';$script:changeSession=$false;$script:persisted=$true
$script:started=[DateTime]::UtcNow.AddMinutes(-1)
function Test-ComponentLiveness {$true}
function Get-Process {param($Id,$ErrorAction) if($script:alive){[pscustomobject]@{Id=$Id;Path=$expected;StartTime=$(if($script:reuse){$script:started.AddSeconds(1)}else{$script:started})}}}
function Start-PolicyController {$script:starts++;$script:alive=$true}
function Read-HelperOutput($Image,$Arguments){
    Assert ($Image -eq $expected -and $Arguments.Contains(('"'+$root+'"'))) 'Helper received the wrong executable or quoted root.'
    $script:helperCalls++
    if($script:helperCode -eq 'PolicyStopped'){$script:alive=$false}
    if($script:changeSession){Write-JsonFile (Join-Path $runtime 'session.json') @{id='replacement';running=$true}}
    return ('{"Code":"'+$script:helperCode+'","Stopped":true,"HoldPersisted":'+$script:persisted.ToString().ToLowerInvariant()+'}')
}
function Save-Pulse([int]$Ago=0) {
    $now=[DateTime]::UtcNow.AddSeconds(-$Ago)
    Write-JsonFile (Join-Path $runtime 'policy-heartbeat.json') @{Schema=1;InstanceId=[guid]::NewGuid().ToString('N');SessionId='session';Pid=123;StartedUtc=$script:started.ToString('o');Phase='Running';ProgressUtc=$now.ToString('o');DeadlineUtc=$now.AddSeconds(15).ToString('o');Sequence=1}
}
try {
    $sessionPath=Join-Path $runtime 'session.json'
    Write-JsonFile $sessionPath @{id='session';running=$true;bridgePid=456;bridgeStartedUtc=$script:started.ToString('o');bridgeGeneration=1}
    Assert ((Get-PolicyObservation).state -eq 'Missing') 'A missing worker was not detected.'
    Write-JsonFile (Join-Path $runtime 'policy-process.json') @{Pid=123;Path=$expected;StartedUtc=$script:started.ToString('o');SessionId='session'}
    Assert ((Get-PolicyObservation).state -eq 'Stale') 'A live PID without progress survived its startup grace.'
    Save-Pulse
    Assert ((Get-PolicyObservation).state -eq 'Healthy' -and (Test-PolicyProcess)) 'A fresh identity-matched pulse was rejected.'
    Assert (-not(Repair-PolicyController 'session') -and $script:helperCalls -eq 0) 'A healthy worker was restarted.'
    $script:reuse=$true
    Assert ((Get-PolicyObservation).state -eq 'Unknown') 'A reused PID inherited an old pulse.'
    $script:reuse=$false
    $pulse=Read-JsonFile (Join-Path $runtime 'policy-heartbeat.json');$owner=Read-JsonFile (Join-Path $runtime 'policy-process.json')
    Assert ((Get-PolicyPulseState $pulse $owner 'other' ([DateTime]::UtcNow)) -eq 'Unknown') 'Another session inherited progress.'
    $pulse.DeadlineUtc=[DateTime]::UtcNow.AddDays(1).ToString('o')
    Assert ((Get-PolicyPulseState $pulse $owner 'session' ([DateTime]::UtcNow)) -eq 'Unknown') 'Unbounded deadlines were accepted.'
    Save-Pulse 20
    Assert ((Get-PolicyObservation).state -eq 'Stale') 'A blocked worker was considered healthy.'
    $script:helperCode='ProgressCurrent'
    Assert (-not(Repair-PolicyController 'session') -and $script:starts -eq 0) 'Fresh progress at the stop check was ignored.'
    $script:helperCode='PolicyStopped'
    Assert ((Repair-PolicyController 'session') -and $script:starts -eq 1) 'A stopped stale worker was not replaced.'
    $script:changeSession=$true
    Assert ((Repair-PolicyController 'session') -and $script:starts -eq 1) 'A late repair started a worker in a newer session.'
    $script:changeSession=$false
    Write-JsonFile $sessionPath @{id='session';running=$true;bridgePid=456;bridgeStartedUtc=$script:started.ToString('o');bridgeGeneration=1}
    [IO.File]::WriteAllText((Join-Path $runtime 'stop.request'),'stop');$before=$script:helperCalls
    Assert (-not(Repair-PolicyController 'session') -and $script:helperCalls -eq $before) 'Stop lost priority over policy repair.'
    Remove-Item -LiteralPath (Join-Path $runtime 'stop.request')
    $session=Read-JsonFile $sessionPath;$script:helperCode='BridgePaused';$before=$script:helperCalls
    Invoke-EmergencyBridgePause $session;Invoke-EmergencyBridgePause $session
    Assert ($script:helperCalls -eq $before+1) 'A confirmed bridge stop caused repeated helper launches.'
    Invoke-EmergencyBridgePause $session -PersistHold
    Assert ($script:helperCalls -eq $before+2) 'A later exit mismatch failed to persist its hold.'
    $session.bridgeGeneration=2;Invoke-EmergencyBridgePause $session
    Assert ($script:helperCalls -eq $before+3) 'A new bridge generation inherited an old pause proof.'
    Write-JsonFile (Join-Path $runtime 'bridge-safety.json') @{Schema=1;SessionId='other';Hold=$true}
    Assert (-not(Test-SessionVpnHold $session)) 'Another session inherited a persistent hold.'
    Write-JsonFile (Join-Path $runtime 'bridge-safety.json') @{Schema=1;SessionId='session';Hold=$true}
    Assert (Test-SessionVpnHold $session) 'A current independent hold was ignored.'
    [IO.File]::WriteAllText((Join-Path $runtime 'bridge-safety.json'),'{')
    Assert (Test-SessionVpnHold $session) 'Unreadable safety state silently allowed VPN resume.'
    Remove-Item -LiteralPath (Join-Path $runtime 'bridge-safety.json')
    $script:persisted=$false;Invoke-EmergencyBridgePause $session -PersistHold
    Assert (Test-SessionVpnHold $session) 'Failed persistence allowed this watcher to resume an unsafe session.'
    Write-Output 'PASS: Windows PowerShell progress/identity/deadlines, bounded repair, fresh-progress recheck, session/Stop priority, pause deduplication and persistent safety holds. No live network changes.'
}finally{
    $prefix=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runtime\liveness-tests-'))
    if(-not [IO.Path]::GetFullPath($root).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture cleanup.'}
    Remove-Item -LiteralPath $root -Recurse -Force
}
