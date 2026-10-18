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
    $path=Join-Path $runtime 'policy-process.json'
    if(-not(Test-Path -LiteralPath $path)){return $false}
    try {
        $identity=Read-JsonFile $path
        $process=Get-Process -Id $identity.Pid -ErrorAction SilentlyContinue
        return [bool]($process -and $process.Path -eq $identity.Path -and [Math]::Abs(($process.StartTime.ToUniversalTime()-([datetime]$identity.StartedUtc).ToUniversalTime()).TotalMilliseconds) -lt 100)
    }catch{return $false}
}
function Start-PolicyController {
    $policyExe=Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe'
    $process=Start-Process -FilePath $policyExe -ArgumentList @('--policy-worker',('"'+$root+'"')) -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $root 'logs\policy-error.log') -RedirectStandardOutput (Join-Path $root 'logs\policy-output.log')
    Write-JsonFile (Join-Path $runtime 'policy-process.json') @{Pid=$process.Id;StartedUtc=$process.StartTime.ToUniversalTime().ToString('o');Path=$policyExe}
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

function Get-WatchRecoveryAction([bool]$RouterAlive,[int]$NetworkMismatch,[string]$WireSockState,[int]$DisconnectedSamples) {
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
    if(-not @(Get-SessionRouter $session).Count){return}
    $signature=Get-SessionNetworkSignature
    if($signature -ne $session.networkSignature){throw 'Network changed; component repair deferred.'}
    if($session.mode -eq 'Start' -and (Get-NetAdapter -Name 'VPNUS' -ErrorAction SilentlyContinue).Status -eq 'Up'){throw 'VPNUS is active; component repair paused.'}
    $observation=Get-WireSockObservation
    if($observation.state -notin @('Connected','Disconnected')){return}
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
    if(-not $Probe.process.HasExited -and ([DateTime]::UtcNow-$Probe.startedUtc).TotalSeconds -lt 20){return $null}
    try {
        $healthy=$false
        if($Probe.process.HasExited -and $Probe.process.ExitCode -eq 0 -and $Probe.output.IsCompleted -and $Probe.errorOutput.IsCompleted){
            try{$result=$Probe.output.GetAwaiter().GetResult() | ConvertFrom-Json;$healthy=$result.healthy -eq $true}catch{}
        }
        return @{discarded=$false;sessionId=$Session.id;bridgeGeneration=$Session.bridgeGeneration;healthy=$healthy;message=$(if($healthy){'Managed VPN HTTPS verified.'}else{'VPN HTTPS could not be verified; no full network restart requested.'});updatedUtc=[DateTime]::UtcNow.ToString('o')}
    }finally{Stop-SessionHealthProbe $Probe}
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
    $healthySince=$null;$repair=$null;$repairAction='';$probe=$null;$healthFailures=0
    $disconnected=0;$networkMismatch=0;$nextHealth=[DateTime]::UtcNow.AddSeconds(10)
    $lastFeedAttempt=[DateTime]::UtcNow;$feedUpdater=$null
    try {
        while($true){
            Start-Sleep -Seconds 2
            if(Test-Path -LiteralPath (Join-Path $runtime 'stop.request')){break}
            try{$session=Read-JsonFile (Join-Path $runtime 'session.json')}catch{continue}
            if(-not $session.running){break}
            if($session.id -ne $watchedSession.id){
                if($session.recoveryParentId -ne $watchedSession.id){break}
                $watchedSession=$session;$budget=@{sessionId=$session.id;Reconnect=0;RepairVpn=0;Bridge=0;Policy=0}
                $healthFailures=0;$disconnected=0;$networkMismatch=0;$healthySince=$null
                Stop-SessionHealthProbe $probe;$probe=$null
                $nextHealth=[DateTime]::UtcNow.AddSeconds(10)
            }
            if($repair -and -not $repair.HasExited){
                Write-WatchState $session.id 'Recovering' 'Component operation in progress' 'Unknown'
                continue
            }
            if($repair){
                if($repair.ExitCode -eq 75 -and $repairAction){$budget[$repairAction]=[Math]::Max(0,$budget[$repairAction]-1);Write-JsonFile $budgetPath $budget}
                $repair.Dispose();$repair=$null
            }
            $observation=Get-WireSockObservation
            $connected=$observation.state -eq 'Connected'
            $routerAlive=@(Get-SessionRouter $session).Count -gt 0
            $signature='unavailable'
            try{$signature=Get-SessionNetworkSignature}catch{}
            if($signature -ne 'unavailable' -and $session.networkSignature -and $signature -ne $session.networkSignature){$networkMismatch++}else{$networkMismatch=0}
            if($observation.state -eq 'Disconnected'){$disconnected++}else{$disconnected=0}
            $action=Get-WatchRecoveryAction $routerAlive $networkMismatch $observation.state $disconnected
            if($action -ne 'Observe'){
                Stop-SessionHealthProbe $probe;$probe=$null;$healthySince=$null
                if($budget[$action] -lt 3 -and [DateTime]::UtcNow -ge $nextAttempt){
                    $budget[$action]++;Write-JsonFile $budgetPath $budget
                    $nextAttempt=[DateTime]::UtcNow.AddSeconds((Get-RecoveryDelay $budget[$action]))
                    $repairAction=$action
                    $repair=Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -PassThru -WorkingDirectory $root `
                        -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$WatchScript+'"'),'-Action',$action,'-ExpectedSessionId',$session.id) `
                        -RedirectStandardOutput (Join-Path $root 'logs\auto-reconnect.log') -RedirectStandardError (Join-Path $root 'logs\auto-reconnect-error.log')
                    Write-WatchState $session.id 'Recovering' $action $observation.state $budget[$action]
                }elseif($budget[$action] -ge 3){Write-WatchState $session.id 'NeedsAttention' 'Component retry limit reached' $observation.state $budget[$action]}
                continue
            }
            # Pause on Unknown/Foreign as well as Disconnected: this bridge has no
            # independently enforced VPN-only egress when WireSock is unavailable.
            $bridgeAlive=@(Get-SessionBridge $session).Count -gt 0
            $needsBridge=if($connected){-not $bridgeAlive -or -not $session.bridgeReady}else{$bridgeAlive -or $session.bridgeReady}
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
                        if($completed.healthy){$healthFailures=0}else{$healthFailures++}
                        $nextHealth=[DateTime]::UtcNow.AddSeconds($(if($healthFailures -eq 1){5}else{30}))
                    }
                }
            }
            if($connected -and $session.bridgeReady -and -not $probe -and [DateTime]::UtcNow -ge $nextHealth){
                try{$probe=Start-SessionHealthProbe $session}
                catch{$healthFailures++;$nextHealth=[DateTime]::UtcNow.AddSeconds(30)}
            }
            if(-not(Test-PolicyProcess) -and $budget.Policy -lt 3 -and [DateTime]::UtcNow -ge $nextPolicy){
                $leased=$false;$policyLock=$null
                try {
                    Invoke-AgentCoordinator 'begin-operation';$leased=$true
                    $policyLock=[IO.File]::Open((Join-Path $runtime 'network-control.lock'),'OpenOrCreate','ReadWrite','None')
                    if((Test-RecoverySession $session.id) -and -not(Test-PolicyProcess)){
                        $budget.Policy++;Write-JsonFile $budgetPath $budget
                        Start-PolicyController
                    }
                }catch{[IO.File]::WriteAllText((Join-Path $root 'logs\recovery-error.log'),'Policy worker repair deferred or failed; router was retained.')}
                finally{if($policyLock){$policyLock.Dispose()};if($leased){Invoke-AgentCoordinator 'end-operation'}}
                $nextPolicy=[DateTime]::UtcNow.AddSeconds((Get-RecoveryDelay $budget.Policy))
            }
            $phase='Watching';$reason=''
            if($observation.state -eq 'Foreign'){$phase='NeedsAttention';$reason='Another WireSock profile is active'}
            elseif(-not $connected){$phase='Suspect';$reason='Managed VPN connection is not confirmed'}
            elseif((-not $session.bridgeReady -and $budget.Bridge -ge 3) -or (-not(Test-PolicyProcess) -and $budget.Policy -ge 3)){$phase='NeedsAttention';$reason='Component retry limit reached'}
            elseif(-not $session.bridgeReady -or -not(Test-PolicyProcess)){$phase='Degraded';$reason='A component is unavailable'}
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
