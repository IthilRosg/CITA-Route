$ErrorActionPreference='Stop'
. "$PSScriptRoot\NativeUpgradeTransaction.ps1";. "$PSScriptRoot\NativeUpgradePair.ps1";. "$PSScriptRoot\NativeUpgradeFlow.ps1";. "$PSScriptRoot\NativeUpgradeRecovery.ps1"
# Model only SCM and authenticated RPC; pair/journal/intent use actual files.
function Stop-NativeRecoveryService($Lease,$Journal,[scriptblock]$AssertRetired){Assert-NativeRecoveryProof $AssertRetired 'writer';$script:stops++}
function Assert-NativeRecoveryStartedProof($Lease,$Journal,$Proof,[string]$ExpectedHash){if($Proof.Id -cne $Journal.id -or $Proof.AgentSha -cne $ExpectedHash -or $Proof.ProcessId -le 0 -or $Proof.StartedUtcTicks -le 0){throw 'Model started proof differs.'}}
function Test-NativeUpgradeStoppedFence($Lease){return $true}
$script:readyPhase='Disconnected'
$script:replyMode='normal'
function Invoke-NativeUpgradeServiceRpc($Lease,$Proof,[string]$Command,[string]$Id){
 $script:rpcs++
 if($Command -eq 'native-capabilities'){return [pscustomobject]@{Success=$true;Native=[pscustomobject]@{maintenanceGateProtocol=1;maintenanceValidationProtocol=1}}}
 if($script:replyMode -ceq 'null-first' -and $script:rpcs -eq 1){return [pscustomobject]@{Success=$false;Native=$null}}
 if($script:replyMode -ceq 'permanent'){return [pscustomobject]@{Success=$false;Native=[pscustomobject]@{phase='Unavailable';errorCode='Failure'}}}
 $phase=if($script:rpcs -eq 1){'Stopping'}else{$script:readyPhase};$op=if($script:badReply){'b'*32}else{'a'*32}
 return [pscustomobject]@{Success=$true;Native=[pscustomobject]@{phase=$phase;desired=$false;operation=$op;generation=7;errorCode=$null}}
}
function Reject([scriptblock]$action){$failed=$false;try{& $action}catch{$failed=$true};if(!$failed){throw 'Expected rejection.'}}
$base=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../runtime')).TrimEnd('\');$root=[IO.Path]::GetFullPath((Join-Path $base ('recovery-tests-'+[guid]::NewGuid().ToString('N'))))
if([IO.Path]::GetDirectoryName($root) -ine $base -or [IO.Path]::GetFileName($root) -cnotmatch '^recovery-tests-[a-f0-9]{32}$'){throw 'Test root refused.'};[void][IO.Directory]::CreateDirectory($root)
function Setup([string]$name){
 $dir=Join-Path $root $name;[void][IO.Directory]::CreateDirectory((Join-Path $dir 'native'));[IO.File]::WriteAllText((Join-Path $dir 'native\core.exe'),'old native');[IO.File]::WriteAllText((Join-Path $dir 'native\intent.json'),'{"Desired":false,"Operation":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","Generation":7}')
 [IO.File]::WriteAllText((Join-Path $dir 'RoutePilot.Agent.exe'),'old agent');$id=[guid]::NewGuid().ToString('N');$stage=New-NativePreparationStage $dir $id;[IO.File]::WriteAllText((Join-Path $stage 'core.exe'),'new native');[IO.File]::Copy((Join-Path $dir 'native\intent.json'),(Join-Path $stage 'intent.json'))
 $agent=Join-Path $dir ('native-upgrade-'+$id+'-agent-stage.exe');[IO.File]::WriteAllText($agent,'new agent');$lease=Open-NativeUpgradeLease $dir;$null=Start-NativeUpgrade $lease $id (Get-NativeUpgradeHash $agent) ('A'*64) 'S-1-5-18' 'Automatic' (Get-NativeUpgradeInventoryFromDirectory $stage)
 Set-NativeUpgradePhase $lease $id 'ServiceStopped';Start-NativePairPromotion $lease $id {$true};return [pscustomobject]@{Dir=$dir;Id=$id;Lease=$lease}
}
try{
 $script:transitionStates=[Collections.Generic.Queue[object]]::new()
 $script:transitionStates.Enqueue([pscustomobject]@{State='Stop Pending';ProcessId=42;StartType='Disabled'})
 $script:transitionStates.Enqueue([pscustomobject]@{State='Stopped';ProcessId=0;StartType='Disabled'})
 $modelReader={param($lease) $script:transitionStates.Dequeue()}
 $transition=Wait-NativeRecoveryTransition $null 42 'Stopping' 2 {$true} $modelReader
 if($transition.State -cne 'Stopped'){throw 'Pending stop did not reach stopped.'}
 $script:transitionStates.Enqueue([pscustomobject]@{State='Start Pending';ProcessId=42;StartType='Disabled'})
 $script:transitionStates.Enqueue([pscustomobject]@{State='Running';ProcessId=42;StartType='Disabled'})
 $transition=Wait-NativeRecoveryTransition $null 42 'Starting' 2 {$true} $modelReader
 if($transition.State -cne 'Running'){throw 'Pending start did not reach same running process.'}
 $script:transitionStates.Enqueue([pscustomobject]@{State='Stop Pending';ProcessId=43;StartType='Disabled'})
 Reject {Wait-NativeRecoveryTransition $null 42 'Stopping' 2 {$true} $modelReader}
 $script:transitionStates.Enqueue([pscustomobject]@{State='Running';ProcessId=43;StartType='Disabled'})
 Reject {Wait-NativeRecoveryTransition $null 42 'Starting' 2 {$true} $modelReader}
 $script:transitionStates.Enqueue([pscustomobject]@{State='Running';ProcessId=42;StartType='Disabled'})
 Reject {Wait-NativeRecoveryTransition $null 0 'Starting' 2 {$true} $modelReader}
 $script:transitionStates.Enqueue([pscustomobject]@{State='Start Pending';ProcessId=42;StartType='Manual'})
 Reject {Wait-NativeRecoveryTransition $null 42 'Starting' 2 {$true} $modelReader}
 $startOld={param($lease,$journal,$retired) Assert-NativeRecoveryProof $retired 'writer';$script:starts++;return [pscustomobject]@{Id=$journal.id;AgentSha=$journal.oldAgentSha;ProcessId=123;StartedUtcTicks=123}}
 $validateOld={param($lease,$proof,$id) Invoke-NativeUpgradeServiceRpc $lease $proof 'native-status' $id}
 $script:stops=0;$script:starts=0;$script:rpcs=0;$script:badReply=$false;$t=Setup 'rollback';try{
  $counter=[pscustomobject]@{count=0};$startup={param($startType) if($startType -cne 'Automatic' -or !(Test-Path -LiteralPath (Join-Path $t.Dir 'native-upgrade.pending'))){throw 'Startup restored out of order or wrong type.'};$counter.count++;return $true}
  $result=Invoke-NativeUpgradeRecovery $t.Lease $t.Id {$true} {$true} $startOld $validateOld $startup {param($j,$a,$n) $true} {throw 'Unused'}
  if(!$result.success -or $result.outcome -cne 'RolledBack' -or $script:stops -ne 1 -or $script:starts -ne 1 -or $script:rpcs -ne 2 -or $counter.count -ne 1 -or (Test-Path -LiteralPath (Join-Path $t.Dir 'native-upgrade.pending')) -or [IO.File]::ReadAllText((Join-Path $t.Dir 'native\core.exe')) -cne 'old native'){throw 'Model rollback failed.'}
 }finally{$t.Lease.Handle.Dispose()}
 $script:stops=0;$script:starts=0;$script:rpcs=0;$script:badReply=$false;$script:replyMode='null-first';$t=Setup 'startup-null';try{
  $result=Invoke-NativeUpgradeRecovery $t.Lease $t.Id {$true} {$true} $startOld $validateOld {$true} {param($j,$a,$n) $true} {throw 'Unused'}
  if(!$result.success -or $result.outcome -cne 'RolledBack' -or $script:rpcs -ne 2){throw 'Transient null startup status did not recover.'}
 }finally{$script:replyMode='normal';$t.Lease.Handle.Dispose()}
 $script:stops=0;$script:starts=0;$script:rpcs=0;$script:badReply=$false;$script:replyMode='permanent';$t=Setup 'startup-permanent';try{
  Reject {Invoke-NativeUpgradeRecovery $t.Lease $t.Id {$true} {$true} $startOld $validateOld {$true} {param($j,$a,$n) $true} {throw 'Unused'}}
  if($script:rpcs -ne 1 -or !(Test-Path -LiteralPath (Join-Path $t.Dir 'native-upgrade.pending'))){throw 'Permanent status was retried or marker removed.'}
 }finally{$script:replyMode='normal';$t.Lease.Handle.Dispose()}
 $script:stops=0;$script:starts=0;$script:rpcs=0;$script:badReply=$false;$script:readyPhase='Unconfigured';$t=Setup 'unconfigured';try{
  $result=Invoke-NativeUpgradeRecovery $t.Lease $t.Id {$true} {$true} $startOld $validateOld {$true} {param($j,$a,$n) $true} {throw 'Unused'} 'Unconfigured'
  if(!$result.success -or $result.outcome -cne 'RolledBack' -or $script:rpcs -ne 2 -or (Test-Path -LiteralPath (Join-Path $t.Dir 'native-upgrade.pending'))){throw 'Unconfigured recovery failed.'}
 }finally{$script:readyPhase='Disconnected';$t.Lease.Handle.Dispose()}
 $script:stops=0;$script:starts=0;$script:rpcs=0;$script:badReply=$true;$script:readyPhase='Unconfigured';$t=Setup 'unconfigured-wrong-op';try{
  Reject {Invoke-NativeUpgradeRecovery $t.Lease $t.Id {$true} {$true} $startOld $validateOld {$true} {return $true} {throw 'Unused'} 'Unconfigured'}
  if(!(Test-Path -LiteralPath (Join-Path $t.Dir 'native-upgrade.pending'))){throw 'Unconfigured wrong identity lost the marker.'}
 }finally{$script:badReply=$false;$script:readyPhase='Disconnected';$t.Lease.Handle.Dispose()}
 $script:stops=0;$script:rpcs=0;$t=Setup 'committed-with-marker';try{
  Set-NativeUpgradePhase $t.Lease $t.Id 'Verified';Set-NativeUpgradePhase $t.Lease $t.Id 'Committed'
  Reject {Invoke-NativeUpgradeRecovery $t.Lease $t.Id {$true} {$true} $startOld $validateOld {$true} {return $true} {throw 'Unused'}}
  if($script:stops -ne 0 -or $script:rpcs -ne 0 -or !(Test-Path -LiteralPath (Join-Path $t.Dir 'native-upgrade.pending'))){throw 'Committed marker branch mutated service or marker.'}
 }finally{$t.Lease.Handle.Dispose()}
 $script:stops=0;$t=Setup 'unretired';try{
  Reject {Invoke-NativeUpgradeRecovery $t.Lease $t.Id {@($true,$true)} {$true} $startOld $validateOld {$true} {return $true} {throw 'Unused'}}
  if($script:stops -ne 0 -or (Read-NativeUpgradeJournal $t.Lease).phase -cne 'NativePromoted'){throw 'Invalid retirement proof changed transaction.'}
 }finally{$t.Lease.Handle.Dispose()}
 $script:stops=0;$script:starts=0;$script:rpcs=0;$script:badReply=$true;$t=Setup 'bad-readiness';try{
  Reject {Invoke-NativeUpgradeRecovery $t.Lease $t.Id {$true} {$true} $startOld $validateOld {$true} {return $true} {throw 'Unused'}}
  if(!(Test-Path -LiteralPath (Join-Path $t.Dir 'native-upgrade.pending')) -or $script:stops -ne 2){throw 'Failed old readiness did not preserve marker and refence.'}
 }finally{$t.Lease.Handle.Dispose()}
 $script:stops=0;$script:starts=0;$script:rpcs=0;$script:badReply=$false;$t=Setup 'committed';try{
  Set-NativeUpgradePhase $t.Lease $t.Id 'Verified';Set-NativeUpgradePhase $t.Lease $t.Id 'Committed';Complete-NativeUpgrade $t.Lease $t.Id {param($j,$a,$n) $true}
  [IO.File]::WriteAllText((Join-Path $t.Dir 'native\mutable.json'),'user change')
  $j=Read-NativeUpgradeJournal $t.Lease;$proof=[pscustomobject]@{Id=$t.Id;AgentSha=$j.newAgentSha;ProcessId=123;StartedUtcTicks=456}
  $result=Invoke-NativeUpgradeRecovery $t.Lease $t.Id {$true} {$true} $startOld $validateOld {$true} {return $true} {$proof}
  if(!$result.success -or $result.outcome -cne 'CommittedReadOnly' -or $script:stops -ne 0 -or $script:starts -ne 0 -or [IO.File]::ReadAllText((Join-Path $t.Dir 'native\mutable.json')) -cne 'user change'){throw 'Committed branch changed user state.'}
 }finally{$t.Lease.Handle.Dispose()}
 $dir=Join-Path $root 'missing';[void][IO.Directory]::CreateDirectory($dir);$lease=Open-NativeUpgradeLease $dir;try{
  $result=Assert-NativeUpgradeMissingJournal $lease ('c'*32) {$true}
  if($result.outcome -cne 'NoJournalBaselineUnchanged'){throw 'Missing journal baseline outcome differs.'}
  Reject {Assert-NativeUpgradeMissingJournal $lease ('c'*32) {$false}}
 }finally{$lease.Handle.Dispose()}
 'PASS recovery model adapters; real pair/journal/intent files; no SCM or IPC'
}finally{if([IO.Directory]::Exists($root)){if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($root)) -ine $base -or [IO.Path]::GetFileName($root) -cnotmatch '^recovery-tests-[a-f0-9]{32}$'){throw 'Cleanup target refused.'};Assert-NativeUpgradePath $root;if(@(Get-ChildItem -LiteralPath $root -Force -Recurse|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Cleanup contains a link.'};[IO.Directory]::Delete($root,$true)}}
