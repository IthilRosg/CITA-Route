# Source-only native upgrade journal. The caller must hold the returned lease
# across every transition and provide verified filesystem/service coordination.
function Assert-NativeUpgradePath([string]$Path) {
    $current=[IO.Path]::GetFullPath($Path)
    while($current){
        try{$attrs=[IO.File]::GetAttributes($current)}catch [IO.FileNotFoundException]{}catch [IO.DirectoryNotFoundException]{}
        if($null -ne $attrs -and ($attrs -band [IO.FileAttributes]::ReparsePoint)){throw 'Native upgrade path contains a reparse point.'}
        $attrs=$null;$current=[IO.Path]::GetDirectoryName($current)
    }
}
function Open-NativeUpgradeLease([string]$Root) {
    $root=[IO.Path]::GetFullPath($Root).TrimEnd('\')
    Assert-NativeUpgradePath $root
    if(-not [IO.Directory]::Exists($root)){throw 'Upgrade root unavailable.'}
    $lock=Join-Path $root 'native-upgrade.lock';Assert-NativeUpgradePath $lock
    $handle=[IO.FileStream]::new($lock,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    return [pscustomobject]@{Root=$root;Handle=$handle}
}
function Assert-NativeUpgradeLease($Lease) {
    if(!$Lease -or !$Lease.Handle -or !$Lease.Handle.CanWrite -or !$Lease.Root){throw 'Native upgrade lease required.'}
    Assert-NativeUpgradePath $Lease.Root
}
function Get-NativeUpgradeHash([string]$Path) {
    Assert-NativeUpgradePath $Path
    # An elevated Windows PowerShell launched from a .NET/pwsh host can inherit
    # a module path without Get-FileHash. Hashing must not depend on module
    # autoloading, and the image remains pinned against writes/deletion here.
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $sha=[Security.Cryptography.SHA256]::Create()
    try{return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$sha.Dispose();$stream.Dispose()}
}
function Get-NativeBundleSourcePins([string]$Root,$Manifest,[Collections.IDictionary]$Sources) {
    $base=[IO.Path]::GetFullPath($Root).TrimEnd('\');$pins=@{}
    foreach($name in $Sources.Keys){
        $relative=$Sources[$name]
        $manifestRows=@($Manifest.files|Where-Object {$_.path.Replace('/','\') -ieq $relative})
        # $Matches is an automatic PowerShell variable. A regex check must not
        # replace the manifest rows before their digest is captured.
        if($manifestRows.Count -ne 1 -or $manifestRows[0].sha256 -cnotmatch '^[A-F0-9]{64}$'){throw 'Native bundle manifest entry missing or duplicated.'}
        $pins[$name]=$manifestRows[0].sha256;$path=[IO.Path]::GetFullPath((Join-Path $base $relative))
        if(!$path.StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-NativeUpgradeHash $path) -cne $pins[$name]){throw 'Native bundle source digest mismatch.'}
    }
    return $pins
}
function Convert-NativeUpgradeEntries($Entries,[bool]$RequireNonempty=$false) {
    $items=@($Entries)
    if($items.Count -gt 4096 -or ($RequireNonempty -and $items.Count -lt 1)){throw 'Native inventory count refused.'}
    $map=[Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $items){
        if(!$entry -or $entry.path -isnot [string] -or !$entry.path -or [IO.Path]::IsPathRooted($entry.path) -or $entry.path.Contains('/') -or $entry.path.Contains(':') -or $entry.path -match '[\x00-\x1f<>"|?*]' -or $entry.sha256 -cnotmatch '^[A-F0-9]{64}$' -or ($entry.bytes -isnot [long] -and $entry.bytes -isnot [int]) -or $entry.bytes -lt 0){throw 'Invalid native inventory entry.'}
        foreach($segment in $entry.path.Split('\')){if(!$segment -or $segment -in @('.','..') -or $segment.EndsWith('.') -or $segment.EndsWith(' ')){throw 'Invalid native inventory path.'}}
        if($map.ContainsKey($entry.path)){throw 'Duplicate native inventory path.'}
        $map.Add($entry.path,[pscustomobject]@{path=$entry.path;bytes=[long]$entry.bytes;sha256=$entry.sha256})
    }
    return ,@($map.Values)
}
function Get-NativeUpgradeInventory([string]$Root) {
    return ,(Get-NativeUpgradeInventoryFromDirectory (Join-Path $Root 'native'))
}
function New-NativeUpgradeInput($Lease,[string]$Id,[string]$ApplicationRoot,$Service,$Intent,[string]$NewAgentSha,[string]$ArchiveSha,[ValidateSet('Disconnected','Unconfigured')][string]$ExpectedPhase='Disconnected') {
    Assert-NativeUpgradeLease $Lease
    if($Id -cnotmatch '^[a-f0-9]{32}$' -or !$Service -or $Service.ProcessId -le 0 -or $Service.StartedUtcTicks -le 0 -or $Service.StartType -cnotin @('Automatic','AutomaticDelayedStart','Manual','Disabled') -or !$Intent -or $Intent.Desired -isnot [bool] -or $Intent.Desired -or $Intent.Operation -cnotmatch '^[a-f0-9]{32}$' -or ($Intent.Generation -isnot [long] -and $Intent.Generation -isnot [int]) -or $Intent.Generation -lt 0 -or $NewAgentSha -cnotmatch '^[A-F0-9]{64}$' -or $ArchiveSha -cnotmatch '^[A-F0-9]{64}$'){throw 'Native upgrade input identity invalid.'}
    # Capture the returned array directly. @(<function returning ,array>) adds
    # a second level which survives JSON and breaks the separate worker process.
    $inventory=Get-NativeUpgradeInventory $Lease.Root
    return [ordered]@{Run=$Id;ApplicationRoot=[IO.Path]::GetFullPath($ApplicationRoot);OldAgentSha=(Get-NativeUpgradeHash (Join-Path $Lease.Root 'RoutePilot.Agent.exe'));OldNative=$inventory;OriginalService=@{ProcessId=$Service.ProcessId;StartedUtcTicks=$Service.StartedUtcTicks};OriginalStartType=$Service.StartType;Operation=$Intent.Operation;Generation=$Intent.Generation;ExpectedPhase=$ExpectedPhase;NewAgentSha=$NewAgentSha;ArchiveSha=$ArchiveSha}
}
function Get-NativeUpgradeInventoryFromDirectory([string]$Directory) {
    $native=[IO.Path]::GetFullPath($Directory).TrimEnd('\');Assert-NativeUpgradePath $native
    if(![IO.Directory]::Exists($native)){throw 'Native directory unavailable.'}
    $items=@(Get-ChildItem -LiteralPath $native -Force -Recurse -ErrorAction Stop)
    if(@($items|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Native inventory contains a reparse point.'}
    $files=@($items|Where-Object {!$_.PSIsContainer});if($files.Count -gt 4096){throw 'Native inventory exceeds limit.'}
    $rows=foreach($f in $files|Sort-Object FullName){
        $name=$f.FullName.Substring($native.Length+1);Assert-NativeUpgradePath $f.FullName
        [pscustomobject]@{path=$name;bytes=[long]$f.Length;sha256=(Get-NativeUpgradeHash $f.FullName)}
    }
    return ,(Convert-NativeUpgradeEntries @($rows))
}
function Assert-NativePreparationState($State) {
    if(!$State -or $State.Phase -cnotin @('Disconnected','Unconfigured') -or $State.Desired -isnot [bool] -or $State.Desired){throw 'Native connection must be disconnected before preparing an upgrade.'}
}
function New-NativePreparationStage([string]$Root,[string]$Id) {
    if($Id -cnotmatch '^[a-f0-9]{32}$'){throw 'Invalid native preparation identity.'}
    $rootPath=[IO.Path]::GetFullPath($Root).TrimEnd('\');Assert-NativeUpgradePath $rootPath
    if(![IO.Directory]::Exists($rootPath)){throw 'Native preparation root unavailable.'}
    $path=Join-Path $rootPath ('native-stage-'+$Id)
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -ine $rootPath){throw 'Native preparation path escaped root.'}
    New-Item -ItemType Directory -Path $path -ErrorAction Stop|Out-Null
    return $path
}
function Write-NativeUpgradeJournal([string]$Path,$Value) {
    Assert-NativeUpgradePath $Path
    $tmp=Join-Path ([IO.Path]::GetDirectoryName($Path)) ('.native-upgrade-'+[guid]::NewGuid().ToString('N')+'.tmp')
    try{
        $bytes=[Text.Encoding]::UTF8.GetBytes(($Value|ConvertTo-Json -Depth 8 -Compress))
        $f=[IO.FileStream]::new($tmp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
        try{$f.Write($bytes,0,$bytes.Length);$f.Flush($true)}finally{$f.Dispose()}
        if([IO.File]::Exists($Path)){[IO.File]::Replace($tmp,$Path,[NullString]::Value)}else{[IO.File]::Move($tmp,$Path)}
    }finally{if([IO.File]::Exists($tmp)){[IO.File]::Delete($tmp)}}
}
function Read-NativeUpgradeJournal($Lease) {
    Assert-NativeUpgradeLease $Lease;$path=Join-Path $Lease.Root 'native-upgrade.json';Assert-NativeUpgradePath $path
    if(![IO.File]::Exists($path)){throw 'Native upgrade journal unavailable.'}
    $f=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{if($f.Length -gt 4MB){throw 'Native upgrade journal too large.'};$r=[IO.StreamReader]::new($f,[Text.Encoding]::UTF8,$true,4096,$true);try{$j=$r.ReadToEnd()|ConvertFrom-Json}finally{$r.Dispose()}}finally{$f.Dispose()}
    if($j.schema -ne 1 -or $j.id -cnotmatch '^[a-f0-9]{32}$' -or $j.root -cne $Lease.Root -or $j.phase -notin @('Prepared','ServiceStopped','AgentPromoted','NativePromoted','Verified','Committed','Restoring','RolledBack','RecoveryFailed') -or $j.oldAgentSha -cnotmatch '^[A-F0-9]{64}$' -or $j.newAgentSha -cnotmatch '^[A-F0-9]{64}$' -or $j.archiveSha -cnotmatch '^[A-F0-9]{64}$' -or $j.ownerSid -notmatch '^S-1-(\d+-)+\d+$' -or $j.originalStartType -notin @('Automatic','Manual','Disabled','AutomaticDelayedStart') -or @($j.oldNative).Count -gt 4096){throw 'Invalid native upgrade journal.'}
    $j.oldNative=Convert-NativeUpgradeEntries @($j.oldNative)
    $j.newNative=Convert-NativeUpgradeEntries @($j.newNative) $true
    return $j
}
function Assert-NativeUpgradeMarker($Lease,[string]$Id) {
    if($Id -cnotmatch '^[a-f0-9]{32}$'){throw 'Invalid native upgrade identity.'}
    $path=Join-Path $Lease.Root 'native-upgrade.pending';Assert-NativeUpgradePath $path
    $stream=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{if($stream.Length -ne 33){throw 'Native upgrade marker size differs.'};$r=[IO.StreamReader]::new($stream,[Text.Encoding]::UTF8,$false,256,$true);try{$value=$r.ReadToEnd()}finally{$r.Dispose()};if($value -cne ($Id+"`n")){throw 'Native upgrade marker differs.'}}catch{$stream.Dispose();throw}
    return $stream
}
function Start-NativeUpgrade($Lease,[string]$Id,[string]$NewAgentSha,[string]$ArchiveSha,[string]$OwnerSid,[string]$OriginalStartType,$NewNativeEntries) {
    Assert-NativeUpgradeLease $Lease
    if($Id -cnotmatch '^[a-f0-9]{32}$' -or $NewAgentSha -cnotmatch '^[A-F0-9]{64}$' -or $ArchiveSha -cnotmatch '^[A-F0-9]{64}$' -or $OwnerSid -notmatch '^S-1-(\d+-)+\d+$' -or $OriginalStartType -notin @('Automatic','Manual','Disabled','AutomaticDelayedStart')){throw 'Invalid native upgrade parameters.'}
    $newNative=Convert-NativeUpgradeEntries $NewNativeEntries $true
    $marker=Join-Path $Lease.Root 'native-upgrade.pending';$journal=Join-Path $Lease.Root 'native-upgrade.json'
    Assert-NativeUpgradePath $marker;Assert-NativeUpgradePath $journal
    try{[void][IO.File]::GetAttributes($marker);throw 'Pending upgrade exists.'}catch [IO.FileNotFoundException]{}
    if([IO.File]::Exists($journal)){
        $previous=Read-NativeUpgradeJournal $Lease
        if($previous.phase -notin @('Committed','RolledBack') -or $previous.id -ceq $Id){throw 'Prior upgrade requires recovery.'}
        $history=Join-Path $Lease.Root ('native-upgrade-'+$previous.id+'.json');Assert-NativeUpgradePath $history
        [IO.File]::Move($journal,$history)
    }elseif([IO.Directory]::Exists($journal)){throw 'Invalid journal path.'}
    $agent=Join-Path $Lease.Root 'RoutePilot.Agent.exe';$old=Get-NativeUpgradeHash $agent;$inventory=Get-NativeUpgradeInventory $Lease.Root
    $j=[ordered]@{schema=1;id=$Id;root=$Lease.Root;phase='Prepared';oldAgentSha=$old;oldNative=@($inventory);newAgentSha=$NewAgentSha;newNative=@($newNative);archiveSha=$ArchiveSha;ownerSid=$OwnerSid;originalStartType=$OriginalStartType}
    Write-NativeUpgradeJournal $journal $j
    $f=[IO.FileStream]::new($marker,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
    try{$b=[Text.Encoding]::ASCII.GetBytes($Id+"`n");$f.Write($b,0,$b.Length);$f.Flush($true)}finally{$f.Dispose()}
    return (Read-NativeUpgradeJournal $Lease)
}
function Set-NativeUpgradePhase($Lease,[string]$Id,[string]$Phase) {
    $j=Read-NativeUpgradeJournal $Lease;if($j.id -cne $Id){throw 'Native upgrade id differs.'}
    $marker=Assert-NativeUpgradeMarker $Lease $Id
    try{
        $next=@{Prepared='ServiceStopped';ServiceStopped='AgentPromoted';AgentPromoted='NativePromoted';NativePromoted='Verified';Verified='Committed'}
        $allowed=($next.ContainsKey($j.phase) -and $next[$j.phase] -ceq $Phase) -or ($j.phase -in @('Prepared','ServiceStopped','AgentPromoted','NativePromoted','Verified','RecoveryFailed') -and $Phase -ceq 'Restoring') -or ($j.phase -ceq 'Restoring' -and $Phase -in @('RolledBack','RecoveryFailed'))
        if(!$allowed){throw 'Native upgrade transition refused.'}
        $j.phase=$Phase;Write-NativeUpgradeJournal (Join-Path $Lease.Root 'native-upgrade.json') $j
    }finally{$marker.Dispose()}
}
function Complete-NativeUpgrade($Lease,[string]$Id,[scriptblock]$VerifyActualPair) {
    if(!$VerifyActualPair){throw 'Actual pair verifier required.'}
    $j=Read-NativeUpgradeJournal $Lease;if($j.id -cne $Id -or $j.phase -notin @('Committed','RolledBack')){throw 'Native upgrade is not terminal.'}
    $marker=Assert-NativeUpgradeMarker $Lease $Id
    try{
        $journal=Join-Path $Lease.Root 'native-upgrade.json';$pin=Get-NativeUpgradeHash $journal
        $agent=Get-NativeUpgradeHash (Join-Path $Lease.Root 'RoutePilot.Agent.exe');$inventory=Get-NativeUpgradeInventory $Lease.Root
        $before=ConvertTo-Json -InputObject @($inventory) -Depth 5 -Compress
        if($j.phase -eq 'Committed' -and ($agent -cne $j.newAgentSha -or $before -cne (ConvertTo-Json -InputObject @($j.newNative) -Depth 5 -Compress))){throw 'Committed native pair differs.'}
        if($j.phase -eq 'RolledBack' -and ($agent -cne $j.oldAgentSha -or $before -cne (ConvertTo-Json -InputObject @($j.oldNative) -Depth 5 -Compress))){throw 'Restored native pair differs.'}
        $verified=& $VerifyActualPair $j $agent $inventory
        if($verified -isnot [bool] -or !$verified){throw 'Native pair verification refused.'}
        $after=Get-NativeUpgradeInventory $Lease.Root
        if((Get-NativeUpgradeHash $journal) -cne $pin -or (Get-NativeUpgradeHash (Join-Path $Lease.Root 'RoutePilot.Agent.exe')) -cne $agent -or (ConvertTo-Json -InputObject @($after) -Depth 5 -Compress) -cne $before){throw 'Native upgrade changed during verification.'}
    }finally{$marker.Dispose()}
    # Marker deletion is serialized by the exclusive lease; trusted root ACLs
    # and writer coordination are the caller's responsibility.
    $check=Assert-NativeUpgradeMarker $Lease $Id;$check.Dispose();[IO.File]::Delete((Join-Path $Lease.Root 'native-upgrade.pending'))
}
