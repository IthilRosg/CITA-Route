function Set-BridgeSession([bool]$Ready,[int]$BridgeId = 0,$Process = $null) {
    $sessionPath = Join-Path $runtime 'session.json'
    if (-not (Test-Path -LiteralPath $sessionPath)) { return }
    $session = [IO.File]::ReadAllText($sessionPath) | ConvertFrom-Json
    if ($session.bridgeReady -eq $Ready -and (-not $BridgeId -or $session.bridgePid -eq $BridgeId)) { return }
    $session | Add-Member -NotePropertyName bridgeReady -NotePropertyValue $Ready -Force
    if ($BridgeId) { $session | Add-Member -NotePropertyName bridgePid -NotePropertyValue $BridgeId -Force }
    $session | Add-Member -NotePropertyName bridgeGeneration -NotePropertyValue ([int]$session.bridgeGeneration+1) -Force
    if($Process -and $Process.StartTime){$session | Add-Member -NotePropertyName bridgeStartedUtc -NotePropertyValue $Process.StartTime.ToUniversalTime().ToString('o') -Force}
    Write-JsonFile $sessionPath $session
}

function Update-WireSockBridge([bool]$Connected,[string]$ExpectedId = '',[switch]$ReportAttempt) {
    # Watchers sample CLI state before entering this lock. Re-check the generation
    # under the same lock used by Start/Stop before touching a bridge or session.
    $watchLock=$null
    $leased=$false
    if ($ExpectedId) {
        try {
            Invoke-AgentCoordinator 'begin-operation';$leased=$true
            $watchLock=[IO.File]::Open((Join-Path $runtime 'network-control.lock'),'OpenOrCreate','ReadWrite','None')
        } catch {if($leased){Invoke-AgentCoordinator 'end-operation'};if($ReportAttempt){return $false};return}
    }
    try {
        if ($ExpectedId) {
            $current=[IO.File]::ReadAllText((Join-Path $runtime 'session.json')) | ConvertFrom-Json
            if ($current.id -ne $ExpectedId -or -not $current.running -or (Test-Path -LiteralPath (Join-Path $runtime 'stop.request'))) { return }
            # Never resume from a sample taken before acquiring coordinator ownership.
            $Connected=(Get-WireSockObservation).state -eq 'Connected'
            if(Test-Path -LiteralPath (Join-Path $runtime 'stop.request')){return}
        }
        Invoke-BridgeUpdate $Connected
        if($ReportAttempt){return $true}
    } finally { if($watchLock){$watchLock.Dispose()};if($leased){Invoke-AgentCoordinator 'end-operation'} }
}

function Get-SessionBridge($Session) {
    try{return @(Get-OurProcesses | Where-Object {
        $_.Path -eq $bridgeExe -and $_.Id -eq $Session.bridgePid -and
        (-not $Session.bridgeStartedUtc -or [Math]::Abs(($_.StartTime.ToUniversalTime()-([datetime]$Session.bridgeStartedUtc).ToUniversalTime()).TotalMilliseconds) -lt 100)
    })}catch{return @()}
}

