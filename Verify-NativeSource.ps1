param([string]$Receipt='runtime\native-corresponding-source-current.zip.receipt.json')
$ErrorActionPreference='Stop';$root=$PSScriptRoot
. (Join-Path $root 'NativeSourceProvenance.ps1')
$receiptPath=[IO.Path]::GetFullPath($(if([IO.Path]::IsPathRooted($Receipt)){$Receipt}else{Join-Path $root $Receipt}));$allowed=(Join-Path $root 'runtime')+'\'
if(-not $receiptPath.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Source receipt must stay inside runtime'}
$source=Get-Content -LiteralPath $receiptPath -Raw|ConvertFrom-Json
if(!$source.workspaceInputs){throw 'Verified native workspace inventory missing'}
Assert-NativeInventory $source.workspaceInputs @(Get-NativeWorkspaceInventory $root)
if(-not ([IO.Path]::GetFullPath($source.path)).StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or (Get-FileHash $source.path).Hash -ne $source.sha256){throw 'Corresponding-source archive differs'}
$expected=@{};foreach($pair in @(@('native-core.exe','native-core-lab.exe'),@('native-owner.exe','native-awg-lab.exe'),@('native-guard.exe','native-guard-lab.exe'))){$expected[$pair[0]]=(Get-FileHash (Join-Path $root ('runtime/'+$pair[1]))).Hash}
if($source.coreBinarySha256 -ne $expected['native-core.exe']){throw 'Source core does not match accepted binary'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($source.path)
try{
 $names=@{};$archiveFiles=@{};$total=0L
 foreach($entry in $zip.Entries){$name=$entry.FullName.Replace('\','/');$segments=$name.TrimEnd('/').Split('/');if($name.StartsWith('/') -or $name.Contains(':') -or $name -match '(^|/)\.\.(/|$)' -or @($segments|Where-Object {$_ -eq '' -or $_ -eq '.' -or $_.EndsWith('.') -or $_.EndsWith(' ') -or $_ -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)'}).Count -gt 0 -or $names.ContainsKey($name) -or (($entry.ExternalAttributes -shr 16) -band 61440) -eq 40960){throw 'Unsafe source archive path'};$names[$name]=$true;if(-not $name.EndsWith('/')){$archiveFiles[$name]=$true};$total+=$entry.Length}
 if($names.Count -gt 30000 -or $total -gt 2147483648){throw 'Source archive budget exceeded'}
}finally{$zip.Dispose()}
$stage=Join-Path $root ('runtime/native-source-verification-'+[guid]::NewGuid().ToString('N'));Expand-Archive -LiteralPath $source.path -DestinationPath $stage
$manifest=Get-Content (Join-Path $stage 'SOURCE-MANIFEST.json') -Raw|ConvertFrom-Json
$listed=@{};foreach($file in $manifest.files){$name=([string]$file.name).Replace('\','/');if(!$name -or $listed.ContainsKey($name) -or !$archiveFiles.ContainsKey($name) -or $name -ieq 'SOURCE-MANIFEST.json'){throw 'Extracted source inventory differs'};$listed[$name]=$true;$path=[IO.Path]::GetFullPath((Join-Path $stage $name));if(-not $path.StartsWith($stage+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-FileHash -LiteralPath $path).Hash -ne $file.sha256){throw 'Extracted source inventory differs'}}
if(!$archiveFiles.ContainsKey('SOURCE-MANIFEST.json') -or $archiveFiles.Count -ne ($listed.Count+1)){throw 'Extracted source inventory differs'}
# The saved recipe uses only this archive's file:// module proxy and local Go.
$env:GOCACHE=Join-Path $root 'downloads/go-buildcache';$env:GOFLAGS='-p=2';$env:GOMAXPROCS='2'
& (Join-Path $stage 'BUILD.ps1');if($LASTEXITCODE -ne 0){throw 'Offline source rebuild failed'}
foreach($name in $expected.Keys){if((Get-FileHash (Join-Path $stage $name)).Hash -ne $expected[$name]){throw ('Reproduced binary differs: '+$name)}}
$source|Add-Member -Force -NotePropertyName rebuildVerified -NotePropertyValue $true
$source|Add-Member -Force -NotePropertyName verifiedBinaries -NotePropertyValue $expected
$source|Add-Member -Force -NotePropertyName verificationStage -NotePropertyValue $stage
$source|Add-Member -Force -NotePropertyName verifiedUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('o'))
Assert-NativeInventory $source.workspaceInputs @(Get-NativeWorkspaceInventory $root)
$source|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $receiptPath -Encoding UTF8
'PASS: all three native binaries reproduced from the pinned source archive using its offline module proxy.'
