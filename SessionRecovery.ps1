if(-not(Get-Command Get-PolicyObservation -CommandType Function -ErrorAction SilentlyContinue)){. (Join-Path $PSScriptRoot 'WireSockRecovery.ps1')}
function Test-PolicyV2 {
    $agent=Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe'
    return ([version][Diagnostics.FileVersionInfo]::GetVersionInfo($agent).FileVersion -ge [version]'0.2.3.0')
}
function Get-SessionNetworkSignature {
    if(-not(Test-PolicyV2)){
        # Compatibility for an automatic package rollback to the previous agent.
        $config=Read-JsonFile (Join-Path $runtime 'bridge.json')
        $nic=[Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object Name -eq $config.outbounds[0].bind_interface | Select-Object -First 1
        if(-not $nic){return 'missing-interface'}
        $ips=$nic.GetIPProperties()
        $text=$nic.Id+'|'+$nic.OperationalStatus+'|'+(($ips.UnicastAddresses | ForEach-Object Address | Sort-Object) -join ',')+'|'+(($ips.GatewayAddresses | ForEach-Object Address | Sort-Object) -join ',')+'|'+(($ips.DnsAddresses | Sort-Object) -join ',')
        $sha=[Security.Cryptography.SHA256]::Create()
        try{return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))).Replace('-','')}finally{$sha.Dispose()}
    }
    $signature=Read-HelperOutput (Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe') ('--network-signature "'+$root+'"')
    if(($signature -join '').Trim() -notmatch '^(?:[A-F0-9]{64}|missing-interface:.+)$'){throw 'Could not read the canonical network signature.'}
    return ($signature -join '').Trim()
}
function Test-PolicyProcess {
    return (Get-PolicyObservation).state -in @('Healthy','Starting')
}
function Start-PolicyController {
    $policyExe=Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe'
    $process=Start-Process -FilePath $policyExe -ArgumentList @('--policy-worker',('"'+$root+'"')) -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $root 'logs\policy-error.log') -RedirectStandardOutput (Join-Path $root 'logs\policy-output.log')
    $session=Read-JsonFile (Join-Path $runtime 'session.json')
    Write-JsonFile (Join-Path $runtime 'policy-process.json') @{Pid=$process.Id;StartedUtc=$process.StartTime.ToUniversalTime().ToString('o');Path=$policyExe;SessionId=$session.id}
}
function Set-SessionRecovering([string]$ExpectedId,[string]$Reason) {
    $session=Read-JsonFile (Join-Path $runtime 'session.json')
    if($session.id -ne $ExpectedId -or -not $session.running){return $false}
    $session | Add-Member -NotePropertyName recovering -NotePropertyValue $true -Force
    $session | Add-Member -NotePropertyName recoveryReason -NotePropertyValue $Reason -Force
    $session | Add-Member -NotePropertyName bridgeReady -NotePropertyValue $false -Force
    Write-JsonFile (Join-Path $runtime 'session.json') $session
    return $true
}
function Get-RecoveryDelay([int]$Attempts) { return [int](@(2,5,15,30,60)[[Math]::Min([Math]::Max(0,$Attempts),4)]) }
function Test-WatchObservationGap([datetime]$Previous,[datetime]$Now) {return $Now -lt $Previous -or ($Now-$Previous).TotalSeconds -gt 20}
function Test-RecoveryNetworkAvailable {
    # Match preparation's actual requirements before spending its retry budget.
    try{return @(Get-NetIPConfiguration -ErrorAction Stop | Where-Object {$_.NetAdapter.HardwareInterface -and $_.NetAdapter.Status -eq 'Up' -and $_.IPv4DefaultGateway}).Count -gt 0}
    catch{return $false}
}

function Test-RecoverySession([string]$ExpectedId) {
    if(Test-Path -LiteralPath (Join-Path $runtime 'stop.request')){return $false}
    $current=Read-JsonFile (Join-Path $runtime 'session.json')
    return [bool]($ExpectedId -and $current.id -eq $ExpectedId -and $current.running)
}

function Get-SessionRouter($Session) {
    try{return @(Get-OurProcesses | Where-Object {
        $_.Path -eq $routerExe -and $_.Id -eq $Session.routerPid -and
        (-not $Session.routerStartedUtc -or [Math]::Abs(($_.StartTime.ToUniversalTime()-([datetime]$Session.routerStartedUtc).ToUniversalTime()).TotalMilliseconds) -lt 100)
    })}catch{return @()}
}

