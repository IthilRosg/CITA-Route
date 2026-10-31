$ErrorActionPreference='Stop'
. "$PSScriptRoot\NativeUpgradeTransaction.ps1";. "$PSScriptRoot\NativeUpgradePair.ps1"
$base=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../runtime')).TrimEnd('\');$testRoot=[IO.Path]::GetFullPath((Join-Path $base ('pair-tests-'+[guid]::NewGuid().ToString('N'))));[void][IO.Directory]::CreateDirectory($testRoot)
function Reject([scriptblock]$action){$failed=$false;try{& $action}catch{$failed=$true};if(!$failed){throw 'Expected rejection.'}}
function Setup([string]$name,[bool]$sameAgent=$false,[bool]$sameNative=$false){
 $root=[IO.Path]::GetFullPath((Join-Path $testRoot $name));[void][IO.Directory]::CreateDirectory((Join-Path $root 'native'));[IO.File]::WriteAllText((Join-Path $root 'native\file.bin'),'old native');[IO.File]::WriteAllText((Join-Path $root 'RoutePilot.Agent.exe'),'old agent')
 $id=[guid]::NewGuid().ToString('N');$stage=New-NativePreparationStage $root $id;[IO.File]::WriteAllText((Join-Path $stage 'file.bin'),$(if($sameNative){'old native'}else{'new native'}))
 $agent=Join-Path $root ('native-upgrade-'+$id+'-agent-stage.exe');[IO.File]::WriteAllText($agent,$(if($sameAgent){'old agent'}else{'new agent'}));$hash=Get-NativeUpgradeHash $agent;$inventory=Get-NativeUpgradeInventoryFromDirectory $stage
 $lease=Open-NativeUpgradeLease $root;$null=Start-NativeUpgrade $lease $id $hash ('A'*64) 'S-1-5-18' 'Automatic' $inventory;Set-NativeUpgradePhase $lease $id 'ServiceStopped'
 return [pscustomobject]@{Root=$root;Id=$id;Lease=$lease;Paths=(Get-NativePairPaths $lease $id)}
}
try{
 foreach($step in 0..6){
  $t=Setup ('interrupt-'+$step);try{
   if($step -ge 1){[IO.File]::Replace($t.Paths.AgentStage,$t.Paths.Agent,$t.Paths.AgentOld)}
   if($step -ge 2){Set-NativeUpgradePhase $t.Lease $t.Id 'AgentPromoted'}
   if($step -ge 3){[IO.Directory]::Move($t.Paths.Native,$t.Paths.NativeOld)}
   if($step -ge 4){[IO.Directory]::Move($t.Paths.NativeStage,$t.Paths.Native)}
   if($step -ge 5){Set-NativeUpgradePhase $t.Lease $t.Id 'NativePromoted'}
   if($step -ge 6){[IO.Directory]::Move($t.Paths.Native,$t.Paths.NativeNew)}
   Restore-NativePair $t.Lease $t.Id {$true};Restore-NativePair $t.Lease $t.Id {$true}
   if([IO.File]::ReadAllText($t.Paths.Agent) -cne 'old agent' -or [IO.File]::ReadAllText((Join-Path $t.Paths.Native 'file.bin')) -cne 'old native' -or (Read-NativeUpgradeJournal $t.Lease).phase -cne 'RolledBack' -or !(Test-Path -LiteralPath (Join-Path $t.Root 'native-upgrade.pending'))){throw 'Interrupt recovery did not retain old pair and marker.'}
  }finally{$t.Lease.Handle.Dispose()}
 }
 foreach($combo in @(@{name='same-agent';agent=$true;native=$false},@{name='same-native';agent=$false;native=$true},@{name='same-both';agent=$true;native=$true})){
  foreach($step in 0..6){
   $t=Setup ($combo.name+'-'+$step) $combo.agent $combo.native
   try{
    $before=Read-NativeUpgradeJournal $t.Lease
    $beforeFields=ConvertTo-Json -Compress -Depth 8 -InputObject @($before.id,$before.root,$before.oldAgentSha,@($before.oldNative),$before.newAgentSha,@($before.newNative),$before.archiveSha,$before.ownerSid,$before.originalStartType)
    if($step -ge 1){[IO.File]::Replace($t.Paths.AgentStage,$t.Paths.Agent,$t.Paths.AgentOld)}
    if($step -ge 2){Set-NativeUpgradePhase $t.Lease $t.Id 'AgentPromoted'}
    if($step -ge 3){[IO.Directory]::Move($t.Paths.Native,$t.Paths.NativeOld)}
    if($step -ge 4){[IO.Directory]::Move($t.Paths.NativeStage,$t.Paths.Native)}
    if($step -ge 5){Set-NativeUpgradePhase $t.Lease $t.Id 'NativePromoted'}
    if($step -ge 6){[IO.Directory]::Move($t.Paths.Native,$t.Paths.NativeNew)}
    Restore-NativePair $t.Lease $t.Id {$true};Restore-NativePair $t.Lease $t.Id {$true}
    $after=Read-NativeUpgradeJournal $t.Lease
    $afterFields=ConvertTo-Json -Compress -Depth 8 -InputObject @($after.id,$after.root,$after.oldAgentSha,@($after.oldNative),$after.newAgentSha,@($after.newNative),$after.archiveSha,$after.ownerSid,$after.originalStartType)
    if($beforeFields -cne $afterFields -or $after.phase -cne 'RolledBack' -or [IO.File]::ReadAllText($t.Paths.Agent) -cne 'old agent' -or [IO.File]::ReadAllText((Join-Path $t.Paths.Native 'file.bin')) -cne 'old native' -or !(Test-Path -LiteralPath (Join-Path $t.Root 'native-upgrade.pending'))){throw ('Equal-component recovery differs: '+$combo.name+' step '+$step)}
   }finally{$t.Lease.Handle.Dispose()}
  }
  $t=Setup ($combo.name+'-forward') $combo.agent $combo.native
  try{
   $before=Read-NativeUpgradeJournal $t.Lease
   $beforeFields=ConvertTo-Json -Compress -Depth 8 -InputObject @($before.id,$before.root,$before.oldAgentSha,@($before.oldNative),$before.newAgentSha,@($before.newNative),$before.archiveSha,$before.ownerSid,$before.originalStartType)
   Start-NativePairPromotion $t.Lease $t.Id {$true}
   if((Read-NativeUpgradeJournal $t.Lease).phase -cne 'NativePromoted'){throw 'Equal-component forward promotion failed.'}
   Restore-NativePair $t.Lease $t.Id {$true};Restore-NativePair $t.Lease $t.Id {$true}
   $after=Read-NativeUpgradeJournal $t.Lease
   $afterFields=ConvertTo-Json -Compress -Depth 8 -InputObject @($after.id,$after.root,$after.oldAgentSha,@($after.oldNative),$after.newAgentSha,@($after.newNative),$after.archiveSha,$after.ownerSid,$after.originalStartType)
   if($beforeFields -cne $afterFields -or $after.phase -cne 'RolledBack' -or [IO.File]::ReadAllText($t.Paths.Agent) -cne 'old agent' -or [IO.File]::ReadAllText((Join-Path $t.Paths.Native 'file.bin')) -cne 'old native'){throw 'Equal-component forward recovery differs.'}
  }finally{$t.Lease.Handle.Dispose()}
 }
 $t=Setup 'forward';try{
  Reject {Start-NativePairPromotion $t.Lease $t.Id {$false}}
  Reject {Start-NativePairPromotion $t.Lease $t.Id {@($true,$true)}}
  if([IO.File]::ReadAllText($t.Paths.Agent) -ne 'old agent'){throw 'Rejected fence mutated agent.'}
  Start-NativePairPromotion $t.Lease $t.Id {$true}
  if([IO.File]::ReadAllText($t.Paths.Agent) -ne 'new agent' -or [IO.File]::ReadAllText((Join-Path $t.Paths.Native 'file.bin')) -ne 'new native' -or (Read-NativeUpgradeJournal $t.Lease).phase -ne 'NativePromoted'){throw 'Pair promotion failed.'}
  Restore-NativePair $t.Lease $t.Id {$true}
 }finally{$t.Lease.Handle.Dispose()}
 $t=Setup 'tamper';try{
  [IO.File]::WriteAllText((Join-Path $t.Paths.NativeStage 'file.bin'),'tampered')
  Reject {Start-NativePairPromotion $t.Lease $t.Id {$true}}
  if([IO.File]::ReadAllText($t.Paths.Agent) -ne 'old agent'){throw 'Tampered stage caused mutation.'}
 }finally{$t.Lease.Handle.Dispose()}
 $t=Setup 'fence-during';try{
  $script:fenceCalls=0
  Reject {Start-NativePairPromotion $t.Lease $t.Id {$script:fenceCalls++;$script:fenceCalls -lt 3}}
  if([IO.File]::ReadAllText($t.Paths.Agent) -ne 'new agent'){throw 'Fixture did not interrupt after atomic promotion.'}
  Restore-NativePair $t.Lease $t.Id {$true}
  if([IO.File]::ReadAllText($t.Paths.Agent) -ne 'old agent'){throw 'Recovery after changed fence failed.'}
  [IO.File]::WriteAllText($t.Paths.Agent,'new agent')
  Reject {Restore-NativePair $t.Lease $t.Id {$true}}
  if([IO.File]::ReadAllText($t.Paths.Agent) -ne 'new agent'){throw 'Terminal mismatch caused mutation.'}
 }finally{$t.Lease.Handle.Dispose()}
 $t=Setup 'missing';try{
  [IO.File]::Replace($t.Paths.AgentStage,$t.Paths.Agent,$t.Paths.AgentOld);[IO.File]::Delete($t.Paths.AgentOld)
  Reject {Restore-NativePair $t.Lease $t.Id {$true}}
  if([IO.File]::ReadAllText($t.Paths.Agent) -ne 'new agent'){throw 'Missing backup caused mutation.'}
 }finally{$t.Lease.Handle.Dispose()}
 $t=Setup 'occupied';try{
  Start-NativePairPromotion $t.Lease $t.Id {$true}
  [void][IO.Directory]::CreateDirectory($t.Paths.NativeNew);[IO.File]::WriteAllText((Join-Path $t.Paths.NativeNew 'file.bin'),'new native')
  Reject {Restore-NativePair $t.Lease $t.Id {$true}}
  if([IO.File]::ReadAllText($t.Paths.Agent) -ne 'new agent' -or [IO.File]::ReadAllText((Join-Path $t.Paths.NativeNew 'file.bin')) -ne 'new native'){throw 'Occupied recovery path was modified.'}
 }finally{$t.Lease.Handle.Dispose()}
 $t=Setup 'busy';try{
  $handle=[IO.File]::Open($t.Paths.Agent,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
  try{Reject {Start-NativePairPromotion $t.Lease $t.Id {$true}}}finally{$handle.Dispose()}
  if([IO.File]::ReadAllText($t.Paths.Agent) -ne 'old agent'){throw 'Busy file caused mutation.'}
 }finally{$t.Lease.Handle.Dispose()}
 'PASS pair promotion, interruption recovery, fences, tamper, missing and busy files; equal-agent/equal-native/equal-both each 7 interruption points'
}finally{
 if([IO.Path]::GetDirectoryName($testRoot) -ine $base -or [IO.Path]::GetFileName($testRoot) -cnotmatch '^pair-tests-[a-f0-9]{32}$'){throw 'Pair fixture cleanup escaped root.'}
 Assert-NativeUpgradePath $testRoot
 if([IO.Directory]::Exists($testRoot)){
  foreach($item in Get-ChildItem -LiteralPath $testRoot -Recurse -Force){if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Pair fixture cleanup found a link.'}}
  [IO.Directory]::Delete($testRoot,$true)
 }
}
