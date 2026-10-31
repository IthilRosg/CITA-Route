[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunRoot,[Parameter(Mandatory)][ValidateSet('primary','backup','recover-on-boot')][string]$Role)
$ErrorActionPreference='Stop'
if($Role -cnotin @('primary','backup','recover-on-boot')){throw 'Recovery role refused.'}
function Get-NativeRecoveryProtocol($Reply) {
 if(!$Reply -or $Reply.Success -isnot [bool] -or !$Reply.Success -or !$Reply.Native){throw 'Recovery capabilities refused.'}
 $gate=$Reply.Native.maintenanceGateProtocol;$validation=$Reply.Native.maintenanceValidationProtocol
 if($null -eq $gate -and $null -eq $validation){return 'Normal'}
 if(($gate -isnot [int] -and $gate -isnot [long]) -or ($validation -isnot [int] -and $validation -isnot [long]) -or $gate -ne 1 -or $validation -ne 1){throw 'Recovery maintenance capabilities differ.'}
 return 'Maintenance'
}
function Get-NativeRecoveryStatusCommand([string]$Mode) {
 if($Mode -ceq 'Normal'){return 'native-status'}
 if($Mode -ceq 'Maintenance'){return 'native-maintenance-status'}
 throw 'Recovery status protocol unavailable.'
}
$ctx=$null
try {
 . (Join-Path $PSScriptRoot 'NativeUpgradeWorker.ps1')
 $ctx=Open-NativeUpgradeWorker $RunRoot $Role $true
 $recoveryState=[pscustomobject]@{Started=$null;Mode=$null}
 $writer={return (Assert-NativeUpgradeWorkerWriter $ctx)}
 $barrier={return (Wait-NativeUpgradeWorkerLeaseBarrier $ctx)}
 $startOld={
  param($lease,$journal,$retired)
  $writerProof=Assert-NativeUpgradeWorkerWriter $ctx
  if($writerProof -isnot [bool] -or !$writerProof -or !(Test-NativeUpgradeStoppedFence $lease)){throw 'Recovery service fence refused.'}
  $agent=Join-Path $lease.Root 'RoutePilot.Agent.exe';Assert-NativeUpgradePath $agent
  $image=[IO.FileStream]::new($agent,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);$process=$null
  try {
   if((Get-NativeUpgradeHash $agent) -cne $journal.oldAgentSha){throw 'Recovery old image differs.'}
   $native=Get-NativeUpgradeInventory $lease.Root
   if((ConvertTo-Json -InputObject @($native) -Depth 6 -Compress) -cne (ConvertTo-Json -InputObject @($journal.oldNative) -Depth 6 -Compress)){throw 'Recovery old components differ.'}
   $writerProof=Assert-NativeUpgradeWorkerWriter $ctx;if($writerProof -isnot [bool] -or !$writerProof){throw 'Recovery writer changed.'}
   Set-Service -Name RoutePilotRescue -StartupType Manual -ErrorAction Stop
   $stopped=Get-NativeUpgradeServiceSnapshot $lease
   if($stopped.State -cne 'Stopped' -or $stopped.ProcessId -ne 0 -or $stopped.StartType -cne 'Manual'){throw 'Recovery start fence differs.'}
   Start-Service -Name RoutePilotRescue -ErrorAction Stop
   (Get-Service -Name RoutePilotRescue -ErrorAction Stop).WaitForStatus('Running',[TimeSpan]::FromSeconds(20))
   $state=Get-NativeUpgradeServiceSnapshot $lease
   if($state.State -cne 'Running' -or $state.ProcessId -le 0){throw 'Recovery service process missing.'}
   $process=Get-Process -Id $state.ProcessId -ErrorAction Stop;$null=$process.Handle
   if($process.HasExited -or $process.Path -ine $agent){throw 'Recovery service process differs.'}
   $proof=[pscustomobject]@{Id=$journal.id;AgentSha=$journal.oldAgentSha;ProcessId=$state.ProcessId;StartedUtcTicks=$process.StartTime.ToUniversalTime().Ticks}
   $caps=Invoke-NativeUpgradeServiceRpc $lease $proof 'native-capabilities' $journal.id
   $mode=Get-NativeRecoveryProtocol $caps
   if($mode -ceq 'Maintenance'){$null=Invoke-NativeUpgradeServiceRpc $lease $proof 'native-maintenance-validate' $journal.id}
   # A later recovery process must be able to identify the exact service
   # started here even if this worker stops before its terminal witness.
   # The RolledBack/no-marker pre-witness case still needs a separate read-only
   # terminal reconciliation path; it is not claimed as recovered here.
   Save-NativeUpgradeWorkerJson $ctx 'restored-service.json' @{Id=$proof.Id;AgentSha=$proof.AgentSha;ProcessId=$proof.ProcessId;StartedUtcTicks=$proof.StartedUtcTicks}
   $recoveryState.Started=$proof;$recoveryState.Mode=$mode
   return $proof
  } finally {if($process){$process.Dispose()};$image.Dispose()}
 }
 $validateOld={
  param($lease,$proof,$id)
  $command=Get-NativeRecoveryStatusCommand $recoveryState.Mode
  if($command -ceq 'native-status'){return (Invoke-NativeUpgradeServiceRpc $lease $proof $command $id -AllowNativeStartupPending)}
  return (Invoke-NativeUpgradeServiceRpc $lease $proof $command $id)
 }
 $restoreStartup={param($startType) return (Restore-NativeUpgradeWorkerStartupType $ctx $startType)}
 $verifyOld={
  param($journal,$agentSha,$native)
  if($journal.id -cne $ctx.Id -or $agentSha -cne $ctx.Input.OldAgentSha -or
     (ConvertTo-Json -InputObject @($native) -Depth 6 -Compress) -cne (ConvertTo-Json -InputObject @($ctx.Input.OldNative) -Depth 6 -Compress)){return $false}
  $state=Get-NativeUpgradeServiceSnapshot $ctx.Lease
  $proof=$recoveryState.Started
  if(!$proof -or $state.State -cne 'Running' -or $state.ProcessId -ne $proof.ProcessId -or $state.StartType -cne $ctx.Input.OriginalStartType){return $false}
  $oldAgent=Join-Path $ctx.Lease.Root 'RoutePilot.Agent.exe';$image=[IO.FileStream]::new($oldAgent,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);$process=$null
  try {
   if((Get-NativeUpgradeHash $oldAgent) -cne $ctx.Input.OldAgentSha -or $state.ProcessId -le 0){return $false}
   $process=Get-Process -Id $state.ProcessId -ErrorAction Stop;$null=$process.Handle
   if($process.HasExited -or $process.Path -ine $oldAgent -or $process.StartTime.ToUniversalTime().Ticks -ne $proof.StartedUtcTicks){return $false}
   $command=Get-NativeRecoveryStatusCommand $recoveryState.Mode
   $reply=Invoke-NativeUpgradeServiceRpc $ctx.Lease $proof $command $ctx.Id
   Assert-NativeUpgradeReadyReply $reply $ctx.Input.Operation $ctx.Input.Generation $ctx.Input.ExpectedPhase
   return $true
  }finally{if($process){$process.Dispose()};$image.Dispose()}
 }
 $getCommittedProof={return (Read-NativeUpgradeWorkerJson $ctx 'started-service.json' 65536)}
 $journalPath=Join-Path $ctx.Lease.Root 'native-upgrade.json'
 if(![IO.File]::Exists($journalPath)){
  $baseline={
   $writerProof=Assert-NativeUpgradeWorkerWriter $ctx;if($writerProof -isnot [bool] -or !$writerProof){return $false}
   $agent=Join-Path $ctx.Lease.Root 'RoutePilot.Agent.exe';Assert-NativeUpgradePath $agent
   if((Get-NativeUpgradeHash $agent) -cne $ctx.Input.OldAgentSha){return $false}
   $native=Get-NativeUpgradeInventory $ctx.Lease.Root
   if((ConvertTo-Json -InputObject @($native) -Depth 6 -Compress) -cne (ConvertTo-Json -InputObject @($ctx.Input.OldNative) -Depth 6 -Compress)){return $false}
   $state=Get-NativeUpgradeServiceSnapshot $ctx.Lease
   if($state.State -cne 'Running' -or $state.ProcessId -le 0 -or $state.StartType -cne $ctx.Input.OriginalStartType){return $false}
   $image=[IO.FileStream]::new($agent,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);$process=$null
   try {
    if((Get-NativeUpgradeHash $agent) -cne $ctx.Input.OldAgentSha){return $false}
    $process=Get-Process -Id $state.ProcessId -ErrorAction Stop;$null=$process.Handle
    if($process.HasExited -or $process.Path -ine $agent){return $false}
    $proof=[pscustomobject]@{Id=$ctx.Id;AgentSha=$ctx.Input.OldAgentSha;ProcessId=$state.ProcessId;StartedUtcTicks=$process.StartTime.ToUniversalTime().Ticks}
    $reply=Invoke-NativeUpgradeServiceRpc $ctx.Lease $proof 'native-status' $ctx.Id
    Assert-NativeUpgradeReadyReply $reply $ctx.Input.Operation $ctx.Input.Generation $ctx.Input.ExpectedPhase
    return $true
   }finally{if($process){$process.Dispose()};$image.Dispose()}
  }
  $result=Assert-NativeUpgradeMissingJournal $ctx.Lease $ctx.Id $baseline
 } else {
  $result=Invoke-NativeUpgradeRecovery $ctx.Lease $ctx.Id $writer $barrier $startOld $validateOld $restoreStartup $verifyOld $getCommittedProof $ctx.Input.ExpectedPhase
 }
 if(!$result -or $result.success -isnot [bool] -or !$result.success -or $result.id -cne $ctx.Id -or $result.outcome -cnotin @('RolledBack','CommittedReadOnly','NoJournalBaselineUnchanged')){throw 'Recovery outcome unconfirmed.'}
 if($result.outcome -ceq 'RolledBack' -and $recoveryState.Mode -ceq 'Maintenance'){
  # Dispatch observes the removed marker and releases the agent's validation
  # intent lease before normal routing can be requested again.
  $caps=Invoke-NativeUpgradeServiceRpc $ctx.Lease $recoveryState.Started 'native-capabilities' $ctx.Id
  if((Get-NativeRecoveryProtocol $caps) -cne 'Maintenance'){throw 'Recovered maintenance capability changed.'}
 }
 $writerProof=Assert-NativeUpgradeWorkerWriter $ctx;if($writerProof -isnot [bool] -or !$writerProof){throw 'Recovery writer changed before confirmation.'}
 Save-NativeUpgradeWorkerJson $ctx ('recovered-'+$Role+'.json') @{Run=$ctx.Id;Success=$true}
} catch {
 $failureType=$_.Exception.GetType().Name
 if($ctx){try{Save-NativeUpgradeWorkerJson $ctx ('worker-failure-'+$Role+'.json') @{Run=$ctx.Id;ErrorType=$failureType}}catch{}}
 exit 1
} finally {
 if($ctx){Close-NativeUpgradeWorker $ctx}
}
