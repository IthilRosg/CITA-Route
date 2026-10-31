$ErrorActionPreference='Stop';$root='__ROOT__';$id='__ID__';$task='__TASK__';$agent='C:\Program Files\RoutePilot\Agent';$tasks=@($task,($task+'-Backup'))
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100' -or [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($root)) -ine $agent -or (Split-Path $root -Leaf) -cne ('cita-cycle-'+$id)){throw 'Cleanup identity refused'}
$item=Get-Item -LiteralPath $root -Force;if(!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Protected root refused'}
for($a=$item.Parent;$a;$a=$a.Parent){if($a.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Ancestor link refused'}}
$acl=Get-Acl -LiteralPath $root;$trusted=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted){throw 'Owner refused'}
foreach($r in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){if($r.AccessControlType -eq 'Allow' -and -not ($r.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and $r.IdentityReference.Value -notin $trusted -and (([int64]$r.FileSystemRights -band 0xffffffff) -band 0x500d0156)){throw 'ACL refused'}}
$files=@(Get-ChildItem -LiteralPath $root -Force);if(@($files|Where-Object {$_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint)}).Count){throw 'Root is not flat or contains links'}
function Actors {return @(Get-CimInstance Win32_Process|Where-Object {$_.ProcessId -ne $PID -and (($_.ExecutablePath -and $_.ExecutablePath.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) -or ($_.CommandLine -and $_.CommandLine.IndexOf($root,[StringComparison]::OrdinalIgnoreCase) -ge 0))})}
if(@(Actors).Count){throw 'Cycle actors remain'}
$result=[IO.File]::ReadAllText("$root\result-backup.json")|ConvertFrom-Json;$base=[IO.File]::ReadAllText("$root\baseline.json")|ConvertFrom-Json;$svc=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'"
if($result.run -cne $id -or !$result.success -or $result.jobActiveAfter -ne 0 -or !$svc -or $svc.State -ne 'Running' -or $svc.ProcessId -ne $result.servicePid -or (Get-FileHash -LiteralPath (Join-Path $agent 'native\intent.json')).Hash -cne $base.intentSha -or (Get-FileHash -LiteralPath (Join-Path $agent 'RoutePilot.Agent.exe')).Hash -cne $base.agentSha -or (Get-FileHash -LiteralPath "$root\Cita.PhysicalServiceCycle.exe").Hash -cne '__HELPER__'){throw 'Terminal recovery proof differs'}
foreach($name in $tasks){
 $v=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue;if(!$v){continue};$role=if($name -eq $task){'--primary'}else{'--backup'};$principal=[string]$v.Principal.UserId;$sid=if($principal -match '^S-1-'){$principal}else{([Security.Principal.NTAccount]::new($principal)).Translate([Security.Principal.SecurityIdentifier]).Value}
 if([string]$v.State -notin @('Ready','Disabled') -or $sid -ne 'S-1-5-18' -or $v.Actions.Count -ne 1 -or $v.Actions[0].Execute -ine "$root\Cita.PhysicalServiceCycle.exe" -or $v.Actions[0].Arguments -ne ($role+' "'+$root+'"')){throw 'Watcher identity or terminal state differs'}
}
foreach($name in $tasks){if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){Disable-ScheduledTask -TaskName $name|Out-Null}}
for($i=0;$i -lt 50;$i++){if(@($tasks|Where-Object {$v=Get-ScheduledTask -TaskName $_ -ErrorAction SilentlyContinue;$v -and [string]$v.State -notin @('Ready','Disabled')}).Count -eq 0){break};Start-Sleep -Milliseconds 100}
foreach($name in $tasks){$v=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue;if(!$v){continue};$role=if($name -eq $task){'--primary'}else{'--backup'};$principal=[string]$v.Principal.UserId;$sid=if($principal -match '^S-1-'){$principal}else{([Security.Principal.NTAccount]::new($principal)).Translate([Security.Principal.SecurityIdentifier]).Value};if([string]$v.State -ne 'Disabled' -or $sid -ne 'S-1-5-18' -or $v.Actions.Count -ne 1 -or $v.Actions[0].Execute -ine "$root\Cita.PhysicalServiceCycle.exe" -or $v.Actions[0].Arguments -ne ($role+' "'+$root+'"')){throw 'Disabled watcher identity unconfirmed'}}
if(@(Actors).Count){throw 'Cycle actors remain'}
foreach($name in $tasks){if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){Unregister-ScheduledTask -TaskName $name -Confirm:$false}}
if(@($tasks|Where-Object {Get-ScheduledTask -TaskName $_ -ErrorAction SilentlyContinue}).Count -or @(Actors).Count){throw 'Watcher removal unconfirmed'}
$item=Get-Item -LiteralPath $root -Force;if(!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.FullName -ine $root){throw 'Root changed'}
$files=@(Get-ChildItem -LiteralPath $root -Force);if(@($files|Where-Object {$_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint)}).Count){throw 'Root changed or is not flat'}
foreach($file in $files){if([IO.Path]::GetDirectoryName($file.FullName) -ine $root){throw 'Unexpected cleanup path'};Remove-Item -LiteralPath $file.FullName -Force}
Remove-Item -LiteralPath $root -Force
if(Test-Path -LiteralPath $root){throw 'Root removal unconfirmed'}
[pscustomobject]@{run=$id;success=$true;outcome='VerifiedCleanup';scope='Exact cycle root and watcher tasks only'}|ConvertTo-Json -Compress
