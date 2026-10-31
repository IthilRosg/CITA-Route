# Pair operations require the caller to dot-source NativeUpgradeTransaction.ps1,
# hold its exclusive lease, and maintain the external service/process fence.
function Assert-NativePairFence($Lease,[string]$Id,[scriptblock]$AssertQuiescentFence) {
    if(!$AssertQuiescentFence){throw 'Native pair safety proof required.'}
    $j=Read-NativeUpgradeJournal $Lease;if($j.id -cne $Id){throw 'Native pair transaction identity differs.'}
    $marker=Assert-NativeUpgradeMarker $Lease $Id;$marker.Dispose()
    $safe=& $AssertQuiescentFence
    if($safe -isnot [bool] -or !$safe){throw 'Native pair SCM quiescence or exclusive writer unconfirmed.'}
    return $j
}
function Get-NativePairPaths($Lease,[string]$Id) {
    if($Id -cnotmatch '^[a-f0-9]{32}$'){throw 'Native pair identity invalid.'}
    $root=$Lease.Root;$prefix='native-upgrade-'+$Id
    $paths=[ordered]@{Agent=(Join-Path $root 'RoutePilot.Agent.exe');AgentStage=(Join-Path $root ($prefix+'-agent-stage.exe'));AgentOld=(Join-Path $root ($prefix+'-agent-old.exe'));AgentNew=(Join-Path $root ($prefix+'-agent-new.exe'));Native=(Join-Path $root 'native');NativeStage=(Join-Path $root ('native-stage-'+$Id));NativeOld=(Join-Path $root ($prefix+'-native-old'));NativeNew=(Join-Path $root ($prefix+'-native-new'))}
    foreach($path in $paths.Values){if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -ine $root){throw 'Native pair path escaped root.'};Assert-NativeUpgradePath $path}
    return $paths
}
function Get-NativePairFileKind([string]$Path,[string]$Old,[string]$New) {
    Assert-NativeUpgradePath $Path
    if([IO.Directory]::Exists($Path)){throw 'Native pair file path is a directory.'}
    if(![IO.File]::Exists($Path)){return 'Missing'}
    $sha=Get-NativeUpgradeHash $Path
    if($sha -ceq $Old -and $sha -ceq $New){return 'Both'}
    if($sha -ceq $Old){return 'Old'};if($sha -ceq $New){return 'New'}
    throw 'Native pair file hash differs.'
}
function Get-NativePairDirectoryKind([string]$Path,$Journal) {
    Assert-NativeUpgradePath $Path
    if([IO.File]::Exists($Path)){throw 'Native pair directory path is a file.'}
    if(![IO.Directory]::Exists($Path)){return 'Missing'}
    $actual=Get-NativeUpgradeInventoryFromDirectory $Path
    $json=ConvertTo-Json -InputObject @($actual) -Depth 6 -Compress
    $old=$json -ceq (ConvertTo-Json -InputObject @($Journal.oldNative) -Depth 6 -Compress)
    $new=$json -ceq (ConvertTo-Json -InputObject @($Journal.newNative) -Depth 6 -Compress)
    if($old -and $new){return 'Both'};if($old){return 'Old'};if($new){return 'New'}
    throw 'Native pair directory inventory differs.'
}
function Get-NativePairState($Lease,$Journal) {
    $p=Get-NativePairPaths $Lease $Journal.id
    return [pscustomobject]@{Paths=$p;Agent=(Get-NativePairFileKind $p.Agent $Journal.oldAgentSha $Journal.newAgentSha);AgentStage=(Get-NativePairFileKind $p.AgentStage $Journal.oldAgentSha $Journal.newAgentSha);AgentOld=(Get-NativePairFileKind $p.AgentOld $Journal.oldAgentSha $Journal.newAgentSha);AgentNew=(Get-NativePairFileKind $p.AgentNew $Journal.oldAgentSha $Journal.newAgentSha);Native=(Get-NativePairDirectoryKind $p.Native $Journal);NativeStage=(Get-NativePairDirectoryKind $p.NativeStage $Journal);NativeOld=(Get-NativePairDirectoryKind $p.NativeOld $Journal);NativeNew=(Get-NativePairDirectoryKind $p.NativeNew $Journal)}
}
function Start-NativePairPromotion($Lease,[string]$Id,[scriptblock]$AssertQuiescentFence) {
    $j=Assert-NativePairFence $Lease $Id $AssertQuiescentFence
    if($j.phase -cne 'ServiceStopped'){throw 'Native pair promotion requires stopped service phase.'}
    $s=Get-NativePairState $Lease $j
    if($s.Agent -notin @('Old','Both') -or $s.AgentStage -notin @('New','Both') -or $s.Native -notin @('Old','Both') -or $s.NativeStage -notin @('New','Both') -or $s.AgentOld -ne 'Missing' -or $s.AgentNew -ne 'Missing' -or $s.NativeOld -ne 'Missing' -or $s.NativeNew -ne 'Missing'){throw 'Native pair initial state refused.'}
    $null=Assert-NativePairFence $Lease $Id $AssertQuiescentFence
    [IO.File]::Replace($s.Paths.AgentStage,$s.Paths.Agent,$s.Paths.AgentOld)
    $null=Assert-NativePairFence $Lease $Id $AssertQuiescentFence
    Set-NativeUpgradePhase $Lease $Id 'AgentPromoted'
    $null=Assert-NativePairFence $Lease $Id $AssertQuiescentFence
    [IO.Directory]::Move($s.Paths.Native,$s.Paths.NativeOld)
    $null=Assert-NativePairFence $Lease $Id $AssertQuiescentFence
    [IO.Directory]::Move($s.Paths.NativeStage,$s.Paths.Native)
    $null=Assert-NativePairFence $Lease $Id $AssertQuiescentFence
    Set-NativeUpgradePhase $Lease $Id 'NativePromoted'
}
function Restore-NativePair($Lease,[string]$Id,[scriptblock]$AssertQuiescentFence) {
    $j=Assert-NativePairFence $Lease $Id $AssertQuiescentFence
    if($j.phase -notin @('Prepared','ServiceStopped','AgentPromoted','NativePromoted','Verified','Restoring','RecoveryFailed','RolledBack')){throw 'Native pair rollback phase refused.'}
    $s=Get-NativePairState $Lease $j
    if($j.phase -ceq 'RolledBack' -and ($s.Agent -notin @('Old','Both') -or $s.Native -notin @('Old','Both'))){throw 'Terminal native pair no longer matches rollback proof.'}
    if($s.Agent -eq 'Missing' -or $s.AgentStage -notin @('Missing','New','Both') -or $s.AgentOld -notin @('Missing','Old','Both') -or $s.AgentNew -notin @('Missing','New','Both') -or $s.NativeStage -notin @('Missing','New','Both') -or $s.NativeOld -notin @('Missing','Old','Both') -or $s.NativeNew -notin @('Missing','New','Both')){throw 'Native pair rollback inputs refused.'}
    if($s.Agent -eq 'New' -and ($s.AgentOld -notin @('Old','Both') -or $s.AgentNew -ne 'Missing')){throw 'Native pair old agent recovery unavailable.'}
    if($s.Native -eq 'New' -and ($s.NativeOld -notin @('Old','Both') -or $s.NativeNew -ne 'Missing')){throw 'Native pair old native recovery unavailable.'}
    if($s.Native -eq 'Missing' -and $s.NativeOld -notin @('Old','Both')){throw 'Native pair old native directory missing.'}
    if($j.phase -ne 'Restoring' -and $j.phase -ne 'RolledBack'){Set-NativeUpgradePhase $Lease $Id 'Restoring'}
    if($s.Agent -eq 'New'){$null=Assert-NativePairFence $Lease $Id $AssertQuiescentFence;[IO.File]::Replace($s.Paths.AgentOld,$s.Paths.Agent,$s.Paths.AgentNew)}
    if($s.Native -eq 'New'){$null=Assert-NativePairFence $Lease $Id $AssertQuiescentFence;[IO.Directory]::Move($s.Paths.Native,$s.Paths.NativeNew);$s.Native='Missing'}
    if($s.Native -eq 'Missing'){$null=Assert-NativePairFence $Lease $Id $AssertQuiescentFence;[IO.Directory]::Move($s.Paths.NativeOld,$s.Paths.Native)}
    $null=Assert-NativePairFence $Lease $Id $AssertQuiescentFence;$after=Get-NativePairState $Lease (Read-NativeUpgradeJournal $Lease)
    if($after.Agent -notin @('Old','Both') -or $after.Native -notin @('Old','Both')){throw 'Native pair rollback remains unconfirmed.'}
    if((Read-NativeUpgradeJournal $Lease).phase -ne 'RolledBack'){Set-NativeUpgradePhase $Lease $Id 'RolledBack'}
}
