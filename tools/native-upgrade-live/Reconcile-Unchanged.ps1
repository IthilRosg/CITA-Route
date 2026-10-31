param([switch]$Cleanup,[switch]$VerifyPublicArchiveStage)
$ErrorActionPreference='Stop';$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
$journal=Join-Path $workspace 'runtime/native-upgrade-live-active.json';$e=[IO.File]::ReadAllText($journal)|ConvertFrom-Json
if($e.Run -cnotmatch '^[a-f0-9]{32}$' -or $e.Root -cne ('C:\Program Files\RoutePilot\Agent\native-update-'+$e.Run)){throw 'Live root scope differs.'}
$code=@'
$ErrorActionPreference='Stop';$r='__ROOT__';$id='__ID__';$agent='C:\Program Files\RoutePilot\Agent'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -cne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong station.'}
$plan=[IO.File]::ReadAllText((Join-Path $r 'plan.json'))|ConvertFrom-Json
foreach($pin in $plan.Files.PSObject.Properties){if((Get-FileHash -LiteralPath (Join-Path $r $pin.Name)).Hash -cne $pin.Value){throw 'Failed run input changed.'}}
. ([ScriptBlock]::Create([IO.File]::ReadAllText((Join-Path $r 'NativeUpgradeTransaction.ps1'))))
$before=[IO.File]::ReadAllText((Join-Path $r 'upgrade-input.json'))|ConvertFrom-Json
$rows=@($before.OldNative);$nested=$false
if($rows.Count -eq 1 -and $rows[0] -is [array]){$rows=@($rows[0]);$nested=$true}
$expected=Convert-NativeUpgradeEntries $rows $true
$actual=Get-NativeUpgradeInventory $agent
if((ConvertTo-Json -InputObject @($expected) -Depth 6 -Compress) -cne (ConvertTo-Json -InputObject @($actual) -Depth 6 -Compress)){throw 'Protected native baseline changed.'}
if((Get-FileHash -LiteralPath (Join-Path $agent 'RoutePilot.Agent.exe')).Hash -cne $before.OldAgentSha -or (Get-FileHash -LiteralPath (Join-Path $agent 'native\intent.json')).Hash -cne $plan.IntentSha){throw 'Protected pair changed.'}
foreach($path in @('native-upgrade.pending','native-upgrade.json',('native-upgrade-'+$id+'-agent-stage.exe'))){if(Test-Path -LiteralPath (Join-Path $agent $path)){throw 'Native mutation or agent staging was observed; this cleanup is ineligible.'}}
$stage=Join-Path $agent ('native-stage-'+$id);$verifiedStage=$false
if(Test-Path -LiteralPath $stage){
 if(!__VERIFY_STAGE__){throw 'Preparation stage exists; explicit verification required.'}
 $preparing=[IO.File]::ReadAllText((Join-Path $r 'preparing.json'))|ConvertFrom-Json
 if($preparing.Run -cne $id -or !$preparing.StageWasAbsent){throw 'Owned stage creation proof missing.'}
 Assert-NativeUpgradePath $stage
 Add-Type -AssemblyName System.IO.Compression
 $stream=[IO.File]::Open((Join-Path $r 'native-components.zip'),'Open','Read','Read');$archive=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Read,$true)
 try{
  $members=@{};foreach($member in $archive.Entries){$members[$member.FullName]=$member}
  foreach($f in Get-ChildItem -LiteralPath $stage -Recurse -Force){
   if($f.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Stage linked.'};if($f.PSIsContainer){continue}
   $name=$f.FullName.Substring($stage.Length+1).Replace('\','/');$member=$members[$name]
   if(!$member -or $member.Length -ne $f.Length){throw 'Stage contains non-public or unknown contents.'}
   $content=$member.Open();$sha=[Security.Cryptography.SHA256]::Create()
   try{$hash=([BitConverter]::ToString($sha.ComputeHash($content))).Replace('-','')}finally{$content.Dispose();$sha.Dispose()}
   if((Get-FileHash -LiteralPath $f.FullName).Hash -cne $hash){throw 'Stage differs from immutable public archive.'}
  };$verifiedStage=$true
 }finally{$archive.Dispose();$stream.Dispose()}
}
$s=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'"
if($s.State -cne 'Running' -or $s.ProcessId -ne $before.OriginalService.ProcessId){throw 'Original service changed.'}
$processes=@(Get-CimInstance Win32_Process -Filter "Name='Cita.NativeUpdater.exe' OR Name='powershell.exe'"|Where-Object {$_.ExecutablePath -ieq (Join-Path $r 'Cita.NativeUpdater.exe') -or ($_.Name -ieq 'powershell.exe' -and $_.CommandLine -like ('*'+$r+'*'))})
if($processes.Count){throw 'An owned live process remains.'}
[pscustomobject]@{Run=$id;NativeFiles=@($actual).Count;SameOldPair=$true;SameServicePid=[int]$s.ProcessId;PublicArchiveStageVerified=$verifiedStage;MalformedNestedInventory=$nested;OwnedProcesses=0}|ConvertTo-Json -Compress
'@
$verifyLiteral=if($VerifyPublicArchiveStage){'$true'}else{'$false'}
$proof=(Invoke-BoundedRm $code.Replace('__ROOT__',$e.Root).Replace('__ID__',$e.Run).Replace('__VERIFY_STAGE__',$verifyLiteral) 30)|ConvertFrom-Json
$proof|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $workspace ('runtime/native-upgrade-unchanged-'+$e.Run+'.json'))
if(!$Cleanup){$proof;return}
$cleanupCode=@'
$ErrorActionPreference='Stop';$root='__ROOT__';$id='__ID__';$task='CitaNativeUpgrade-'+$id
if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($root)) -ine 'C:\Program Files\RoutePilot\Agent' -or [IO.Path]::GetFileName($root) -cne ('native-update-'+$id)){throw 'Cleanup escaped owned root.'}
$names=@($task,($task+'-Backup'),($task+'-Boot'),($task+'-Controller'));foreach($name in $names){if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){Disable-ScheduledTask -TaskName $name|Out-Null}}
if(@(Get-CimInstance Win32_Process -Filter "Name='Cita.NativeUpdater.exe' OR Name='powershell.exe'"|Where-Object {$_.ExecutablePath -ieq (Join-Path $root 'Cita.NativeUpdater.exe') -or ($_.Name -ieq 'powershell.exe' -and $_.CommandLine -like ('*'+$root+'*'))}).Count){throw 'A process remains.'}
foreach($name in $names){$t=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue;if($t){if($t.State -eq 'Running'){throw 'Task remains running.'};Unregister-ScheduledTask -TaskName $name -Confirm:$false}}
$stage=Join-Path 'C:\Program Files\RoutePilot\Agent' ('native-stage-'+$id)
if(Test-Path -LiteralPath $stage){
 if(!__STAGE_VERIFIED__){throw 'Stage was not independently verified.'}
 if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($stage)) -ine 'C:\Program Files\RoutePilot\Agent' -or [IO.Path]::GetFileName($stage) -cne ('native-stage-'+$id)){throw 'Stage cleanup escaped scope.'}
 $children=@(Get-ChildItem -LiteralPath $stage -Recurse -Force);foreach($f in $children){if($f.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Stage child linked.'}}
 foreach($f in @($children|Where-Object {!$_.PSIsContainer})){Remove-Item -LiteralPath $f.FullName -Force}
 foreach($d in @($children|Where-Object {$_.PSIsContainer}|Sort-Object {$_.FullName.Length} -Descending)){Remove-Item -LiteralPath $d.FullName}
 Remove-Item -LiteralPath $stage
}
$items=@(Get-ChildItem -LiteralPath $root -Force);$dir=Get-Item -LiteralPath $root -Force
if($dir.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Root linked.'}
foreach($f in $items){if($f.PSIsContainer -or ($f.Attributes -band [IO.FileAttributes]::ReparsePoint) -or [IO.Path]::GetDirectoryName($f.FullName) -ine $root){throw 'Unexpected cleanup child.'}}
foreach($f in $items){Remove-Item -LiteralPath $f.FullName -Force};Remove-Item -LiteralPath $root
foreach($suffix in @('.log','.log.err')){$p=Join-Path $env:TEMP ('cita-native-start-'+$id+$suffix);if(Test-Path -LiteralPath $p){if((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Log linked.'};Remove-Item -LiteralPath $p -Force}}
if(Test-Path -LiteralPath $root){throw 'Root remains.'}
[pscustomobject]@{Run=$id;RootAbsent=$true;TasksAbsent=$true;OwnedProcesses=0}|ConvertTo-Json -Compress
'@
$stageLiteral=if($proof.PublicArchiveStageVerified){'$true'}else{'$false'}
$absence=(Invoke-BoundedRm $cleanupCode.Replace('__ROOT__',$e.Root).Replace('__ID__',$e.Run).Replace('__STAGE_VERIFIED__',$stageLiteral) 30)|ConvertFrom-Json
$absence|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $workspace ('runtime/native-upgrade-live-cleanup-'+$e.Run+'.json'))
Move-Item -LiteralPath $journal -Destination (Join-Path $workspace ('runtime/native-upgrade-live-failed-'+$e.Run+'.json'))
$absence
