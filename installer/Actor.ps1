param([Parameter(Mandatory)][string]$RunRoot,[Parameter(Mandatory)][string]$Role)
$ErrorActionPreference='Stop'
. "$PSScriptRoot\NativeUpgradeWorker.ps1"
$script:upgradeCtx=$null
$diagnosticStage='Admission'
function Invoke-ActorCoordinator([string]$Command,$Proof) {
    if($Command -cnotin @('native-status','begin-operation','end-operation')){throw 'Actor coordinator scope refused.'}
    $ctx=$script:upgradeCtx;$null=Assert-NativeUpgradeWorkerWriter $ctx
    $state=Get-NativeUpgradeServiceSnapshot $ctx.Lease
    if($state.State -cne 'Running' -or $state.ProcessId -ne $Proof.ProcessId){throw 'Actor coordinator service changed.'}
    $p=Get-Process -Id $Proof.ProcessId -ErrorAction Stop
    try{
        $null=$p.Handle
        if($p.HasExited -or $p.StartTime.ToUniversalTime().Ticks -ne $Proof.StartedUtcTicks -or $p.Path -ine (Join-Path $ctx.Lease.Root 'RoutePilot.Agent.exe')){throw 'Actor coordinator process changed.'}
        Initialize-NativeUpgradePipe
        $reply=[Cita.NativeUpgradePipe]::Exchange((@{Command=$Command}|ConvertTo-Json -Compress),$Proof.ProcessId)|ConvertFrom-Json
        if($p.HasExited -or (Get-NativeUpgradeServiceSnapshot $ctx.Lease).ProcessId -ne $Proof.ProcessId -or $reply.Success -isnot [bool] -or !$reply.Success){throw 'Actor coordination refused.'}
        return $reply
    }finally{$p.Dispose()}
}
try {
    $ctx=$script:upgradeCtx=Open-NativeUpgradeWorker $RunRoot $Role $false
    if([IO.File]::Exists((Join-Path $ctx.Lease.Root 'native-upgrade.pending'))){throw 'An earlier native upgrade needs recovery.'}
    $current=Get-NativeUpgradeServiceSnapshot $ctx.Lease
    if($current.State -cne 'Running' -or $current.ProcessId -ne $ctx.Input.OriginalService.ProcessId -or $current.StartType -cne $ctx.Input.OriginalStartType -or (Get-NativeUpgradeHash (Join-Path $ctx.Lease.Root 'RoutePilot.Agent.exe')) -cne $ctx.Input.OldAgentSha){throw 'Initial native service differs.'}
    $original=[pscustomobject]@{Id=$ctx.Id;AgentSha=$ctx.Input.OldAgentSha;ProcessId=$current.ProcessId;StartedUtcTicks=[long]$ctx.Input.OriginalService.StartedUtcTicks}
    $diagnosticStage='InitialStatus';$reply=Invoke-ActorCoordinator 'native-status' $original
    Assert-NativeUpgradeReadyReply $reply $ctx.Input.Operation $ctx.Input.Generation $ctx.Input.ExpectedPhase
    $null=Invoke-ActorCoordinator 'begin-operation' $original;$ctx.Coordinated=$true
    # Retire only a completed predecessor before preparing the next operation.
    # A crash during preparation then has no foreign active journal to mistake
    # for this run. The full historical journal is retained without overwrite.
    $journalPath=Join-Path $ctx.Lease.Root 'native-upgrade.json'
    if([IO.File]::Exists($journalPath)){
        $prior=Read-NativeUpgradeJournal $ctx.Lease
        if($prior.phase -cnotin @('Committed','RolledBack') -or $prior.id -ceq $ctx.Id){throw 'Previous native upgrade requires recovery.'}
        $history=Join-Path $ctx.Lease.Root ('native-upgrade-'+$prior.id+'.json');Assert-NativeUpgradePath $history
        [IO.File]::Move($journalPath,$history)
        Save-NativeUpgradeWorkerJson $ctx 'predecessor-retired.json' @{Run=$ctx.Id;Previous=$prior.id}
    }
    $baseline=Convert-NativeUpgradeEntries @($ctx.Input.OldNative)
    $actualInventory=Get-NativeUpgradeInventory $ctx.Lease.Root
    if((ConvertTo-Json -InputObject @($actualInventory) -Depth 6 -Compress) -cne (ConvertTo-Json -InputObject @($baseline) -Depth 6 -Compress)){throw 'Initial native inventory differs.'}
    $nativeStage=Join-Path $ctx.Lease.Root ('native-stage-'+$ctx.Id)
    $agentStage=Join-Path $ctx.Lease.Root ('native-upgrade-'+$ctx.Id+'-agent-stage.exe')
    if((Test-Path -LiteralPath $nativeStage) -or (Test-Path -LiteralPath $agentStage)){throw 'Owned native staging path is occupied.'}
    Save-NativeUpgradeWorkerJson $ctx 'preparing.json' @{Run=$ctx.Id;StageWasAbsent=$true;AgentStageWasAbsent=$true}
    $incoming=Join-Path $ctx.Root 'Incoming.Agent.exe';$zip=Join-Path $ctx.Root 'native-components.zip'
    if((Get-NativeUpgradeHash $incoming) -cne $ctx.Input.NewAgentSha -or (Get-NativeUpgradeHash $zip) -cne $ctx.Input.ArchiveSha){throw 'Incoming upgrade inputs differ.'}
    $diagnosticStage='PrepareComponents'
    $prepared=& "$PSScriptRoot\Install-NativeComponents.ps1" -Package $zip -Sha256 $ctx.Input.ArchiveSha -PrepareOnly -TransactionId $ctx.Id
    if($prepared.stage -ine $nativeStage -or $prepared.transactionId -cne $ctx.Id -or $prepared.archiveSha -cne $ctx.Input.ArchiveSha){throw 'Native preparation receipt differs.'}
    $source=[IO.File]::Open($incoming,'Open','Read','Read');$target=$null
    try{$target=[IO.File]::Open($agentStage,'CreateNew','Write','None');$source.CopyTo($target);$target.Flush($true)}finally{if($target){$target.Dispose()};$source.Dispose()}
    if((Get-NativeUpgradeHash $agentStage) -cne $ctx.Input.NewAgentSha){throw 'Incoming agent staging differs.'}
    $entries=Get-NativeUpgradeInventoryFromDirectory $nativeStage
    $expected=if($prepared.configured){'Disconnected'}else{'Unconfigured'}
    if($expected -cne $ctx.Input.ExpectedPhase){throw 'Native profile configuration unexpectedly changed.'}
    $diagnosticStage='Arm';$null=Start-NativeUpgrade $ctx.Lease $ctx.Id $ctx.Input.NewAgentSha $ctx.Input.ArchiveSha $ctx.Plan.OwnerSid $ctx.Input.OriginalStartType $entries
    $assert={Assert-NativeUpgradeWorkerWriter $script:upgradeCtx}
    $barrier={Wait-NativeUpgradeWorkerLeaseBarrier $script:upgradeCtx}
    $restore={Restore-NativeUpgradeWorkerStartupType $script:upgradeCtx $script:upgradeCtx.Input.OriginalStartType}
    $persist={param($proof) $script:upgradeCtx.Proof=$proof;Save-NativeUpgradeWorkerJson $script:upgradeCtx 'started-service.json' $proof;return $true}
    $verify={param($j,$hash,$inventory)
        $c=$script:upgradeCtx;$null=Assert-NativeUpgradeWorkerWriter $c
        $status=Invoke-NativeUpgradeServiceRpc $c.Lease $c.Proof 'native-maintenance-status' $c.Id
        Assert-NativeUpgradeReadyReply $status $c.Input.Operation $c.Input.Generation $c.Input.ExpectedPhase
        return ((Get-NativeUpgradeServiceSnapshot $c.Lease).StartType -ceq $c.Input.OriginalStartType)
    }
    $diagnosticStage='ReplaceAndValidate';$outcome=Invoke-NativeUpgradeForward $ctx.Lease $ctx.Id $assert $barrier $restore $verify $ctx.Input.Operation $ctx.Input.Generation $ctx.Input.ExpectedPhase $persist
    $null=Invoke-NativeUpgradeServiceRpc $ctx.Lease $ctx.Proof 'native-capabilities' $ctx.Id
    $null=Invoke-ActorCoordinator 'end-operation' $ctx.Proof;$ctx.Coordinated=$false
    Save-NativeUpgradeWorkerJson $ctx 'actor-complete.json' @{Run=$ctx.Id;Success=$true;Phase=$outcome.phase}
    exit 0
}catch{
    $failure=@{Stage=$diagnosticStage;ErrorType=$_.Exception.GetType().Name;HResult=$_.Exception.HResult;Line=$_.InvocationInfo.ScriptLineNumber;Script=[IO.Path]::GetFileName($_.InvocationInfo.ScriptName);Command=$_.InvocationInfo.MyCommand.Name}
    $failureContext=$script:upgradeCtx
    if(!$failureContext){$p=[IO.Path]::GetFullPath($RunRoot).TrimEnd('\');if([IO.Path]::GetDirectoryName($p) -ieq (Join-Path $env:ProgramFiles 'RoutePilot\Agent') -and [IO.Path]::GetFileName($p) -cmatch '^native-update-[a-f0-9]{32}$'){$failureContext=[pscustomobject]@{Root=$p;Id=[IO.Path]::GetFileName($p).Substring(14)}}}
    if($failureContext){$failure.Run=$failureContext.Id;try{Save-NativeUpgradeWorkerJson $failureContext 'actor-failure.json' $failure}catch{}}
    exit 1
}finally{Close-NativeUpgradeWorker $script:upgradeCtx}
