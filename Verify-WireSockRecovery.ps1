$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WireSockCli.ps1')
. (Join-Path $PSScriptRoot 'WireSockRecovery.ps1')
. (Join-Path $PSScriptRoot 'FileStorage.ps1')
function Assert-Recovery($Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
$script:mockDisconnectCode = 1
$script:mockStatusCode = 0
$script:mockStatus = 'Not connected'
$script:disconnectCalls = 0
$cli = {
    param([string]$Command)
    if ($Command -eq 'disconnect') { $script:disconnectCalls++; $global:LASTEXITCODE = $script:mockDisconnectCode; 'Disconnect completed' }
    elseif ($Command -eq 'status') { $global:LASTEXITCODE = $script:mockStatusCode; $script:mockStatus }
    else { throw 'Unexpected CLI operation' }
}
Invoke-WireSock @('disconnect') | Out-Null
$script:mockStatus = 'Connected to SmartRouting-AWG2'
$disconnectRejected = $false
try { Invoke-WireSock @('disconnect') | Out-Null } catch { $disconnectRejected = $true }
Assert-Recovery $disconnectRejected 'A real disconnect failure was hidden.'
$script:mockStatusCode = 1
$script:mockStatus = 'Not connected'
$statusFailureRejected = $false
try { Invoke-WireSock @('disconnect') | Out-Null } catch { $statusFailureRejected = $true }
Assert-Recovery $statusFailureRejected 'An unsuccessful status query was accepted.'
$script:mockStatusCode = 0
$script:mockStatus = -join ([char[]]@(0x041d,0x0435,0x0442,0x0020,0x043f,0x043e,0x0434,0x043a,0x043b,0x044e,0x0447,0x0435,0x043d,0x0438,0x044f))
Invoke-WireSock @('disconnect') | Out-Null

$testRuntimeBase = Join-Path $PSScriptRoot 'runtime'
$root = Join-Path $testRuntimeBase ('recovery-tests-' + [guid]::NewGuid().ToString('N'))
$runtime = Join-Path $root 'runtime'
New-Item -ItemType Directory -Path $runtime,(Join-Path $root 'logs') -Force | Out-Null
$bridgeExe = Join-Path $runtime 'vpn-bridge.exe'
$routerExe = Join-Path $runtime 'smart-router.exe'
$script:bridgeRunning = $true
$script:routerRunning = $true
$smartProfile = 'SmartRouting-AWG2'
function Invoke-AgentCoordinator($Command) {}
function Get-WireSockObservation {[pscustomobject]@{state='Connected'}}
$script:bridgeStarts = 0
$script:bridgeStops = 0
$script:mockBridgeId = 20
$script:failBridgeStart = $false
function Get-OurProcesses {
    if ($script:routerRunning) { [pscustomobject]@{Id=10;Path=$routerExe} }
    if ($script:bridgeRunning) { [pscustomobject]@{Id=$script:mockBridgeId;Path=$bridgeExe} }
}
function Stop-Process {
    param([Parameter(ValueFromPipeline=$true)]$InputObject,[switch]$Force)
    process {
        Assert-Recovery ($InputObject.Path -eq $bridgeExe) 'Recovery stopped the main router.'
        $script:bridgeRunning = $false
        $script:bridgeStops++
    }
}
function Start-Helper($Exe,$Config) {
    Assert-Recovery ($Exe -eq $bridgeExe) 'Recovery restarted the wrong process.'
    if ($script:failBridgeStart) { throw 'Simulated helper failure' }
    $script:bridgeRunning = $true
    $script:bridgeStarts++
    $script:mockBridgeId = 30
    return [pscustomobject]@{Id=30}
}
try {
    Write-JsonFile (Join-Path $runtime 'session.json') @{id='unchanged-session';running=$true;bridgePid=20}
    Update-WireSockBridge $true
    Assert-Recovery ($script:bridgeStarts -eq 0 -and $script:bridgeStops -eq 0) 'Healthy connection was interrupted.'
    Update-WireSockBridge $false
    Update-WireSockBridge $false
    Assert-Recovery (-not $script:bridgeRunning -and $script:bridgeStops -eq 1) 'Disconnected bridge was not paused safely.'
    $paused = [IO.File]::ReadAllText((Join-Path $runtime 'session.json')) | ConvertFrom-Json
    Assert-Recovery (-not $paused.bridgeReady) 'Disconnected bridge was reported as ready.'
    $script:failBridgeStart = $true
    $restartFailed = $false
    try { Update-WireSockBridge $true } catch { $restartFailed = $true }
    Assert-Recovery $restartFailed 'Helper startup failure was hidden.'
    $failed = [IO.File]::ReadAllText((Join-Path $runtime 'session.json')) | ConvertFrom-Json
    Assert-Recovery (-not $failed.bridgeReady) 'Failed helper startup was reported as ready.'
    $script:failBridgeStart = $false
    Update-WireSockBridge $true
    Update-WireSockBridge $true
    Assert-Recovery ($script:bridgeRunning -and $script:bridgeStarts -eq 1) 'Reconnected bridge did not resume exactly once.'
    $session = [IO.File]::ReadAllText((Join-Path $runtime 'session.json')) | ConvertFrom-Json
    Assert-Recovery ($session.id -eq 'unchanged-session' -and $session.bridgePid -eq 30 -and $session.bridgeReady) 'Recovery reset traffic history or retained an obsolete PID/status.'
    Assert-Recovery (-not (Test-Path -LiteralPath (Join-Path $root 'logs\connection-lost.txt'))) 'Recovery retained an obsolete disconnect warning.'
    [IO.File]::WriteAllText((Join-Path $root 'logs\connection-lost.txt'),'stale warning')
    Update-WireSockBridge $true
    Assert-Recovery (-not (Test-Path -LiteralPath (Join-Path $root 'logs\connection-lost.txt'))) 'Healthy bridge retained a warning from a previous session.'
    Write-JsonFile (Join-Path $runtime 'session.json') @{id='current-session';running=$true;routerPid=10;bridgePid=30;bridgeReady=$true}
    $beforeStaleWatch=(Get-FileHash -LiteralPath (Join-Path $runtime 'session.json')).Hash
    Update-WireSockBridge $false 'old-session'
    Assert-Recovery ($script:bridgeRunning -and $beforeStaleWatch -eq (Get-FileHash -LiteralPath (Join-Path $runtime 'session.json')).Hash) 'A stale reconnect callback modified a new session.'
    $held=[IO.File]::Open((Join-Path $runtime 'network-control.lock'),'OpenOrCreate','ReadWrite','None')
    try { Update-WireSockBridge $false 'current-session' } finally { $held.Dispose() }
    Assert-Recovery $script:bridgeRunning 'A watcher changed the bridge while Start/Stop held the control lock.'
    [void](Stop-LostRouterSession 'old-session')
    Assert-Recovery $script:bridgeRunning 'An old watcher shut down a new session.'
    Assert-Recovery (-not (Stop-LostRouterSession 'current-session')) 'A live router was treated as crashed.'
    $script:routerRunning = $false
    $script:mockDisconnectCode = 0
    $script:mockStatus = 'Connected to SmartRouting-AWG2'
    $beforeCrashDisconnect = $script:disconnectCalls
    Assert-Recovery (Stop-LostRouterSession 'current-session') 'A crashed router was not cleaned up.'
    $stopped = [IO.File]::ReadAllText((Join-Path $runtime 'session.json')) | ConvertFrom-Json
    Assert-Recovery (-not $script:bridgeRunning -and -not $stopped.running -and -not $stopped.bridgeReady) 'Crash cleanup retained an orphaned bridge or active session.'
    Assert-Recovery ($script:disconnectCalls -eq $beforeCrashDisconnect + 1) 'Crash cleanup did not disconnect the managed profile.'
    $script:mockStatus = 'Connected to MAIN'
    Write-JsonFile (Join-Path $runtime 'session.json') @{id='another-session';running=$true;routerPid=10;bridgeReady=$false}
    [void](Stop-LostRouterSession 'another-session')
    Assert-Recovery ($script:disconnectCalls -eq $beforeCrashDisconnect + 1) 'Crash cleanup disconnected an unrelated profile.'
    Write-Output 'PASS: disconnect postcondition, genuine failure detection, localized status, bridge pause/resume, failed restart retry, accurate readiness, no duplicate restart, session counters preserved, crash cleanup, old-watcher isolation.'
} finally {
    $resolvedTestFolder = [IO.Path]::GetFullPath($root)
    $allowedPrefix = [IO.Path]::GetFullPath($testRuntimeBase).TrimEnd('\') + '\recovery-tests-'
    if (-not $resolvedTestFolder.StartsWith($allowedPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolvedTestFolder -Recurse -Force
}
