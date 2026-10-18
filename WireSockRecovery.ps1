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

function Invoke-BridgeUpdate([bool]$Connected) {
    $session=Read-JsonFile (Join-Path $runtime 'session.json')
    $bridgeProcesses = @(Get-SessionBridge $session)
    $lostPath = Join-Path $root 'logs\connection-lost.txt'
    if (-not $Connected) {
        Set-BridgeSession $false
        $bridgeProcesses | Stop-Process -Force
        if (-not (Test-Path -LiteralPath $lostPath)) {
            [IO.File]::WriteAllText($lostPath,
                'WireSock is reconnecting. VPN bridge is paused; direct traffic remains available. The bridge will resume automatically.')
        }
        return
    }
    if ($bridgeProcesses.Count) {
        Set-BridgeSession $true $bridgeProcesses[0].Id
        Remove-Item -LiteralPath $lostPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $root 'logs\recovery-error.log') -Force -ErrorAction SilentlyContinue
        return
    }
    Set-BridgeSession $false
    $restartedBridge = Start-Helper $bridgeExe (Join-Path $runtime 'bridge.json')
    Set-BridgeSession $true $restartedBridge.Id $restartedBridge
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
        Write-JsonFile $sessionPath $session
        $statusText = (Invoke-WireSock @('status')) -join ' '
        if ($statusText -match [regex]::Escape($smartProfile)) { Invoke-WireSock @('disconnect') | Out-Null }
        [IO.File]::WriteAllText((Join-Path $root 'logs\connection-lost.txt'),
            'The router exited unexpectedly. Its VPN bridge and managed WireSock connection were stopped. Connect again to restart routing.')
        return $true
    } finally { $recoveryLock.Dispose() }
}
