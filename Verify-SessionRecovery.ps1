$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'FileStorage.ps1')
. (Join-Path $PSScriptRoot 'SessionRecovery.ps1')
$fixture=Join-Path $PSScriptRoot ('runtime\session-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$runtime=$fixture
try {
    if((Get-WatchRecoveryAction $true 0 'Disconnected' 2) -ne 'Observe' -or (Get-WatchRecoveryAction $true 0 'Disconnected' 3) -ne 'RepairVpn'){throw 'An explicit disconnect retains three-poll confirmation.'}
    if((Get-WatchRecoveryAction $true 0 'Connected' 99) -ne 'Observe'){throw 'Connected packet loss must not become a disconnect repair.'}
    Write-JsonFile (Join-Path $runtime 'session.json') @{id='timing';running=$true;bridgeGeneration=4}
    foreach($i in 1..20){Write-RecoveryStageTiming 'timing' 'ConnectWireSock' $i}
    $timings=Read-JsonFile (Join-Path $runtime 'recovery-timing.json')
    if($timings.events.Count -ne 16 -or $timings.events[-1].milliseconds -ne 20){throw 'Recovery timings must remain bounded and preserve the last completed stage.'}
    Write-RecoveryStageTiming 'old-session' 'ResumeBridge' 99
    Write-RecoveryStageTiming 'timing' 'Unsupported' 99
    $timings=Read-JsonFile (Join-Path $runtime 'recovery-timing.json')
    if($timings.events.Count -ne 16 -or $timings.events[-1].milliseconds -ne 20){throw 'Timing cannot attribute old-session or arbitrary stages to current repair.'}
    $gapNow=[datetime]::UtcNow
    if((Test-WatchObservationGap $gapNow $gapNow.AddSeconds(20)) -or -not(Test-WatchObservationGap $gapNow $gapNow.AddSeconds(21)) -or -not(Test-WatchObservationGap $gapNow $gapNow.AddSeconds(-1))){throw 'Observation gaps and clock rollback must invalidate prior samples.'}
    $script:stoppedOldProbe=0
    function Stop-SessionHealthProbe($Probe){$script:stoppedOldProbe++}
    $context=@{epoch='same:1';probe=@{old=$true};failures=3;next=$gapNow.AddSeconds(30)}
    $afterRepair=@{id='same';bridgeGeneration=2;running=$true;bridgeReady=$true}
    if(-not(Reset-SessionHealthContext $context $afterRepair $gapNow) -or $context.epoch -ne 'same:2' -or $context.probe -or $context.failures -ne 0 -or $context.next -ne $gapNow -or $script:stoppedOldProbe -ne 1){throw 'A new bridge must cancel stale HTTPS, reset failures and verify immediately without touching the route.'}
    $context.failures=1;$context.next=$gapNow.AddSeconds(5)
    if((Reset-SessionHealthContext $context $afterRepair $gapNow.AddSeconds(1)) -or $context.failures -ne 1 -or $context.next -ne $gapNow.AddSeconds(5)){throw 'Unchanged bridge generations must retain health backoff instead of busy probing.'}
    $afterRepair.id='new'
    if(-not(Reset-SessionHealthContext $context $afterRepair $gapNow) -or $context.epoch -ne 'new:2'){throw 'Session identity is part of health ownership.'}
    $script:stoppedOldProbe=0
    $oldProbe=@{sessionId='same';bridgeGeneration=1;startedUtc=$gapNow.AddSeconds(-21)}
    $live=@{id='same';bridgeGeneration=1;running=$true;bridgeReady=$true}
    if(-not (Complete-SessionHealthProbe $oldProbe $live).discarded -or $script:stoppedOldProbe -ne 1){throw 'Even a completed pre-sleep probe must be discarded.'}
    $healthOutput=New-Object 'Threading.Tasks.TaskCompletionSource[string]';$healthError=New-Object 'Threading.Tasks.TaskCompletionSource[string]'
    $healthOutput.SetResult('{"healthy":false,"code":"ControlHttpsUnavailable","attempts":[{"Provider":"Cloudflare","Stage":"TLS","Outcome":"Failed","Milliseconds":23,"Error":"SecureConnectionError","PrivateKey":"fixture-not-evidence"}]}');$healthError.SetResult('')
    $probe=@{sessionId='same';bridgeGeneration=1;startedUtc=[datetime]::UtcNow;process=@{HasExited=$true;ExitCode=0};output=$healthOutput.Task;errorOutput=$healthError.Task}
    $completed=Complete-SessionHealthProbe $probe $live
    if($completed.healthy -or $completed.code -ne 'ControlHttpsUnavailable' -or $completed.attempts.Count -ne 1 -or $completed.attempts[0].Stage -ne 'TLS' -or $completed.attempts[0].PSObject.Properties.Name -contains 'PrivateKey'){throw 'Health completion must preserve bounded per-control evidence without arbitrary fields.'}
    $legacyOutput=New-Object 'Threading.Tasks.TaskCompletionSource[string]';$legacyOutput.SetResult('{"healthy":true,"code":"Verified"}');$probe.output=$legacyOutput.Task
    $completed=Complete-SessionHealthProbe $probe $live
    if(-not $completed.healthy -or $completed.attempts.Count -ne 0){throw 'An old agent without attempt details must remain compatible.'}
    function Get-NetIPConfiguration {param($ErrorAction) @([pscustomobject]@{NetAdapter=@{HardwareInterface=$true;Status='Disconnected'};IPv4DefaultGateway='fixture'})}
    if(Test-RecoveryNetworkAvailable){throw 'A down physical interface cannot spend reconnect attempts.'}
    function Get-NetIPConfiguration {param($ErrorAction) @([pscustomobject]@{NetAdapter=@{HardwareInterface=$false;Status='Up'};IPv4DefaultGateway='fixture'})}
    if(Test-RecoveryNetworkAvailable){throw 'A virtual VPN interface cannot prove underlay readiness.'}
    function Get-NetIPConfiguration {param($ErrorAction) @([pscustomobject]@{NetAdapter=@{HardwareInterface=$true;Status='Up'};IPv4DefaultGateway='fixture'})}
    if(-not(Test-RecoveryNetworkAvailable)){throw 'A restored physical network must allow bounded recovery.'}
    $now=[DateTime]::UtcNow;$observation=[pscustomobject]@{state='Transitioning';managed=$true}
    $progress=Get-ManagedTransitionProgress $null 'session' $observation $now
    foreach($seconds in @(10,20,30,40,45)){$progress=Get-ManagedTransitionProgress $progress 'session' $observation $now.AddSeconds($seconds)}
    if(-not(Test-ManagedTransitionTimeout $progress 'session' $observation $now.AddSeconds(45))){throw 'A continuously observed owned transition must become eligible after 45 seconds.'}
    if(Test-ManagedTransitionTimeout $progress 'other' $observation $now.AddSeconds(45)){throw 'A stale session cannot authorize transitional cleanup.'}
    if(Test-ManagedTransitionTimeout $progress 'session' $observation $now.AddSeconds(66)){throw 'A stale watcher cannot authorize transitional cleanup.'}
    $reset=Get-ManagedTransitionProgress $progress 'session' $observation $now.AddSeconds(66)
    if($reset.elapsedSeconds -ne 0){throw 'A watcher/sleep gap must restart the transition observation window.'}
    $reset=Get-ManagedTransitionProgress $progress 'new-session' $observation $now.AddSeconds(46)
    if($reset.elapsedSeconds -ne 0){throw 'A new routing generation must restart the transition observation window.'}
    $reset=Get-ManagedTransitionProgress $progress 'session' $observation $now.AddSeconds(44)
    if($reset.elapsedSeconds -ne 0 -or (Test-ManagedTransitionTimeout $progress 'session' $observation $now.AddSeconds(44))){throw 'A backwards clock must not authorize a pending reset.'}
    $observation.managed=$false
    if((Get-ManagedTransitionProgress $progress 'session' $observation $now.AddSeconds(46)) -or (Test-ManagedTransitionTimeout $progress 'session' $observation $now.AddSeconds(46))){throw 'An unidentified transition must never authorize a disconnect.'}
    $observation.managed=$true;$observation.state='Connected'
    if(Test-ManagedTransitionTimeout $progress 'session' $observation $now.AddSeconds(46)){throw 'A recovered connection must not be reset from old transition history.'}
    if((Get-WatchRecoveryAction $true 9 'Transitioning' 0 44) -ne 'Observe' -or (Get-WatchRecoveryAction $true 9 'Transitioning' 0 45) -ne 'RepairVpn'){throw 'Only the independently confirmed managed timeout may request targeted repair.'}
    foreach($wireState in @('Transitioning','Unknown','Foreign','Unexpected')){
        foreach($alive in @($true,$false)){
            foreach($mismatch in @(0,2,9)){
                if((Get-WatchRecoveryAction $alive $mismatch $wireState 9) -ne 'Observe'){throw 'Unconfirmed WireSock ownership/state must not consume recovery attempts, including after resume.'}
            }
        }
    }
    foreach($wireState in @('Connected','Disconnected')){
        if((Get-WatchRecoveryAction $false 0 $wireState 0) -ne 'Reconnect'){throw 'A missing router with confirmed ownership must still recover.'}
        if((Get-WatchRecoveryAction $true 2 $wireState 0) -ne 'Reconnect'){throw 'A confirmed changed network must still recover after WireSock settles.'}
    }
    if((Get-WatchRecoveryAction $true 0 'Disconnected' 3) -ne 'RepairVpn'){throw 'Confirmed transport disconnection must retain component repair.'}
    if((Get-WatchRecoveryAction $true 1 'Connected' 0) -ne 'Observe'){throw 'A single network observation cannot restart a healthy router.'}
    if((@(0,1,2,3,4,9) | ForEach-Object {Get-RecoveryDelay $_}) -join ',' -ne '2,5,15,30,60,60'){throw 'Recovery delays must remain bounded.'}
    $path=Join-Path $runtime 'session.json'
    Write-JsonFile $path @{id='current';running=$true;bridgeReady=$true;mode='ProxyOnly'}
    $script:pendingResets=0;$script:pausedBridges=0;$script:pendingOwned=$true
    function Get-SessionRouter($Session) {return @([pscustomobject]@{Id=1})}
    function Get-SessionNetworkSignature {throw 'A pending reset must not rebind a changed network.'}
    function Test-SessionVpnHold($Session) {$false}
    function Get-WireSockObservation {[pscustomobject]@{state='Transitioning';managed=$script:pendingOwned}}
    function Invoke-BridgeUpdate([bool]$Connected) {if($Connected){throw 'A pending reset cannot resume unverified VPN traffic.'};$script:pausedBridges++}
    function Disconnect-ManagedWireSock([switch]$OnlyTransitioning) {if(-not $OnlyTransitioning){throw 'Pending cleanup must recheck its exact state.'};$script:pendingResets++}
    $now=[DateTime]::UtcNow
    Write-JsonFile (Join-Path $runtime 'managed-transition.json') @{schema=1;sessionId='current';startedUtc=$now.AddSeconds(-46).ToString('o');updatedUtc=$now.ToString('o')}
    Repair-SessionVpn 'current'
    if($script:pendingResets -ne 1 -or $script:pausedBridges -ne 1){throw 'A stalled owned transition must pause only its bridge and reset once, without a router rebind.'}
    $script:pendingOwned=$false;Repair-SessionVpn 'current';Repair-SessionVpn 'old'
    if($script:pendingResets -ne 1){throw 'A foreign or old-session transition was reset.'}
    if(Set-SessionRecovering 'stale' 'test'){throw 'A stale recovery was accepted.'}
    if((Read-JsonFile $path).recovering){throw 'A stale recovery modified the session.'}
    if(-not(Set-SessionRecovering 'current' 'test')){throw 'A current recovery was rejected.'}
    $state=Read-JsonFile $path
    if(-not $state.recovering -or $state.bridgeReady -or $state.mode -ne 'ProxyOnly'){throw 'Recovering state did not preserve session identity/mode.'}
    $self=Get-Process -Id $PID
    $watchSessionId=[guid]::NewGuid().ToString('N');Write-WatchProcessIdentity @{id=$watchSessionId}
    $watchIdentity=Read-JsonFile (Join-Path $runtime 'watch-process.json')
    if($watchIdentity.Pid -ne $PID -or $watchIdentity.Path -ne $self.Path -or $watchIdentity.SessionId -ne $watchSessionId -or [math]::Abs((([datetime]$watchIdentity.StartedUtc).ToUniversalTime()-$self.StartTime.ToUniversalTime()).TotalMilliseconds) -ge 100){throw 'Watcher diagnostic counters require an exact PID/start/image/session identity.'}
    $identity=@{Pid=$PID;Path=$self.Path;StartedUtc=$self.StartTime.ToUniversalTime().ToString('o')}
    Write-JsonFile (Join-Path $runtime 'policy-process.json') $identity
    if(Test-PolicyProcess){throw 'An arbitrary executable was accepted as the protected controller.'}
    $expected=Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe'
    function Test-ComponentLiveness {$false} # Old-agent rollback without a progress contract.
    function Get-Process {param($Id,$ErrorAction) [pscustomobject]@{Path=$expected;StartTime=$self.StartTime}}
    $identity.Path=$expected;Write-JsonFile (Join-Path $runtime 'policy-process.json') $identity
    if(-not(Test-PolicyProcess)){throw 'Valid protected controller process identity was rejected.'}
    $identity.StartedUtc=[DateTime]::UtcNow.AddDays(-1).ToString('o');Write-JsonFile (Join-Path $runtime 'policy-process.json') $identity
    if(Test-PolicyProcess){throw 'A reused PID must not count as a live controller.'}
    Write-Output 'PASS: capped recovery delays, continuous managed-transition timeout, watcher/sleep gaps, clock rollback, stale generation rejection, recovering state preservation and controller PID/start-time/path identity.'
}finally {
    $prefix=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runtime\session-tests-'))
    if(-not [IO.Path]::GetFullPath($fixture).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture cleanup.'}
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
