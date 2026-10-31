$ErrorActionPreference='Stop'
. "$PSScriptRoot\NativeUpgradeTransaction.ps1";. "$PSScriptRoot\NativeUpgradePair.ps1";. "$PSScriptRoot\NativeUpgradeFlow.ps1"
# Model adapters replace SCM/RPC only; pair, journal, inventory and marker are real FS.
function Assert-NativeUpgradeWriter([scriptblock]$AssertWriter){Assert-NativeUpgradeFlowCallback $AssertWriter 'Writer refused.'}
function Stop-NativeUpgradeService($Lease,[string]$Id,[scriptblock]$AssertWriter){Assert-NativeUpgradeWriter $AssertWriter;Set-NativeUpgradePhase $Lease $Id 'ServiceStopped'}
$script:stoppedOk=$true
$script:readyPhase='Disconnected'
function Test-NativeUpgradeStoppedFence($Lease){return $script:stoppedOk}
function Start-NativeUpgradeValidationService($Lease,[string]$Id,[scriptblock]$AssertWriter){return [pscustomobject]@{Id=$Id}}
function Invoke-NativeUpgradeServiceRpc($Lease,$Proof,[string]$Command,[string]$Id){
 $script:rpcCount++
 $phase=if($script:rpcCount -eq 1){'Stopping'}else{$script:readyPhase}
 $op=if($script:badReply){'b'*32}else{'a'*32}
 return [pscustomobject]@{Success=$true;Native=[pscustomobject]@{phase=$phase;desired=$false;operation=$op;generation=7;errorCode=$null}}
}
function Reject([scriptblock]$action){$failed=$false;try{& $action}catch{$failed=$true};if(!$failed){throw 'Expected rejection.'}}
$base=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../runtime')).TrimEnd('\');$testRoot=[IO.Path]::GetFullPath((Join-Path $base ('flow-tests-'+[guid]::NewGuid().ToString('N'))))
if([IO.Path]::GetDirectoryName($testRoot) -ine $base -or [IO.Path]::GetFileName($testRoot) -cnotmatch '^flow-tests-[a-f0-9]{32}$'){throw 'Test root is outside the approved runtime.'}
[void][IO.Directory]::CreateDirectory($testRoot)
function Setup([string]$name){
 $root=[IO.Path]::GetFullPath((Join-Path $testRoot $name));[void][IO.Directory]::CreateDirectory((Join-Path $root 'native'));[IO.File]::WriteAllText((Join-Path $root 'native\file.bin'),'old native');[IO.File]::WriteAllText((Join-Path $root 'RoutePilot.Agent.exe'),'old agent')
 $id=[guid]::NewGuid().ToString('N');$stage=New-NativePreparationStage $root $id;[IO.File]::WriteAllText((Join-Path $stage 'file.bin'),'new native');$agent=Join-Path $root ('native-upgrade-'+$id+'-agent-stage.exe');[IO.File]::WriteAllText($agent,'new agent')
 $inventory=Get-NativeUpgradeInventoryFromDirectory $stage;$lease=Open-NativeUpgradeLease $root;$null=Start-NativeUpgrade $lease $id (Get-NativeUpgradeHash $agent) ('A'*64) 'S-1-5-18' 'Automatic' $inventory
 return [pscustomobject]@{Root=$root;Id=$id;Lease=$lease}
}
try{
 $script:rpcCount=0;$script:badReply=$false;$t=Setup 'success';try{
  $counter=[pscustomobject]@{Calls=0}
  $restore={if(!(Test-Path -LiteralPath (Join-Path $t.Root 'native-upgrade.pending')) -or (Read-NativeUpgradeJournal $t.Lease).phase -cne 'Verified'){throw 'Startup restored out of order.'};$counter.Calls++;return $true}
  $out=Invoke-NativeUpgradeForward $t.Lease $t.Id {$true} {$true} $restore {param($j,$a,$n) return $true} ('a'*32) 7
  if(!$out.success -or $out.phase -cne 'Committed' -or $script:rpcCount -ne 2 -or $counter.Calls -ne 1 -or (Test-Path -LiteralPath (Join-Path $t.Root 'native-upgrade.pending'))){throw 'Forward order or readiness differs.'}
 }finally{$t.Lease.Handle.Dispose()}
 $script:rpcCount=0;$script:badReply=$false;$script:readyPhase='Unconfigured';$t=Setup 'unconfigured';try{
  $out=Invoke-NativeUpgradeForward $t.Lease $t.Id {$true} {$true} {$true} {param($j,$a,$n) return $true} ('a'*32) 7 'Unconfigured'
  if(!$out.success -or $out.phase -cne 'Committed' -or $script:rpcCount -ne 2 -or (Test-Path -LiteralPath (Join-Path $t.Root 'native-upgrade.pending'))){throw 'Unconfigured forward did not preserve exact readiness phase.'}
 }finally{$script:readyPhase='Disconnected';$t.Lease.Handle.Dispose()}
 $script:rpcCount=0;$script:badReply=$true;$t=Setup 'wrong-op';try{
  Reject {Invoke-NativeUpgradeForward $t.Lease $t.Id {$true} {$true} {$true} {return $true} ('a'*32) 7}
  if(!(Test-Path -LiteralPath (Join-Path $t.Root 'native-upgrade.pending')) -or (Read-NativeUpgradeJournal $t.Lease).phase -cne 'NativePromoted'){throw 'Invalid readiness did not preserve pending transaction.'}
 }finally{$t.Lease.Handle.Dispose()}
 $script:rpcCount=0;$script:badReply=$false;$t=Setup 'startup-failed';try{
  Reject {Invoke-NativeUpgradeForward $t.Lease $t.Id {$true} {$true} {$false} {return $true} ('a'*32) 7}
  if(!(Test-Path -LiteralPath (Join-Path $t.Root 'native-upgrade.pending')) -or (Read-NativeUpgradeJournal $t.Lease).phase -cne 'Verified'){throw 'Failed startup restore lost marker or advanced commit.'}
 }finally{$t.Lease.Handle.Dispose()}
 $script:rpcCount=0;$t=Setup 'leases-failed';try{
  Reject {Invoke-NativeUpgradeForward $t.Lease $t.Id {$true} {@($true,$true)} {$true} {return $true} ('a'*32) 7}
  if((Read-NativeUpgradeJournal $t.Lease).phase -cne 'ServiceStopped' -or [IO.File]::ReadAllText((Join-Path $t.Root 'RoutePilot.Agent.exe')) -cne 'old agent'){throw 'Bad lease callback caused promotion.'}
 }finally{$t.Lease.Handle.Dispose()}
 $script:rpcCount=0;$t=Setup 'invalid-identity';try{
  Reject {Invoke-NativeUpgradeForward $t.Lease $t.Id {$true} {$true} {$true} {return $true} 'invalid' 7}
  if((Read-NativeUpgradeJournal $t.Lease).phase -cne 'Prepared' -or [IO.File]::ReadAllText((Join-Path $t.Root 'RoutePilot.Agent.exe')) -cne 'old agent'){throw 'Invalid identity caused service or file work.'}
 }finally{$t.Lease.Handle.Dispose()}
 $script:stoppedOk=$false;$t=Setup 'stopped-fence-failed';try{
  Reject {Invoke-NativeUpgradeForward $t.Lease $t.Id {$true} {$true} {$true} {return $true} ('a'*32) 7}
  if((Read-NativeUpgradeJournal $t.Lease).phase -cne 'ServiceStopped' -or [IO.File]::ReadAllText((Join-Path $t.Root 'RoutePilot.Agent.exe')) -cne 'old agent'){throw 'Failed stopped fence caused file work.'}
 }finally{$script:stoppedOk=$true;$t.Lease.Handle.Dispose()}
 foreach($bad in @(
  [pscustomobject]@{Success=$true;Native=[pscustomobject]@{phase='Stopping';desired=$true;operation=('a'*32);generation=7;errorCode=$null}},
  [pscustomobject]@{Success=$true;Native=[pscustomobject]@{phase='Stopping';desired=$false;operation=('a'*32);generation=8;errorCode=$null}},
  [pscustomobject]@{Success=$true;Native=[pscustomobject]@{phase='Stopping';desired=$false;operation=('a'*32);generation=7;errorCode='Failure'}},
  [pscustomobject]@{Success=$true;Native=[pscustomobject]@{phase='Stopping';desired=$false;operation=('a'*32);generation=7;errorCode=0}},
  [pscustomobject]@{Success=$false;Native=[pscustomobject]@{phase='Disconnected';desired=$false;operation=('a'*32);generation=7;errorCode=$null}}
 )){Reject {Get-NativeUpgradeFlowReadiness $bad ('a'*32) 7 'Disconnected'}}
 'PASS flow model adapters; real journal/pair files; no SCM or IPC'
}finally{if([IO.Directory]::Exists($testRoot)){if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($testRoot)) -ine $base -or [IO.Path]::GetFileName($testRoot) -cnotmatch '^flow-tests-[a-f0-9]{32}$'){throw 'Test cleanup target refused.'};Assert-NativeUpgradePath $testRoot;if(@(Get-ChildItem -LiteralPath $testRoot -Force -Recurse|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Test cleanup found a link.'};[IO.Directory]::Delete($testRoot,$true)}}