function Get-ManagedTransitionProgress($Previous,[string]$SessionId,$Observation,[datetime]$Now) {
    if(-not $SessionId -or $Observation.state -ne 'Transitioning' -or -not $Observation.managed){return $null}
    $first=$Now
    try{
        $updated=([datetime]$Previous.updatedUtc).ToUniversalTime();$started=([datetime]$Previous.startedUtc).ToUniversalTime()
        # A long watcher pause, clock rollback or another session starts a new
        # observation window; suspended time alone never authorizes a reset.
        if($Previous.schema -eq 1 -and $Previous.sessionId -eq $SessionId -and $started -le $updated -and $updated -le $Now -and ($Now-$updated).TotalSeconds -le 20){$first=$started}
    }catch{}
    return @{schema=1;sessionId=$SessionId;startedUtc=$first.ToString('o');updatedUtc=$Now.ToString('o');elapsedSeconds=[Math]::Max(0,[Math]::Floor(($Now-$first).TotalSeconds))}
}
function Test-ManagedTransitionTimeout($Progress,[string]$SessionId,$Observation,[datetime]$Now) {
    try{
        $updated=([datetime]$Progress.updatedUtc).ToUniversalTime();$started=([datetime]$Progress.startedUtc).ToUniversalTime()
        return [bool]($Observation.state -eq 'Transitioning' -and $Observation.managed -and $Progress.schema -eq 1 -and $Progress.sessionId -eq $SessionId -and
            $started -le $updated -and $updated -le $Now -and ($Now-$updated).TotalSeconds -le 20 -and ($Now-$started).TotalSeconds -ge 45)
    }catch{return $false}
}
function Get-WatchRecoveryAction([bool]$RouterAlive,[int]$NetworkMismatch,[string]$WireSockState,[int]$DisconnectedSamples,[int]$ManagedTransitionSeconds=0) {
    if($WireSockState -eq 'Transitioning' -and $ManagedTransitionSeconds -ge 45){return 'RepairVpn'}
    # Reconnect itself requires a confirmed managed state. Do not consume its
    # retry budget while WireSock is restoring a link after sleep, or when
    # ownership is unknown. The independent bridge safety pause still applies.
    if($WireSockState -notin @('Connected','Disconnected')){return 'Observe'}
    if(-not $RouterAlive){return 'Reconnect'}
    if($NetworkMismatch -ge 2){return 'Reconnect'}
    if($WireSockState -eq 'Disconnected' -and $DisconnectedSamples -ge 3){return 'RepairVpn'}
    # Unknown, an unrelated profile or failed control HTTPS never justify a TUN restart.
    return 'Observe'
}

function Repair-SessionVpn([string]$ExpectedId) {
    # Caller owns the rescue lease AND network-control.lock. No router/policy restart.
    if(-not(Test-RecoverySession $ExpectedId)){return}
    $session=Read-JsonFile (Join-Path $runtime 'session.json')
    if(Test-SessionVpnHold $session){return}
    if(-not @(Get-SessionRouter $session).Count){return}
    if($session.mode -eq 'Start' -and (Get-NetAdapter -Name 'VPNUS' -ErrorAction SilentlyContinue).Status -eq 'Up'){throw 'VPNUS is active; component repair paused.'}
    $observation=Get-WireSockObservation
    if($observation.state -eq 'Transitioning'){
        $progress=$null;try{$progress=Read-JsonFile (Join-Path $runtime 'managed-transition.json')}catch{}
        if(-not(Test-ManagedTransitionTimeout $progress $ExpectedId $observation ([DateTime]::UtcNow))){return}
        # Reset only the confirmed managed pending connection. The ordinary
        # disconnected-state recovery will then reconnect or rebind the router
        # as required by the current network, under its existing checks.
        Invoke-BridgeUpdate $false
        if(-not(Test-RecoverySession $ExpectedId)){return}
        Disconnect-ManagedWireSock -OnlyTransitioning
        return
    }
    if($observation.state -notin @('Connected','Disconnected')){return}
    $signature=Get-SessionNetworkSignature
    if($signature -ne $session.networkSignature){throw 'Network changed; component repair deferred.'}
    if($observation.state -eq 'Disconnected'){Invoke-BridgeUpdate $false;Connect-WireSock}
    if(-not(Test-RecoverySession $ExpectedId)){return}
    if((Get-WireSockObservation).state -ne 'Connected'){throw 'Managed WireSock connection is not confirmed.'}
    if(-not(Test-RecoverySession $ExpectedId)){return}
    Invoke-BridgeUpdate $true
}

