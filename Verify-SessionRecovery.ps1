$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'FileStorage.ps1')
. (Join-Path $PSScriptRoot 'SessionRecovery.ps1')
$fixture=Join-Path $PSScriptRoot ('runtime\session-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$runtime=$fixture
try {
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
    if(Set-SessionRecovering 'stale' 'test'){throw 'A stale recovery was accepted.'}
    if((Read-JsonFile $path).recovering){throw 'A stale recovery modified the session.'}
    if(-not(Set-SessionRecovering 'current' 'test')){throw 'A current recovery was rejected.'}
    $state=Read-JsonFile $path
    if(-not $state.recovering -or $state.bridgeReady -or $state.mode -ne 'ProxyOnly'){throw 'Recovering state did not preserve session identity/mode.'}
    $self=Get-Process -Id $PID
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
    Write-Output 'PASS: capped recovery delays, stale generation rejection, recovering state preservation and controller PID/start-time/path identity.'
}finally {
    $prefix=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runtime\session-tests-'))
    if(-not [IO.Path]::GetFullPath($fixture).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture cleanup.'}
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
