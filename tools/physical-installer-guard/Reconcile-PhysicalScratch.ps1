[CmdletBinding()]
param([switch]$CleanupVerified)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
$path=Join-Path $workspace 'runtime/installer-guard-preflight-active.json'
if(!(Test-Path -LiteralPath $path)){throw 'No retained scratch preflight journal.'}
$j=Get-Content -LiteralPath $path -Raw -Encoding UTF8|ConvertFrom-Json
if($j.schema -ne 1 -or $j.run -cnotmatch '^[a-f0-9]{32}$'){throw 'Invalid preflight journal.'}
$root='C:\Program Files\RoutePilot\Agent\cita-installer-'+$j.run
$task='CitaInstallerScratch-'+$j.run
if($j.root -ine $root -or $j.primaryTask -ne $task -or $j.backupTask -ne ($task+'-Backup') -or $j.deniedTask -ne ($task+'-DeniedController')){throw 'Preflight journal identity differs.'}
$probe=@'
$ErrorActionPreference='Stop';$root='__ROOT__';$task='__TASK__';$id='__ID__'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong station'}
$directory=Test-Path -LiteralPath $root
$files=@();if($directory){$item=Get-Item -LiteralPath $root -Force;if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Retained root is a link'};$files=@(Get-ChildItem -LiteralPath $root -Force|ForEach-Object {$_.Name})}
$tasks=@(foreach($name in @($task,($task+'-Backup'),($task+'-DeniedController'))){if($item=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){[pscustomobject]@{name=$name;state=[string]$item.State;lastResult=(Get-ScheduledTaskInfo -TaskName $name).LastTaskResult}}})
$processes=@(Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -ieq ($root+'\Cita.InstallerPhysical.exe')}|ForEach-Object {[pscustomobject]@{pid=$_.ProcessId;created=$_.CreationDate}})
$result=$null;if(Test-Path -LiteralPath "$root\result-backup.json"){$raw=[IO.File]::ReadAllText("$root\result-backup.json")|ConvertFrom-Json;if($raw.run -ne $id){throw 'Retained result differs'};$result=[pscustomobject]@{success=[bool]$raw.success;outcome=$raw.outcome;jobActiveAfter=$raw.jobActiveAfter}}
[pscustomobject]@{run=$id;rootExists=$directory;names=$files;tasks=$tasks;processes=$processes;backupResult=$result;scope='Read-only observation; no cleanup or restart'}|ConvertTo-Json -Depth 5 -Compress
'@
$probe=$probe.Replace('__ROOT__',$root).Replace('__TASK__',$task).Replace('__ID__',$j.run)
$result=(Invoke-BoundedRm $probe)|ConvertFrom-Json
if($result.run -ne $j.run){throw 'Remote reconciliation identity differs.'}
if($CleanupVerified){
 $receiptPath=Join-Path $workspace 'runtime/installer-guard-preflight-result.json'
 if($j.state -ne 'VerifiedRetained' -or !(Test-Path -LiteralPath $receiptPath)){throw 'Cleanup requires a completed local preflight receipt.'}
 $receipt=Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8|ConvertFrom-Json
 if($receipt.run -ne $j.run -or !$receipt.success -or !$receipt.systemControllerRefused -or $receipt.outcome -ne 'RolledBack' -or $receipt.jobActiveAfter -ne 0 -or !$receipt.actualAppUnchanged -or !$receipt.intentUnchanged -or !$receipt.servicePidUnchanged){throw 'Cleanup receipt unconfirmed.'}
 $cleanup=@'
$ErrorActionPreference='Stop';$root='__ROOT__';$id='__ID__';$task='__TASK__';$parent='C:\Program Files\RoutePilot\Agent'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100' -or [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($root)) -ine $parent -or (Split-Path $root -Leaf) -cne ('cita-installer-'+$id)){throw 'Cleanup identity refused'}
if(Get-ScheduledTask -TaskName ($task+'-DeniedController') -ErrorAction SilentlyContinue){throw 'Negative-test task still exists'}
function Assert-NoActors {if(@(Get-CimInstance Win32_Process|Where-Object {$_.ProcessId -ne $PID -and (($_.ExecutablePath -and $_.ExecutablePath.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) -or ($_.CommandLine -and $_.CommandLine.IndexOf($root,[StringComparison]::OrdinalIgnoreCase) -ge 0))}).Count){throw 'Scratch processes still exist'}}
Assert-NoActors
foreach($name in @($task,($task+'-Backup'))){
 $item=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue;if(!$item){continue}
 $role=if($name -eq $task){'--primary'}else{'--backup'};$principal=[string]$item.Principal.UserId;$sid=if($principal -match '^S-1-'){$principal}else{([Security.Principal.NTAccount]::new($principal)).Translate([Security.Principal.SecurityIdentifier]).Value}
 if([string]$item.State -notin @('Ready','Disabled') -or $sid -ne 'S-1-5-18' -or $item.Actions.Count -ne 1 -or $item.Actions[0].Execute -ine "$root\Cita.InstallerPhysical.exe" -or $item.Actions[0].Arguments -ne ($role+' "'+$root+'"')){throw 'Scratch task identity or state differs'}
}
if(Test-Path -LiteralPath $root){
 if((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Scratch root link refused'}
 if(@(Get-ChildItem -LiteralPath $root -Recurse -Force|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Scratch subtree contains links'}
}
foreach($name in @($task,($task+'-Backup'))){if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){Disable-ScheduledTask -TaskName $name|Out-Null}}
for($i=0;$i -lt 50;$i++){if(@(@($task,($task+'-Backup'))|Where-Object {$v=Get-ScheduledTask -TaskName $_ -ErrorAction SilentlyContinue;$v -and [string]$v.State -notin @('Ready','Disabled')}).Count -eq 0){break};Start-Sleep -Milliseconds 100}
foreach($name in @($task,($task+'-Backup'))){
 $v=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue;if(!$v){continue}
 $role=if($name -eq $task){'--primary'}else{'--backup'};$principal=[string]$v.Principal.UserId;$sid=if($principal -match '^S-1-'){$principal}else{([Security.Principal.NTAccount]::new($principal)).Translate([Security.Principal.SecurityIdentifier]).Value}
 if([string]$v.State -ne 'Disabled' -or $sid -ne 'S-1-5-18' -or $v.Actions.Count -ne 1 -or $v.Actions[0].Execute -ine "$root\Cita.InstallerPhysical.exe" -or $v.Actions[0].Arguments -ne ($role+' "'+$root+'"')){throw 'Disabled scratch watcher identity unconfirmed'}
}
Assert-NoActors
foreach($name in @($task,($task+'-Backup'))){if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){Unregister-ScheduledTask -TaskName $name -Confirm:$false}}
if(@(@($task,($task+'-Backup'))|Where-Object {Get-ScheduledTask -TaskName $_ -ErrorAction SilentlyContinue}).Count){throw 'Scratch task removal unconfirmed'}
Assert-NoActors
if(Test-Path -LiteralPath $root){$again=Get-Item -LiteralPath $root -Force;if(!$again.PSIsContainer -or ($again.Attributes -band [IO.FileAttributes]::ReparsePoint) -or [IO.Path]::GetFullPath($again.FullName) -ine $root){throw 'Scratch root changed before removal'};if(@(Get-ChildItem -LiteralPath $root -Recurse -Force|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Scratch subtree changed or contains links'};Remove-Item -LiteralPath $root -Recurse -Force}
if((Test-Path -LiteralPath $root) -or (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) -or (Get-ScheduledTask -TaskName ($task+'-Backup') -ErrorAction SilentlyContinue)){throw 'Scratch cleanup unconfirmed'}
[pscustomobject]@{run=$id;rootRemaining=$false;tasksRemaining=$false;scope='Verified scratch cleanup only'}|ConvertTo-Json -Compress
'@
 $cleanup=$cleanup.Replace('__ROOT__',$root).Replace('__TASK__',$task).Replace('__ID__',$j.run)
 $clean=(Invoke-BoundedRm $cleanup 60)|ConvertFrom-Json
 if($clean.run -ne $j.run -or $clean.rootRemaining -ne $false -or $clean.tasksRemaining -ne $false){throw 'Cleanup remains unconfirmed; journal retained.'}
 Remove-Item -LiteralPath $path -Force
 return $clean
}
$result
