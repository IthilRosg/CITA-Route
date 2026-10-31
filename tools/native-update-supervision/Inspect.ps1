param([switch]$Cleanup)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
$journal=Join-Path $workspace 'runtime/native-supervision-active.json';$entry=[IO.File]::ReadAllText($journal)|ConvertFrom-Json
if($entry.Run -cnotmatch '^[a-f0-9]{32}$' -or $entry.Root -cne ('C:\Program Files\RoutePilot\Agent\native-update-'+$entry.Run) -or $entry.PrimaryTask -cne ('CitaNativeSupervision-'+$entry.Run) -or $entry.BackupTask -cne ($entry.PrimaryTask+'-Backup')){throw 'Supervisor journal scope differs.'}
$script=@'
$ErrorActionPreference='Stop';$root='__ROOT__';$id='__ID__';$task='__TASK__'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -cne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong station.'}
$b=[IO.File]::ReadAllText((Join-Path $root 'baseline.json'))|ConvertFrom-Json
$path=Join-Path $root 'verified-backup.json'
if(!(Test-Path -LiteralPath $path)){
 $failures=@(Get-ChildItem -LiteralPath $root -Filter 'failure-*.json'|ForEach-Object {[IO.File]::ReadAllText($_.FullName)|ConvertFrom-Json})
 [pscustomobject]@{Run=$id;Complete=$false;Failures=$failures}|ConvertTo-Json -Depth 5 -Compress;return
}
$v=[IO.File]::ReadAllText($path)|ConvertFrom-Json
if($v.Run -cne $id -or !$v.Success -or $v.ActorProcesses -ne 0 -or $v.RecoveryProcesses -ne 0){throw 'Retirement/verification differs.'}
$s=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'"
$agent='C:\Program Files\RoutePilot\Agent'
if($s.State -cne 'Running' -or $s.ProcessId -ne $b.ServicePid -or $s.StartMode -cne $b.StartMode){throw 'Original service changed.'}
if((Get-FileHash -LiteralPath (Join-Path $agent 'RoutePilot.Agent.exe')).Hash -cne $b.AgentSha -or (Get-FileHash -LiteralPath (Join-Path $agent 'native\intent.json')).Hash -cne $b.IntentSha -or (Test-Path -LiteralPath (Join-Path $agent 'native-upgrade.pending'))){throw 'Original protected inputs changed.'}
$owned=@(Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -ieq (Join-Path $root 'Cita.NativeUpdater.exe') -or ($_.Name -ieq 'powershell.exe' -and $_.CommandLine -like ('*'+$root+'*'))}|Select-Object ProcessId,Name)
[pscustomobject]@{Run=$id;Complete=$true;Scope='No service mutation; actor+primary loss and backup retirement/verification';Verification=$v;OriginalServicePid=[int]$s.ProcessId;OriginalInputsUnchanged=$true;OwnedProcesses=$owned}|ConvertTo-Json -Depth 5 -Compress
'@
$script=$script.Replace('__ROOT__',$entry.Root).Replace('__ID__',$entry.Run).Replace('__TASK__',$entry.PrimaryTask)
$result=(Invoke-BoundedRm $script 30)|ConvertFrom-Json
$result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $workspace ('runtime/native-supervision-inspection-'+$entry.Run+'.json'))
if(!$Cleanup){$result|ConvertTo-Json -Depth 6;return}
if(!$result.Complete -or @($result.OwnedProcesses).Count -ne 0){throw 'Supervisor still active or unverified; cleanup refused.'}
$clean=@'
$ErrorActionPreference='Stop';$root='__ROOT__';$task='__TASK__';$id='__ID__'
$parent='C:\Program Files\RoutePilot\Agent'
if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($root)) -ine $parent -or [IO.Path]::GetFileName($root) -cne ('native-update-'+$id)){throw 'Cleanup escaped owned root.'}
foreach($name in @($task,($task+'-Backup'))){Disable-ScheduledTask -TaskName $name|Out-Null}
$owned=@(Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -ieq (Join-Path $root 'Cita.NativeUpdater.exe') -or ($_.Name -ieq 'powershell.exe' -and $_.CommandLine -like ('*'+$root+'*'))})
if($owned.Count){throw 'An owned process remains.'}
foreach($name in @($task,($task+'-Backup'))){if((Get-ScheduledTask -TaskName $name).State -eq 'Running'){throw 'A task is still running.'};Unregister-ScheduledTask -TaskName $name -Confirm:$false}
$items=@(Get-ChildItem -LiteralPath $root -Force)
$dir=Get-Item -LiteralPath $root -Force;if($dir.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Root linked.'}
foreach($item in $items){if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or [IO.Path]::GetDirectoryName($item.FullName) -ine $root){throw 'Unexpected cleanup child.'}}
foreach($item in $items){Remove-Item -LiteralPath $item.FullName -Force}
Remove-Item -LiteralPath $root
if((Test-Path -LiteralPath $root) -or @(Get-ScheduledTask|Where-Object {$_.TaskName -eq $task -or $_.TaskName -eq ($task+'-Backup')}).Count){throw 'Cleanup incomplete.'}
[pscustomobject]@{Run=$id;RootAbsent=$true;TasksAbsent=$true;OwnedProcesses=0}|ConvertTo-Json -Compress
'@
$clean=$clean.Replace('__ROOT__',$entry.Root).Replace('__ID__',$entry.Run).Replace('__TASK__',$entry.PrimaryTask)
$absence=(Invoke-BoundedRm $clean 30)|ConvertFrom-Json
$absence|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $workspace ('runtime/native-supervision-cleanup-'+$entry.Run+'.json'))
Move-Item -LiteralPath $journal -Destination (Join-Path $workspace ('runtime/native-supervision-completed-'+$entry.Run+'.json'))
$absence|ConvertTo-Json
