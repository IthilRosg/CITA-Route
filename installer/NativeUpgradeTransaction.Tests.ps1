$ErrorActionPreference='Stop'
. "$PSScriptRoot\NativeUpgradeTransaction.ps1"
$root=Join-Path (Join-Path $PSScriptRoot '../runtime') ('cita-upgrade-test-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $root 'native'))
[IO.File]::WriteAllText((Join-Path $root 'RoutePilot.Agent.exe'),'old')
[IO.File]::WriteAllText((Join-Path $root 'native\a.bin'),'old native')
$id='a'*32;$other='b'*32;$hash='A'*64
function Reject([scriptblock]$run){$failed=$false;try{& $run}catch{$failed=$true};if(!$failed){throw 'Expected rejection.'}}
try {
 $lease=Open-NativeUpgradeLease $root
 try {
  Assert-NativePreparationState ([pscustomobject]@{Phase='Disconnected';Desired=$false})
  Reject {Assert-NativePreparationState ([pscustomobject]@{Phase='Disconnected';Desired=$true})}
  Reject {Assert-NativePreparationState ([pscustomobject]@{Phase='Connected';Desired=$false})}
  Reject {Assert-NativePreparationState ([pscustomobject]@{Phase='Disconnected';Desired='false'})}
  $stage=New-NativePreparationStage $lease.Root ('e'*32);[IO.File]::WriteAllText((Join-Path $stage 'component.bin'),'prepared')
  $stageInventory=Get-NativeUpgradeInventoryFromDirectory $stage
  if(@($stageInventory).Count -ne 1 -or $stageInventory[0].path -cne 'component.bin'){throw 'Stage inventory differs.'}
  Reject {New-NativePreparationStage $lease.Root ('e'*32)}
  if([IO.File]::ReadAllText((Join-Path $stage 'component.bin')) -cne 'prepared' -or [IO.File]::ReadAllText((Join-Path $root 'native\a.bin')) -cne 'old native'){throw 'Preparation modified existing contents.'}
  $expected=Get-NativeUpgradeInventory $lease.Root
  Reject {Start-NativeUpgrade $lease ('d'*32) $hash $hash 'S-1-5-18' 'Automatic' @()}
  Reject {Start-NativeUpgrade $lease ('d'*32) $hash $hash 'S-1-5-18' 'Automatic' @(@{path='a/../b';bytes=1;sha256=$hash})}
  $j=Start-NativeUpgrade $lease $id $hash $hash 'S-1-5-18' 'Automatic' $expected
  Reject {Open-NativeUpgradeLease $root}
  if($j.phase -ne 'Prepared' -or @($j.oldNative).Count -ne 1 -or [IO.File]::ReadAllText((Join-Path $root 'native-upgrade.pending')) -cne ($id+"`n")){throw 'Begin failed.'}
  Reject {Start-NativeUpgrade $lease $other $hash $hash 'S-1-5-18' 'Automatic' $expected}
  Reject {Set-NativeUpgradePhase $lease $id 'Committed'}
  Reject {Set-NativeUpgradePhase $lease $other 'ServiceStopped'}
  Set-NativeUpgradePhase $lease $id 'Restoring'
  Set-NativeUpgradePhase $lease $id 'RecoveryFailed'
  if(![IO.File]::Exists((Join-Path $root 'native-upgrade.pending'))){throw 'Recovery failure lost marker.'}
  Set-NativeUpgradePhase $lease $id 'Restoring'
  Set-NativeUpgradePhase $lease $id 'RolledBack'
  Reject {Complete-NativeUpgrade $lease $id {param($j,$a,$n) $false}}
  Reject {Complete-NativeUpgrade $lease $id {param($j,$a,$n) 'True'}}
  Reject {Complete-NativeUpgrade $lease $id {param($j,$a,$n) @($true,$true)}}
  if(![IO.File]::Exists((Join-Path $root 'native-upgrade.pending'))){throw 'Failed verification lost marker.'}
  Reject {Complete-NativeUpgrade $lease $id {param($j,$a,$n) [IO.File]::WriteAllText((Join-Path $j.root 'native\a.bin'),'tamper');$true}}
  if(![IO.File]::Exists((Join-Path $root 'native-upgrade.pending'))){throw 'Tamper lost marker.'}
  [IO.File]::WriteAllText((Join-Path $root 'native\a.bin'),'old native')
  Complete-NativeUpgrade $lease $id {param($j,$a,$n) $a -ceq $j.oldAgentSha -and $n[0].sha256 -ceq $j.oldNative[0].sha256}
  if([IO.File]::Exists((Join-Path $root 'native-upgrade.pending'))){throw 'Successful completion retained marker.'}
  $agentSha=Get-NativeUpgradeHash (Join-Path $root 'RoutePilot.Agent.exe')
  $j=Start-NativeUpgrade $lease $other $agentSha $hash 'S-1-5-18' 'Manual' $expected
  if(!(Test-Path -LiteralPath (Join-Path $root ('native-upgrade-'+$id+'.json'))) -or $j.id -cne $other){throw 'Terminal archive failed.'}
  foreach($phase in @('ServiceStopped','AgentPromoted','NativePromoted','Verified','Committed')){Set-NativeUpgradePhase $lease $other $phase}
  [IO.File]::WriteAllText((Join-Path $root 'native\a.bin'),'changed after promotion')
  Reject {Complete-NativeUpgrade $lease $other {$true}}
  if(![IO.File]::Exists((Join-Path $root 'native-upgrade.pending'))){throw 'Changed new native lost marker.'}
  [IO.File]::WriteAllText((Join-Path $root 'native\a.bin'),'old native')
  [IO.File]::WriteAllText((Join-Path $root 'native-upgrade.pending'),('x'*65536))
  Reject {Complete-NativeUpgrade $lease $other {$true}}
  [IO.File]::WriteAllText((Join-Path $root 'native-upgrade.pending'),'tamper')
  Reject {Set-NativeUpgradePhase $lease $other 'ServiceStopped'}
  if(![IO.File]::Exists((Join-Path $root 'native-upgrade.pending'))){throw 'Tampered marker removed.'}
  [IO.File]::Delete((Join-Path $root 'native-upgrade.pending'))
  $interrupted=Read-NativeUpgradeJournal $lease;$interrupted.phase='Prepared';Write-NativeUpgradeJournal (Join-Path $root 'native-upgrade.json') $interrupted
  Reject {Start-NativeUpgrade $lease ('c'*32) $hash $hash 'S-1-5-18' 'Automatic' $expected}
  if((Read-NativeUpgradeJournal $lease).id -cne $other){throw 'Incomplete prepared journal was overwritten.'}
  'PASS native upgrade journal, transitions, recovery, tamper, pair verification and archive'
 }finally{$lease.Handle.Dispose()}
}finally{if([IO.Directory]::Exists($root)){[IO.Directory]::Delete($root,$true)}}
