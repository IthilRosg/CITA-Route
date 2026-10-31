# Recovery functions use the caller's exclusive transaction lease and an
# independent supervisor that has retired the previous writer's entire job.
# Source-only library: caller owns root ACL, coordinator and boot fencing.
function Assert-NativeRecoveryProof([scriptblock]$Proof,[string]$Reason,[object[]]$Arguments=@()) {
    if(!$Proof){throw $Reason};$result=& $Proof @Arguments
    if($result -isnot [bool] -or !$result){throw $Reason}
}
function Test-NativeRecoveryMarkerPresent($Lease) {
    $path=Join-Path $Lease.Root 'native-upgrade.pending';Assert-NativeUpgradePath $path
    try{[void][IO.File]::GetAttributes($path);return $true}catch [IO.FileNotFoundException]{return $false}
}
function Wait-NativeRecoveryTransition($Lease,[int]$ExpectedPid,[string]$Mode,[int]$TimeoutSeconds,[scriptblock]$AssertRetired,[scriptblock]$ReadState={param($lease) Get-NativeUpgradeServiceSnapshot $lease}) {
    if($ExpectedPid -lt 0 -or $Mode -cnotin @('Starting','Stopping') -or $TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 35){throw 'Native recovery transition arguments refused.'}
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds){
        Assert-NativeRecoveryProof $AssertRetired 'Prior native writer retirement changed.'
        $current=& $ReadState $Lease
        if(!$current -or $current.StartType -cne 'Disabled' -or $current.ProcessId -lt 0){throw 'Native recovery transition fence differs.'}
        if($current.State -ceq 'Stopped'){
            if($current.ProcessId -ne 0){throw 'Stopped native recovery service still has a process.'}
            return $current
        }
        if($current.State -ceq 'Running'){
            if($ExpectedPid -le 0 -or $current.ProcessId -ne $ExpectedPid){throw 'Native recovery transition running PID differs.'}
            if($Mode -ceq 'Starting'){return $current}
            Start-Sleep -Milliseconds 100;continue
        }
        if($current.State -notin @('Start Pending','Stop Pending') -or ($Mode -ceq 'Stopping' -and $current.State -cne 'Stop Pending') -or ($current.ProcessId -ne $ExpectedPid -and !($current.State -ceq 'Stop Pending' -and $current.ProcessId -eq 0))){throw 'Native recovery transition identity differs.'}
        Start-Sleep -Milliseconds 100
    }
    throw 'Native recovery transition deadline exceeded.'
}
function Stop-NativeRecoveryService($Lease,$Journal,[scriptblock]$AssertRetired) {
    Assert-NativeRecoveryProof $AssertRetired 'Prior native writer retirement unconfirmed.'
    $state=Get-NativeUpgradeServiceSnapshot $Lease
    if($state.State -notin @('Running','Stopped','Start Pending','Stop Pending')){throw 'Native recovery service state refused.'}
    $agent=Join-Path $Lease.Root 'RoutePilot.Agent.exe';Assert-NativeUpgradePath $agent
    $image=[IO.FileStream]::new($agent,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);$process=$null;$intent=$null
    try{
        $actual=Get-NativeUpgradeHash $agent
        if($actual -cne $Journal.oldAgentSha -and $actual -cne $Journal.newAgentSha){throw 'Native recovery agent hash is unknown.'}
        if($state.State -ceq 'Stopped' -and $state.ProcessId -ne 0){throw 'Stopped service still has a process.'}
        if($state.State -ceq 'Running' -and $state.ProcessId -le 0){throw 'Native recovery service PID unavailable.'}
        if($state.ProcessId -gt 0){
            $process=Get-Process -Id $state.ProcessId -ErrorAction Stop;$null=$process.Handle
            if($process.HasExited -or $process.Path -ine $agent){throw 'Native recovery service process differs.'}
        }
        if($state.State -ceq 'Start Pending'){$intent=Open-NativeRecoveryIntent $Lease}
        Set-Service -Name RoutePilotRescue -StartupType Disabled -ErrorAction Stop
        if((Get-NativeUpgradeServiceSnapshot $Lease).StartType -cne 'Disabled'){throw 'Native recovery boot fence unconfirmed.'}
        Assert-NativeRecoveryProof $AssertRetired 'Prior native writer retirement changed.'
        if($state.State -ceq 'Start Pending'){$state=Wait-NativeRecoveryTransition $Lease $state.ProcessId 'Starting' 20 $AssertRetired}
        if($state.State -ceq 'Running'){
            if(!$process -or $process.HasExited -or $process.Path -ine $agent -or $process.Id -ne $state.ProcessId){throw 'Native recovery service identity changed before stop.'}
            Stop-Service -Name RoutePilotRescue -ErrorAction Stop
            $state=Wait-NativeRecoveryTransition $Lease $process.Id 'Stopping' 35 $AssertRetired
        }elseif($state.State -ceq 'Stop Pending'){$state=Wait-NativeRecoveryTransition $Lease $state.ProcessId 'Stopping' 35 $AssertRetired}
        if($state.State -cne 'Stopped' -or ($process -and !$process.WaitForExit(5000))){throw 'Native recovery service exit unconfirmed.'}
        if(!(Test-NativeUpgradeStoppedFence $Lease)){throw 'Native recovery stopped fence unconfirmed.'}
    }finally{if($intent){$intent.Handle.Dispose()};if($process){$process.Dispose()};$image.Dispose()}
}
function Open-NativeRecoveryIntent($Lease) {
    $path=Join-Path $Lease.Root 'native\intent.json';Assert-NativeUpgradePath $path
    $file=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        if($file.Length -lt 1 -or $file.Length -gt 4096){throw 'Native recovery intent size refused.'}
        $reader=[IO.StreamReader]::new($file,[Text.UTF8Encoding]::new($false,$true),$false,1024,$true);try{$value=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()};$file.Position=0
        if($value.Desired -isnot [bool] -or $value.Desired -or $value.Operation -cnotmatch '^[a-f0-9]{32}$' -or ($value.Generation -isnot [int] -and $value.Generation -isnot [long]) -or $value.Generation -lt 0){throw 'Native recovery intent is not safely disconnected.'}
        return [pscustomobject]@{Handle=$file;Operation=[string]$value.Operation;Generation=[long]$value.Generation}
    }catch{$file.Dispose();throw}
}
function Assert-NativeRecoveryStartedProof($Lease,$Journal,$Proof,[string]$ExpectedHash) {
    if(!$Proof -or $Proof.Id -cne $Journal.id -or $Proof.AgentSha -cne $ExpectedHash -or $Proof.ProcessId -le 0 -or $Proof.StartedUtcTicks -le 0){throw 'Native recovery started process proof invalid.'}
    $state=Get-NativeUpgradeServiceSnapshot $Lease;if($state.State -cne 'Running' -or $state.ProcessId -ne $Proof.ProcessId){throw 'Native recovery started service differs.'}
    $agent=Join-Path $Lease.Root 'RoutePilot.Agent.exe';Assert-NativeUpgradePath $agent
    $image=[IO.FileStream]::new($agent,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);$process=$null
    try{
        if((Get-NativeUpgradeHash $agent) -cne $ExpectedHash){throw 'Native recovery started image differs.'}
        $process=Get-Process -Id $state.ProcessId -ErrorAction Stop;$null=$process.Handle
        if($process.HasExited -or $process.Path -ine $agent -or $process.StartTime.ToUniversalTime().Ticks -ne $Proof.StartedUtcTicks){throw 'Native recovery started process differs.'}
    }finally{if($process){$process.Dispose()};$image.Dispose()}
}
function Assert-NativeRecoveryImmutableRoles($Lease,$Journal) {
    $native=Join-Path $Lease.Root 'native';Assert-NativeUpgradePath $native
    $expected=@{};foreach($entry in $Journal.newNative){if([IO.Path]::GetExtension($entry.path) -iin @('.exe','.dll','.sys')){$expected[$entry.path]=$entry}}
    if($expected.Count -eq 0){throw 'Committed native immutable roles absent.'}
    $items=@(Get-ChildItem -LiteralPath $native -Force -Recurse -ErrorAction Stop)
    if(@($items|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Committed native path contains a link.'}
    $actual=@($items|Where-Object {!$_.PSIsContainer -and [IO.Path]::GetExtension($_.Name) -iin @('.exe','.dll','.sys')})
    if($actual.Count -ne $expected.Count){throw 'Committed native immutable role count differs.'}
    foreach($file in $actual){$relative=$file.FullName.Substring($native.Length+1);$entry=$expected[$relative];if(!$entry -or $file.Length -ne $entry.bytes -or (Get-NativeUpgradeHash $file.FullName) -cne $entry.sha256){throw 'Committed native immutable role differs.'}}
}
function Assert-NativeUpgradeMissingJournal($Lease,[string]$Id,[scriptblock]$BaselineUnchanged) {
    Assert-NativeUpgradeLease $Lease;if($Id -cnotmatch '^[a-f0-9]{32}$'){throw 'Recovery transaction identity invalid.'}
    $journal=Join-Path $Lease.Root 'native-upgrade.json';Assert-NativeUpgradePath $journal
    if([IO.File]::Exists($journal) -or [IO.Directory]::Exists($journal) -or (Test-NativeRecoveryMarkerPresent $Lease)){throw 'Native recovery journal or marker exists.'}
    Assert-NativeRecoveryProof $BaselineUnchanged 'Missing journal baseline is not confirmed.'
    return [pscustomobject]@{success=$true;outcome='NoJournalBaselineUnchanged';id=$Id}
}
function Invoke-NativeUpgradeRecovery($Lease,[string]$Id,[scriptblock]$AssertRetired,[scriptblock]$WaitOldLeasesReleased,[scriptblock]$StartOldAndGetProof,[scriptblock]$ValidateOldStatus,[scriptblock]$RestoreStartupType,[scriptblock]$VerifyOldPair,[scriptblock]$GetCommittedStartedProof,[string]$ExpectedPhase='Disconnected') {
    $ErrorActionPreference='Stop';Assert-NativeUpgradeLease $Lease
    if($Id -cnotmatch '^[a-f0-9]{32}$'){throw 'Recovery transaction identity invalid.'}
    if($ExpectedPhase -cnotin @('Disconnected','Unconfigured')){throw 'Native recovery expected phase invalid.'}
    foreach($cb in @($AssertRetired,$WaitOldLeasesReleased,$StartOldAndGetProof,$ValidateOldStatus,$RestoreStartupType,$VerifyOldPair,$GetCommittedStartedProof)){if(!$cb){throw 'Native recovery callback missing.'}}
    Assert-NativeRecoveryProof $AssertRetired 'Prior native writer retirement unconfirmed.'
    $journal=Join-Path $Lease.Root 'native-upgrade.json';Assert-NativeUpgradePath $journal
    if(![IO.File]::Exists($journal)){throw 'Native recovery journal unavailable; use the separate read-only baseline check.'}
    $j=Read-NativeUpgradeJournal $Lease;if($j.id -cne $Id){throw 'Native recovery transaction differs.'}
    $present=Test-NativeRecoveryMarkerPresent $Lease
    if($j.phase -ceq 'Committed'){
        if($present){throw 'Committed native upgrade still has a pending marker; independent recovery needs attention.'}
        $proof=& $GetCommittedStartedProof
        if(!$proof -or $proof.Id -cne $Id -or $proof.AgentSha -cne $j.newAgentSha -or $proof.ProcessId -le 0 -or $proof.StartedUtcTicks -le 0){throw 'Committed service process proof unavailable.'}
        Assert-NativeRecoveryStartedProof $Lease $j $proof $j.newAgentSha
        Assert-NativeRecoveryImmutableRoles $Lease $j
        $caps=Invoke-NativeUpgradeServiceRpc $Lease $proof 'native-capabilities' $Id
        if(!$caps -or $caps.Success -isnot [bool] -or !$caps.Success -or $caps.Native.maintenanceGateProtocol -ne 1 -or $caps.Native.maintenanceValidationProtocol -ne 1){throw 'Committed native capabilities unconfirmed.'}
        return [pscustomobject]@{success=$true;outcome='CommittedReadOnly';id=$Id}
    }
    if(!$present -or $j.phase -notin @('Prepared','ServiceStopped','AgentPromoted','NativePromoted','Verified','Restoring','RecoveryFailed','RolledBack')){throw 'Native recovery marker or phase refused.'}
    $marker=Assert-NativeUpgradeMarker $Lease $Id;$intent=$null;$oldStartAttempted=$false
    try{
        Stop-NativeRecoveryService $Lease $j $AssertRetired
        Assert-NativeRecoveryProof $WaitOldLeasesReleased 'Previous native service leases remain held.'
        $fence={Assert-NativeRecoveryProof $AssertRetired 'Recovery writer lease changed.';$s=Test-NativeUpgradeStoppedFence $Lease;return ($s -is [bool] -and $s)}
        Restore-NativePair $Lease $Id $fence
        $j=Read-NativeUpgradeJournal $Lease
        $intent=Open-NativeRecoveryIntent $Lease
        $oldStartAttempted=$true;$proof=& $StartOldAndGetProof $Lease $j $AssertRetired
        Assert-NativeRecoveryStartedProof $Lease $j $proof $j.oldAgentSha
        $timer=[Diagnostics.Stopwatch]::StartNew();$ready=$false
        while($timer.Elapsed.TotalSeconds -lt 33){
            Assert-NativeRecoveryProof $AssertRetired 'Recovery writer lease changed.'
            Assert-NativeRecoveryStartedProof $Lease $j $proof $j.oldAgentSha
            $reply=& $ValidateOldStatus $Lease $proof $Id
            Assert-NativeRecoveryStartedProof $Lease $j $proof $j.oldAgentSha
            if($timer.Elapsed.TotalSeconds -ge 45){throw 'Native recovery readiness deadline exceeded.'}
            if($reply -and $reply.Success -is [bool] -and !$reply.Success -and $reply.PSObject.Properties['Native'] -and $null -eq $reply.Native -and !$reply.PSObject.Properties['ErrorCode']){
                Start-Sleep -Milliseconds 200;continue
            }
            if((Get-NativeUpgradeFlowReadiness $reply $intent.Operation $intent.Generation $ExpectedPhase) -ceq 'Ready'){$ready=$true;break}
            Start-Sleep -Milliseconds 200
        }
        if(!$ready){throw 'Native recovery readiness deadline exceeded.'}
        Assert-NativeRecoveryProof $RestoreStartupType 'Native recovery startup type restoration unconfirmed.' @($j.originalStartType)
        Assert-NativeRecoveryProof $AssertRetired 'Recovery writer lease changed.'
        # Keep the false-intent lease until marker deletion is complete; the
        # restored old agent may not understand the maintenance marker.
        $marker.Dispose();$marker=$null
        Complete-NativeUpgrade $Lease $Id $VerifyOldPair
    }catch{
        $original=$_
        if($oldStartAttempted){try{Stop-NativeRecoveryService $Lease $j $AssertRetired}catch{throw 'Recovery service could not be safely stopped; independent intervention required.'}}
        throw $original
    }finally{if($intent){$intent.Handle.Dispose()};if($marker){$marker.Dispose()}}
    return [pscustomobject]@{success=$true;outcome='RolledBack';id=$Id}
}