function Write-WatchState([string]$SessionId,[string]$Phase,[string]$Reason,[string]$WireSockState,[int]$Attempts=0) {
    $now=[DateTime]::UtcNow.ToString('o')
    Write-JsonFile (Join-Path $runtime 'watch-state.json') @{schema=2;phase=$Phase;reason=$Reason;sessionId=$SessionId;wireSockState=$WireSockState;attempts=$Attempts;updatedUtc=$now}
    # A bounded transition history; no hostnames, addresses, configs or CLI output.
    $key=$SessionId+'|'+$Phase+'|'+$Reason
    if($script:lastRecoveryEvent -eq $key){return}
    $script:lastRecoveryEvent=$key
    $path=Join-Path $root 'data\recovery-events.json'
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'data'))
    $events=@();try{$events=@((Read-JsonFile $path).events)}catch{}
    $events=@($events | Select-Object -Last 63)+@(@{sessionId=$SessionId;phase=$Phase;reason=$Reason;wireSockState=$WireSockState;attempts=$Attempts;utc=$now})
    Write-JsonFile $path @{schema=1;events=$events}
}

function Start-SessionHealthProbe($Session) {
    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName=Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe'
    $info.Arguments='--session-health';$info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $process=New-Object Diagnostics.Process;$process.StartInfo=$info
    try {
        if(-not $process.Start()){throw 'Health probe did not start.'}
        return @{process=$process;output=$process.StandardOutput.ReadToEndAsync();errorOutput=$process.StandardError.ReadToEndAsync();sessionId=$Session.id;bridgeGeneration=$Session.bridgeGeneration;startedUtc=[DateTime]::UtcNow}
    }catch{$process.Dispose();throw}
}

function Stop-SessionHealthProbe($Probe) {
    if(-not $Probe){return}
    try{if(-not $Probe.process.HasExited){$Probe.process.Kill();[void]$Probe.process.WaitForExit(1000)}}catch{}
    $Probe.process.Dispose()
}

function Complete-SessionHealthProbe($Probe,$Session) {
    if($Probe.sessionId -ne $Session.id -or $Probe.bridgeGeneration -ne $Session.bridgeGeneration -or -not $Session.running -or -not $Session.bridgeReady){Stop-SessionHealthProbe $Probe;return @{discarded=$true}}
    # An exited child may have finished BEFORE sleep. Never timestamp its old
    # result as a fresh post-resume proof, even when its PID/generation survived.
    if(Test-WatchObservationGap $Probe.startedUtc ([DateTime]::UtcNow)){Stop-SessionHealthProbe $Probe;return @{discarded=$true}}
    if(-not $Probe.process.HasExited -and ([DateTime]::UtcNow-$Probe.startedUtc).TotalSeconds -lt 20){return $null}
    try {
        $healthy=$false;$code='Unverified'
        if($Probe.process.HasExited -and $Probe.process.ExitCode -eq 0 -and $Probe.output.IsCompleted -and $Probe.errorOutput.IsCompleted){
            try{$result=$Probe.output.GetAwaiter().GetResult() | ConvertFrom-Json;$healthy=$result.healthy -eq $true;if($result.code -in @('Verified','StatusUnavailable','ProfileNotConnected','ProfileChanged','MissingExpectedEgress','EgressMismatch','ControlHttpsUnavailable','InvalidConfiguration')){$code=$result.code}}catch{}
        }
        return @{discarded=$false;sessionId=$Session.id;bridgeGeneration=$Session.bridgeGeneration;healthy=$healthy;code=$code;message=$(if($healthy){'Managed VPN HTTPS verified.'}else{'VPN HTTPS could not be verified; no full network restart requested.'});updatedUtc=[DateTime]::UtcNow.ToString('o')}
    }finally{Stop-SessionHealthProbe $Probe}
}

