# Source-only orchestration. Caller holds the transaction lease, durable
# recovery supervisor and exclusive writer fence throughout this operation.
# The helper never starts or stops a service except via the already-defined
# NativeUpgradeService functions; no paths or commands come from IPC.
function Assert-NativeUpgradeFlowCallback([scriptblock]$Callback,[string]$Failure) {
    if(!$Callback){throw $Failure}
    $value=& $Callback
    if($value -isnot [bool] -or !$value){throw $Failure}
}
function Get-NativeUpgradeFlowReadiness($Reply,[string]$Operation,[long]$Generation,[string]$ExpectedPhase) {
    if($Operation -cnotmatch '^[a-f0-9]{32}$' -or $Generation -lt 0 -or $ExpectedPhase -cnotin @('Disconnected','Unconfigured')){throw 'Native upgrade readiness identity refused.'}
    if(!$Reply -or $Reply.Success -isnot [bool] -or !$Reply.Success -or !$Reply.Native){throw 'Native upgrade readiness reply refused.'}
    $n=$Reply.Native
    if($n.desired -isnot [bool] -or $n.desired -or $n.operation -cne $Operation -or ($n.generation -isnot [int] -and $n.generation -isnot [long]) -or $n.generation -ne $Generation -or ($null -ne $n.errorCode -and ($n.errorCode -isnot [string] -or $n.errorCode -cne ''))){throw 'Native upgrade readiness identity or error differs.'}
    if($n.phase -ceq $ExpectedPhase){return 'Ready'}
    if($n.phase -ceq 'Stopping'){return 'Stopping'}
    throw 'Native upgrade readiness phase refused.'
}
function Wait-NativeUpgradeFlowReady($Lease,$Proof,[string]$Id,[string]$Operation,[long]$Generation,[string]$ExpectedPhase,[scriptblock]$AssertWriter) {
    $timer=[Diagnostics.Stopwatch]::StartNew();$command='native-maintenance-validate'
    while($timer.Elapsed.TotalSeconds -lt 33){
        Assert-NativeUpgradeWriter $AssertWriter
        $reply=Invoke-NativeUpgradeServiceRpc $Lease $Proof $command $Id
        if($timer.Elapsed.TotalSeconds -ge 45){throw 'Native upgrade readiness deadline exceeded.'}
        if((Get-NativeUpgradeFlowReadiness $reply $Operation $Generation $ExpectedPhase) -ceq 'Ready'){return}
        $command='native-maintenance-status';Start-Sleep -Milliseconds 200
    }
    throw 'Native upgrade readiness deadline exceeded.'
}
function Invoke-NativeUpgradeForward($Lease,[string]$Id,[scriptblock]$AssertWriter,[scriptblock]$WaitOldLeasesReleased,[scriptblock]$RestoreStartupType,[scriptblock]$VerifyActualPair,[string]$Operation,[long]$Generation,[string]$ExpectedPhase='Disconnected',[scriptblock]$StartedProofObserver=$null) {
    $ErrorActionPreference='Stop'
    foreach($callback in @($AssertWriter,$WaitOldLeasesReleased,$RestoreStartupType,$VerifyActualPair)){if(!$callback){throw 'Native upgrade flow callback missing.'}}
    if($Operation -cnotmatch '^[a-f0-9]{32}$' -or $Generation -lt 0 -or $ExpectedPhase -cnotin @('Disconnected','Unconfigured')){throw 'Native upgrade readiness identity refused.'}
    $j=Read-NativeUpgradeJournal $Lease
    if($j.id -cne $Id -or $j.phase -cne 'Prepared'){throw 'Native upgrade forward phase differs.'}
    Assert-NativeUpgradeWriter $AssertWriter
    Stop-NativeUpgradeService $Lease $Id $AssertWriter
    Assert-NativeUpgradeFlowCallback $WaitOldLeasesReleased 'Previous native leases remain held.'
    # This block executes synchronously inside the pair operation. PowerShell's
    # normal dynamic scope keeps the enclosing lease and writer visible without
    # moving the block into a module that cannot resolve the service functions.
    $fence={Assert-NativeUpgradeWriter $AssertWriter; $state=Test-NativeUpgradeStoppedFence $Lease; return ($state -is [bool] -and $state)}
    Start-NativePairPromotion $Lease $Id $fence
    $proof=Start-NativeUpgradeValidationService $Lease $Id $AssertWriter
    if($StartedProofObserver){$recorded=& $StartedProofObserver $proof;if($recorded -isnot [bool] -or !$recorded){throw 'Native upgrade started-service proof not persisted.'}}
    Wait-NativeUpgradeFlowReady $Lease $proof $Id $Operation $Generation $ExpectedPhase $AssertWriter
    Assert-NativeUpgradeWriter $AssertWriter
    Set-NativeUpgradePhase $Lease $Id 'Verified'
    Assert-NativeUpgradeFlowCallback $RestoreStartupType 'Native service startup type restoration unconfirmed.'
    Assert-NativeUpgradeWriter $AssertWriter
    Set-NativeUpgradePhase $Lease $Id 'Committed'
    Complete-NativeUpgrade $Lease $Id $VerifyActualPair
    return [pscustomobject]@{success=$true;phase='Committed';id=$Id}
}
# Recovery and old-agent boot fencing are delegated to the independent
# supervisor; this source-only forward flow never initiates rollback itself.