function Get-BridgeListenerObservation($Session) {
    $owned=@(Get-SessionBridge $Session)
    if($owned.Count -ne 1){return 'Missing'}
    if($script:nativeListenerSupported -ne $false){
        try{
            $agent=Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe'
            $state=(Read-HelperOutput $agent ('--bridge-listener "'+$root.TrimEnd('\')+'"') -join '').Trim()
            if($state -in @('Ready','Missing','Foreign','Unknown')){$script:nativeListenerSupported=$true;return $state}
        }catch{}
        # One compatibility fallback for old protected agents; a transient failure
        # of a previously supported reader remains Unknown, never Missing.
        if($script:nativeListenerSupported -eq $true){return 'Unknown'}
        $script:nativeListenerSupported=$false
    }
    try {
        $listeners=@(Get-NetTCPConnection -State Listen -LocalAddress '127.0.0.1' -LocalPort 20808 -ErrorAction Stop)
        if(-not $listeners.Count){return 'Missing'}
        if(@($listeners | Where-Object OwningProcess -eq $owned[0].Id).Count -eq $listeners.Count){return 'Ready'}
        return 'Foreign'
    }catch {
        # An empty CIM result uses ObjectNotFound; access/provider errors must not
        # be turned into proof that a live bridge needs to be killed.
        if($_.CategoryInfo.Category -eq 'ObjectNotFound'){return 'Missing'}
        return 'Unknown'
    }
}

function Invoke-BridgeUpdate([bool]$Connected) {
    $session=Read-JsonFile (Join-Path $runtime 'session.json')
    if(Test-SessionVpnHold $session){$Connected=$false}
    $bridgeProcesses = @(Get-SessionBridge $session)
    $lostPath = Join-Path $root 'logs\connection-lost.txt'
    if (-not $Connected) {
        if($session.bridgeReady -or $bridgeProcesses.Count){Write-ConnectionEvidence $root 'BridgePaused' $session}
        Set-BridgeSession $false
        $bridgeProcesses | Stop-Process -Force
        if (-not (Test-Path -LiteralPath $lostPath)) {
            [IO.File]::WriteAllText($lostPath,
                'VPN bridge is paused until the selected connection is verified. Other routes were not restarted.')
        }
        return
    }
    if ($bridgeProcesses.Count) {
        $listener=Get-BridgeListenerObservation $session
        if($listener -eq 'Ready'){
            Set-BridgeSession $true $bridgeProcesses[0].Id
            Remove-Item -LiteralPath $lostPath -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath (Join-Path $root 'logs\recovery-error.log') -Force -ErrorAction SilentlyContinue
            return
        }
        if($listener -eq 'Unknown'){throw 'The bridge listener could not be verified; no live process was restarted.'}
        Set-BridgeSession $false
        if($listener -eq 'Foreign'){throw 'The local bridge port belongs to another process; no unrelated process was stopped.'}
        Write-JsonFile (Join-Path $root 'logs\bridge-repair-last.json') @{schema=1;sessionId=$session.id;bridgeGeneration=$session.bridgeGeneration;code='OwnedListenerMissing';updatedUtc=[DateTime]::UtcNow.ToString('o')}
        Write-ConnectionEvidence $root 'BridgeListenerMissing' $session
        $bridgeProcesses | Stop-Process -Force
        foreach($process in $bridgeProcesses){if($process -is [Diagnostics.Process] -and -not $process.WaitForExit(5000)){throw 'The owned bridge has not finished stopping.'}}
    }
    Set-BridgeSession $false
    $restartedBridge = Start-Helper $bridgeExe (Join-Path $runtime 'bridge.json')
    Set-BridgeSession $true $restartedBridge.Id $restartedBridge
    Write-ConnectionEvidence $root 'BridgeStarted' (Read-JsonFile (Join-Path $runtime 'session.json'))
    Remove-Item -LiteralPath $lostPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $root 'logs\recovery-error.log') -Force -ErrorAction SilentlyContinue
    [IO.File]::AppendAllText((Join-Path $root 'logs\recovery.log'),
        ([DateTime]::UtcNow.ToString('o') + " VPN bridge resumed after WireSock reconnect.`r`n"))
}

function Stop-LostRouterSession([string]$ExpectedId) {
    try { $recoveryLock = [IO.File]::Open((Join-Path $runtime 'network-control.lock'),'OpenOrCreate','ReadWrite','None') }
    catch { return $false }
    try {
        $sessionPath = Join-Path $runtime 'session.json'
        $session = [IO.File]::ReadAllText($sessionPath) | ConvertFrom-Json
        # An old watcher must never shut down a newer session.
        if ($session.id -ne $ExpectedId) { return $true }
        if (@(Get-OurProcesses | Where-Object { $_.Path -eq $routerExe -and $_.Id -eq $session.routerPid }).Count) { return $false }
        Update-WireSockBridge $false
        $session | Add-Member -NotePropertyName running -NotePropertyValue $false -Force
        $session | Add-Member -NotePropertyName bridgeReady -NotePropertyValue $false -Force
        $session | Add-Member -NotePropertyName stoppedUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('o')) -Force
        $session | Add-Member -NotePropertyName stopReason -NotePropertyValue 'RouterExited' -Force
        Write-JsonFile $sessionPath $session
        Disconnect-ManagedWireSock
        [IO.File]::WriteAllText((Join-Path $root 'logs\connection-lost.txt'),
            'The router exited unexpectedly. Its VPN bridge and managed WireSock connection were stopped. Connect again to restart routing.')
        return $true
    } finally { $recoveryLock.Dispose() }
}

# Keep liveness helpers in the existing payload file: older updaters use a fixed file allowlist.
function Test-ComponentLiveness {
    try{return [version][Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe')).FileVersion -ge [version]'0.5.5.0'}catch{return $false}
}
function Read-SmallComponentState([string]$Path) {
    $stream=[IO.File]::Open($Path,'Open','Read','ReadWrite, Delete')
    try {
        if($stream.Length -gt 16384){throw 'Component state exceeds limit.'}
        $reader=New-Object IO.StreamReader($stream,[Text.Encoding]::UTF8,$true)
        try{return ($reader.ReadToEnd() | ConvertFrom-Json)}finally{$reader.Dispose()}
    }finally{$stream.Dispose()}
}
function Get-PolicyPulseState($Pulse,$Owner,[string]$SessionId,[datetime]$Now) {
    if(-not $Pulse){if($Owner.StartedUtc -and [datetime]$Owner.StartedUtc -le $Now.AddSeconds(2) -and ($Now-([datetime]$Owner.StartedUtc).ToUniversalTime()).TotalSeconds -le 15){return 'Starting'};return 'Stale'}
    try {
        $instance=[guid]::Empty
        $progress=([datetime]$Pulse.ProgressUtc).ToUniversalTime();$deadline=([datetime]$Pulse.DeadlineUtc).ToUniversalTime()
        if($Pulse.Schema -ne 1 -or -not [guid]::TryParseExact([string]$Pulse.InstanceId,'N',[ref]$instance) -or $Pulse.Sequence -lt 1 -or
            $Pulse.SessionId -ne $SessionId -or $Pulse.Pid -ne $Owner.Pid -or
            [Math]::Abs((([datetime]$Pulse.StartedUtc).ToUniversalTime()-([datetime]$Owner.StartedUtc).ToUniversalTime()).TotalMilliseconds) -ge 100 -or
            $progress -lt ([datetime]$Owner.StartedUtc).ToUniversalTime().AddSeconds(-2) -or $progress -gt $Now.AddSeconds(2)){return 'Unknown'}
        $budget=switch($Pulse.Phase){'Starting'{45};'Publishing'{45};'Running'{15};'Stopped'{10};'Error'{10};'NetworkChanged'{10};default{0}}
        if($budget -eq 0 -or [Math]::Abs(($deadline-$progress).TotalSeconds-$budget) -gt 0.1){return 'Unknown'}
        if($Now -gt $deadline){return 'Stale'}
        if($Pulse.Phase -in @('Starting','Publishing')){return 'Starting'}
        return 'Healthy'
    }catch{return 'Unknown'}
}
function Get-PolicyObservation {
    try {
        $session=Read-SmallComponentState (Join-Path $runtime 'session.json')
        if(-not $session.running -or (Test-Path -LiteralPath (Join-Path $runtime 'stop.request'))){return @{state='Stopped'}}
        $path=Join-Path $runtime 'policy-process.json'
        if(-not(Test-Path -LiteralPath $path)){return @{state='Missing'}}
        $owner=Read-SmallComponentState $path
        $process=Get-Process -Id $owner.Pid -ErrorAction SilentlyContinue
        if(-not $process){return @{state='Missing'}}
        $expected=Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe'
        if($owner.Path -ne $expected -or $process.Path -ne $expected -or
            [Math]::Abs(($process.StartTime.ToUniversalTime()-([datetime]$owner.StartedUtc).ToUniversalTime()).TotalMilliseconds) -ge 100){return @{state='Unknown'}}
        if(-not(Test-ComponentLiveness)){return @{state='Healthy'}} # Old-agent rollback compatibility.
        if($owner.SessionId -ne $session.id){return @{state='Unknown'}}
        $pulse=$null;$heartbeat=Join-Path $runtime 'policy-heartbeat.json'
        if(Test-Path -LiteralPath $heartbeat){$pulse=Read-SmallComponentState $heartbeat}
        return @{state=(Get-PolicyPulseState $pulse $owner $session.id ([DateTime]::UtcNow))}
    }catch{return @{state='Unknown'}}
}
function Test-SessionVpnHold($Session) {
    if($Session.vpnHoldReason){return $true}
    if($Session.id -and $script:unsafeSessionHold -eq $Session.id){return $true}
    $path=Join-Path $runtime 'bridge-safety.json'
    if(-not(Test-Path -LiteralPath $path)){return $false}
    try{
        $safety=Read-SmallComponentState $path
        if($safety.Schema -ne 1 -or -not $safety.SessionId -or $safety.Hold -ne $true){return $true}
        return [bool]($safety.SessionId -eq $Session.id)
    }catch{return $true}
}
function Invoke-EmergencyBridgePause($Session,[switch]$PersistHold) {
    if(-not(Test-ComponentLiveness)){Update-WireSockBridge $false $Session.id;return}
    if(-not(Test-RecoverySession $Session.id)){return}
    $identity=$Session.id+'|'+$Session.bridgeGeneration+'|'+$Session.bridgePid+'|'+$Session.bridgeStartedUtc
    if(-not $PersistHold -and $script:lastPausedBridge -eq $identity){return}
    $agent=Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe'
    $arguments='--pause-unsafe-bridge "'+$root.TrimEnd('\')+'" '+$Session.id+' '+[int]$Session.bridgeGeneration
    try {
        $result=Read-HelperOutput $agent $arguments | ConvertFrom-Json
        if($result.HoldPersisted -eq $false){$script:unsafeSessionHold=$Session.id}
        if($result.Code -eq 'BridgePaused'){$script:lastPausedBridge=$identity}
        if($result.Code -notin @('BridgePaused','VpnConfirmed','StaleSession')){
            [IO.File]::WriteAllText((Join-Path $root 'logs\recovery-error.log'),'VPN bridge pause could not be confirmed; check component state.')
        }
    }catch{[IO.File]::WriteAllText((Join-Path $root 'logs\recovery-error.log'),'VPN bridge pause could not be confirmed; check component state.')}
}
function Repair-PolicyController([string]$ExpectedId) {
    # Caller owns the ordinary coordinator and network-control.lock.
    if(-not(Test-RecoverySession $ExpectedId)){return $false}
    $observation=Get-PolicyObservation
    if($observation.state -eq 'Missing'){Start-PolicyController;return $true}
    if($observation.state -ne 'Stale'){return $false}
    $agent=Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe'
    $result=Read-HelperOutput $agent ('--stop-stale-policy "'+$root.TrimEnd('\')+'" '+$ExpectedId) | ConvertFrom-Json
    if($result.Code -in @('ProgressCurrent','StaleSession')){return $false}
    if($result.Code -ne 'PolicyStopped'){throw 'Policy termination could not be confirmed.'}
    if((Test-RecoverySession $ExpectedId) -and (Get-PolicyObservation).state -eq 'Missing'){Start-PolicyController}
    return $true
}