function Set-UnsafeVpnHold($Health) {
    if($Health.healthy -or $Health.code -ne 'EgressMismatch'){return}
    $held=$null;$leased=$false
    try {
        Invoke-AgentCoordinator 'begin-operation';$leased=$true
        $held=[IO.File]::Open((Join-Path $runtime 'network-control.lock'),'OpenOrCreate','ReadWrite','None')
        if(-not(Test-RecoverySession $Health.sessionId)){return}
        $current=Read-JsonFile (Join-Path $runtime 'session.json')
        if($current.bridgeGeneration -ne $Health.bridgeGeneration){return}
        $current|Add-Member -NotePropertyName vpnHoldReason -NotePropertyValue 'EgressMismatch' -Force
        Write-JsonFile (Join-Path $runtime 'session.json') $current
        Invoke-BridgeUpdate $false
    }finally{if($held){$held.Dispose()};if($leased){Invoke-AgentCoordinator 'end-operation'}}
}

function Watch-Session([string]$WatchScript) {
    # Only one watcher may own per-session retry budgets and event history.
    try{$watchOwner=[IO.File]::Open((Join-Path $runtime 'watch-control.lock'),'OpenOrCreate','ReadWrite','None')}catch{return}
    try {
    [IO.File]::WriteAllText((Join-Path $runtime 'watch.pid'),[string]$PID)
    $watchedSession=Read-JsonFile (Join-Path $runtime 'session.json')
    $budgetPath=Join-Path $runtime 'component-recovery.json'
    $budget=@{sessionId=$watchedSession.id;Reconnect=0;RepairVpn=0;Bridge=0;Policy=0}
    try {
        $saved=Read-JsonFile $budgetPath
        if($saved.sessionId -eq $watchedSession.id){foreach($component in @('Reconnect','RepairVpn','Bridge','Policy')){$budget[$component]=[Math]::Min(3,[Math]::Max(0,[int]$saved.$component))}}
    }catch{}
    $nextAttempt=[DateTime]::UtcNow;$nextBridge=[DateTime]::UtcNow;$nextPolicy=[DateTime]::UtcNow
    $healthySince=$null;$repair=$null;$repairTimer=$null;$repairAction='';$probe=$null;$healthFailures=0;$policyStaleSamples=0
    $disconnected=0;$networkMismatch=0;$nextHealth=[DateTime]::UtcNow.AddSeconds(10)
    $nextBridgeCheck=[DateTime]::MinValue;$listenerFailures=0;$listenerEpoch='';$listenerState='Unknown'
    $lastFeedAttempt=[DateTime]::UtcNow;$feedUpdater=$null;$transition=$null
    $lastObservation=[DateTime]::UtcNow;$nextNetworkCheck=[DateTime]::MinValue;$networkAvailable=$false
    try {
        while($true){
            Start-Sleep -Seconds 2
            if(Test-Path -LiteralPath (Join-Path $runtime 'stop.request')){break}
            try{$session=Read-JsonFile (Join-Path $runtime 'session.json')}catch{continue}
            if(-not $session.running){break}
            $observedNow=[DateTime]::UtcNow
            if(Test-WatchObservationGap $lastObservation $observedNow){
                Stop-SessionHealthProbe $probe;$probe=$null;$nextHealth=$observedNow
                $networkMismatch=0;$disconnected=0;$listenerFailures=0;$policyStaleSamples=0;$healthySince=$null;$transition=$null
                $nextBridgeCheck=[DateTime]::MinValue;$nextNetworkCheck=[DateTime]::MinValue
                Write-ConnectionEvidence $root 'WatchObservationReset' $session
            }
            $lastObservation=$observedNow
            if($session.id -ne $watchedSession.id){
                if($session.recoveryParentId -ne $watchedSession.id){break}
                $watchedSession=$session;$budget=@{sessionId=$session.id;Reconnect=0;RepairVpn=0;Bridge=0;Policy=0}
                $healthFailures=0;$disconnected=0;$networkMismatch=0;$healthySince=$null;$policyStaleSamples=0
                Stop-SessionHealthProbe $probe;$probe=$null
                $nextHealth=[DateTime]::UtcNow.AddSeconds(10)
            }
            if($repair -and -not $repair.HasExited){
                $repairObservation=Get-WireSockObservation
                if($repairObservation.state -ne 'Connected' -or (Test-SessionVpnHold $session)){Invoke-EmergencyBridgePause $session}
                if($repairTimer -and $repairTimer.Elapsed.TotalSeconds -ge 90){Write-WatchState $session.id 'NeedsAttention' 'Component operation exceeded its deadline; bridge safety remains active' $repairObservation.state}
                elseif((Test-SessionVpnHold $session) -or $repairObservation.state -eq 'Foreign'){Write-WatchState $session.id 'NeedsAttention' 'VPN paused while another component operation is in progress' $repairObservation.state}
                else{Write-WatchState $session.id 'Recovering' 'Component operation in progress' $repairObservation.state}
                continue
            }
            if($repair){
                if($repair.ExitCode -eq 75 -and $repairAction){$budget[$repairAction]=[Math]::Max(0,$budget[$repairAction]-1);Write-JsonFile $budgetPath $budget}
                $repair.Dispose();$repair=$null;$repairTimer=$null
            }
            $observation=Get-WireSockObservation
            $now=[DateTime]::UtcNow
            $transition=Get-ManagedTransitionProgress $transition $session.id $observation $now
            $transitionPath=Join-Path $runtime 'managed-transition.json'
            if($transition){Write-JsonFile $transitionPath $transition}else{Remove-Item -LiteralPath $transitionPath -Force -ErrorAction SilentlyContinue}
            $transitionSeconds=if(Test-ManagedTransitionTimeout $transition $session.id $observation $now){[int][Math]::Min(300,$transition.elapsedSeconds)}else{0}
            $connected=$observation.state -eq 'Connected'
            $routerAlive=@(Get-SessionRouter $session).Count -gt 0
            $signature='unavailable'
            try{$signature=Get-SessionNetworkSignature}catch{}
            if($signature -ne 'unavailable' -and $session.networkSignature -and $signature -ne $session.networkSignature){$networkMismatch++}else{$networkMismatch=0}
            if($observation.state -eq 'Disconnected'){$disconnected++}else{$disconnected=0}
            $action=Get-WatchRecoveryAction $routerAlive $networkMismatch $observation.state $disconnected $transitionSeconds
            # A busy coordinator must not lose a confirmed mismatch. Retry only
            # a fresh result from this exact session and bridge generation.
            if(-not $session.vpnHoldReason){
                try{
                    $pendingHealth=Read-JsonFile (Join-Path $runtime 'session-health.json')
                    $healthAge=([DateTime]::UtcNow-[DateTime]::Parse($pendingHealth.updatedUtc).ToUniversalTime()).TotalSeconds
                    if($pendingHealth.code -eq 'EgressMismatch' -and $pendingHealth.sessionId -eq $session.id -and $pendingHealth.bridgeGeneration -eq $session.bridgeGeneration -and $healthAge -ge -2 -and $healthAge -lt 90){
                        Invoke-EmergencyBridgePause $session -PersistHold
                        Set-UnsafeVpnHold $pendingHealth
                        $session=Read-JsonFile (Join-Path $runtime 'session.json')
                    }
                }catch{}
            }
            if(-not $connected -or (Test-SessionVpnHold $session)){Invoke-EmergencyBridgePause $session}
            if(Test-SessionVpnHold $session){$action='Observe'}
            if(-not(Test-SessionVpnHold $session) -and $observation.state -notin @('Unknown','Foreign') -and ($action -ne 'Observe' -or $networkMismatch -ge 2 -or $observation.state -eq 'Disconnected' -or ($observation.state -eq 'Transitioning' -and $observation.managed))){
                if([DateTime]::UtcNow -ge $nextNetworkCheck){$networkAvailable=Test-RecoveryNetworkAvailable;$nextNetworkCheck=[DateTime]::UtcNow.AddSeconds(5)}
                if(-not $networkAvailable){Stop-SessionHealthProbe $probe;$probe=$null;$healthySince=$null;Write-WatchState $session.id 'WaitingForNetwork' 'Waiting for a physical IPv4 network; retry budget retained' $observation.state;continue}
            }
            if($action -ne 'Observe'){
                Stop-SessionHealthProbe $probe;$probe=$null;$healthySince=$null
                if($budget[$action] -lt 3 -and [DateTime]::UtcNow -ge $nextAttempt){
                    $budget[$action]++;Write-JsonFile $budgetPath $budget
                    $nextAttempt=[DateTime]::UtcNow.AddSeconds((Get-RecoveryDelay $budget[$action]))
                    $repairAction=$action
                    $repair=Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -PassThru -WorkingDirectory $root `
                        -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$WatchScript+'"'),'-Action',$action,'-ExpectedSessionId',$session.id) `
                        -RedirectStandardOutput (Join-Path $root 'logs\auto-reconnect.log') -RedirectStandardError (Join-Path $root 'logs\auto-reconnect-error.log')
                    $repairTimer=[Diagnostics.Stopwatch]::StartNew()
                    Write-WatchState $session.id 'Recovering' $action $observation.state $budget[$action]
                }elseif($budget[$action] -ge 3){Write-WatchState $session.id 'NeedsAttention' 'Component retry limit reached' $observation.state $budget[$action]}
                continue
            }
            # Pause on Unknown/Foreign as well as Disconnected: this bridge has no
            # independently enforced VPN-only egress when WireSock is unavailable.
            $bridgeAlive=@(Get-SessionBridge $session).Count -gt 0
            $currentBridgeEpoch=$session.id+':'+$session.bridgeGeneration
            if($listenerEpoch -ne $currentBridgeEpoch){$listenerEpoch=$currentBridgeEpoch;$listenerFailures=0;$listenerState='Unknown';$nextBridgeCheck=[DateTime]::MinValue}
            if($connected -and $bridgeAlive -and $session.bridgeReady -and -not(Test-SessionVpnHold $session) -and [DateTime]::UtcNow -ge $nextBridgeCheck){
                $listenerState=Get-BridgeListenerObservation $session
                if($listenerState -eq 'Missing'){$listenerFailures++}else{$listenerFailures=0}
                $nextBridgeCheck=[DateTime]::UtcNow.AddSeconds(5)
            }
            $needsBridge=if($connected -and -not(Test-SessionVpnHold $session)){-not $bridgeAlive -or -not $session.bridgeReady}else{$bridgeAlive -or $session.bridgeReady}
            if($connected -and ($listenerFailures -ge 2 -or $listenerState -eq 'Foreign')){$needsBridge=$true}
            if($needsBridge -and (-not $connected -or ($budget.Bridge -lt 3 -and [DateTime]::UtcNow -ge $nextBridge))){
                try {
                    $attempted=Update-WireSockBridge $connected $session.id -ReportAttempt
                    if($connected -and $attempted){$budget.Bridge++;Write-JsonFile $budgetPath $budget}
                }catch{
                    if($connected){$budget.Bridge++;Write-JsonFile $budgetPath $budget}
                    [IO.File]::WriteAllText((Join-Path $root 'logs\recovery-error.log'),'VPN bridge repair failed; direct router was retained.')
                }
                $nextBridge=[DateTime]::UtcNow.AddSeconds((Get-RecoveryDelay $budget.Bridge))
                $session=Read-JsonFile (Join-Path $runtime 'session.json')
                if($session.id -ne $watchedSession.id -or -not $session.running){break}
            }
            if($probe){
                $completed=Complete-SessionHealthProbe $probe $session
                if($null -ne $completed){
                    $probe=$null
                    if(-not $completed.discarded){
                        Write-JsonFile (Join-Path $runtime 'session-health.json') $completed
                        if($completed.code -eq 'EgressMismatch'){
                            try{Set-UnsafeVpnHold $completed}catch{}
                            $session=Read-JsonFile (Join-Path $runtime 'session.json')
                        }
                        if($completed.healthy){$healthFailures=0}else{$healthFailures++}
                        $nextHealth=[DateTime]::UtcNow.AddSeconds($(if($healthFailures -eq 1){5}else{30}))
                    }
                }
            }
            if($connected -and $session.bridgeReady -and -not(Test-SessionVpnHold $session) -and -not $probe -and [DateTime]::UtcNow -ge $nextHealth){
                try{$probe=Start-SessionHealthProbe $session}
                catch{$healthFailures++;$nextHealth=[DateTime]::UtcNow.AddSeconds(30)}
            }
            $policyHealthy=Test-PolicyProcess
            $policyObservation=if($policyHealthy){@{state='Healthy'}}else{Get-PolicyObservation}
            if($policyObservation.state -eq 'Stale'){$policyStaleSamples++}else{$policyStaleSamples=0}
            if(-not $policyHealthy -and ($policyObservation.state -eq 'Missing' -or $policyStaleSamples -ge 2) -and $budget.Policy -lt 3 -and [DateTime]::UtcNow -ge $nextPolicy){
                $leased=$false;$policyLock=$null
                try {
                    Invoke-AgentCoordinator 'begin-operation';$leased=$true
                    $policyLock=[IO.File]::Open((Join-Path $runtime 'network-control.lock'),'OpenOrCreate','ReadWrite','None')
                    if((Test-RecoverySession $session.id) -and (Get-PolicyObservation).state -in @('Missing','Stale')){
                        $budget.Policy++;Write-JsonFile $budgetPath $budget
                        if(-not(Repair-PolicyController $session.id)){$budget.Policy--;Write-JsonFile $budgetPath $budget}
                    }
                }catch{[IO.File]::WriteAllText((Join-Path $root 'logs\recovery-error.log'),'Policy worker repair deferred or failed; router was retained.')}
                finally{if($policyLock){$policyLock.Dispose()};if($leased){Invoke-AgentCoordinator 'end-operation'}}
                $nextPolicy=[DateTime]::UtcNow.AddSeconds((Get-RecoveryDelay $budget.Policy))
            }
            $phase='Watching';$reason=''
            if(Test-SessionVpnHold $session){$phase='NeedsAttention';$reason='VPN exit mismatch or safety state unavailable; disconnect and check selected profile'}
            elseif($observation.state -eq 'Foreign'){$phase='NeedsAttention';$reason='Another WireSock profile is active'}
            elseif($listenerState -eq 'Foreign'){$phase='NeedsAttention';$reason='The local VPN bridge port belongs to another process'}
            elseif(-not $connected){$phase='Suspect';$reason='Managed VPN connection is not confirmed'}
            elseif(-not(Test-PolicyProcess) -and (Get-PolicyObservation).state -eq 'Unknown'){$phase='NeedsAttention';$reason='Policy identity or progress unavailable'}
            elseif((-not $session.bridgeReady -and $budget.Bridge -ge 3) -or (-not(Test-PolicyProcess) -and $budget.Policy -ge 3)){$phase='NeedsAttention';$reason='Component retry limit reached'}
            elseif(-not $session.bridgeReady -or -not(Test-PolicyProcess)){$phase='Degraded';$reason='A component is unavailable'}
            elseif($listenerFailures -gt 0){$phase='Degraded';$reason='The owned VPN bridge listener is not ready'}
            elseif($listenerState -eq 'Unknown'){$phase='Suspect';$reason='The local VPN bridge listener could not be verified'}
            elseif($healthFailures -ge 2){$phase='Degraded';$reason='Repeated control HTTPS failure; direct router retained'}
            elseif($signature -eq 'unavailable'){$phase='Suspect';$reason='Network identity unavailable'}
            if($phase -eq 'Watching'){
                if(-not $healthySince){$healthySince=[DateTime]::UtcNow}
                if(([DateTime]::UtcNow-$healthySince).TotalSeconds -ge 60 -and ($budget.Reconnect+$budget.RepairVpn+$budget.Bridge+$budget.Policy) -gt 0){
                    foreach($component in @('Reconnect','RepairVpn','Bridge','Policy')){$budget[$component]=0}
                    Write-JsonFile $budgetPath $budget
                }
            }else{$healthySince=$null}
            Write-WatchState $session.id $phase $reason $observation.state
            $lastFeedUpdate=(Get-Item -LiteralPath (Join-Path $runtime 'rules\russia_inside.srs')).LastWriteTimeUtc
            try{$lastFeedUpdate=[DateTime]::Parse((Read-JsonFile (Join-Path $runtime 'feeds-state.json')).updatedUtc).ToUniversalTime()}catch{}
            if(([DateTime]::UtcNow-$lastFeedUpdate).TotalHours -ge 24 -and ([DateTime]::UtcNow-$lastFeedAttempt).TotalMinutes -ge 10 -and (-not $feedUpdater -or $feedUpdater.HasExited)){
                $lastFeedAttempt=[DateTime]::UtcNow
                if($feedUpdater){$feedUpdater.Dispose()}
                $feedUpdater=Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -PassThru `
                    -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$WatchScript+'"'),'-Action','UpdateRules') `
                    -RedirectStandardError (Join-Path $root 'logs\feeds-startup.log') -RedirectStandardOutput (Join-Path $root 'logs\feeds-output.log')
            }
        }
    }finally{Stop-SessionHealthProbe $probe;if($repair){$repair.Dispose()};if($feedUpdater){$feedUpdater.Dispose()}}
    }finally{$watchOwner.Dispose()}
}
