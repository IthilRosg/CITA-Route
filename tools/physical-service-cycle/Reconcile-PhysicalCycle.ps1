[CmdletBinding()]
param([switch]$CleanupVerified,[switch]$ViaRadmin)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'));. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
$journal=Join-Path $workspace 'runtime/service-cycle-active.json';if(!(Test-Path -LiteralPath $journal)){throw 'No retained service cycle journal.'}
$j=Get-Content -LiteralPath $journal -Raw|ConvertFrom-Json
if($j.schema -ne 1 -or $j.run -cnotmatch '^[a-f0-9]{32}$' -or $j.root -cne ('C:\Program Files\RoutePilot\Agent\cita-cycle-'+$j.run) -or $j.primaryTask -cne ('CitaServiceCycle-'+$j.run) -or $j.backupTask -cne ($j.primaryTask+'-Backup')){throw 'Service cycle journal identity differs.'}
$probe=@'
$ErrorActionPreference='Stop';$root='__ROOT__';$task='__TASK__';$id='__ID__'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong station'}
$exists=Test-Path -LiteralPath $root
$tasks=@(foreach($name in @($task,($task+'-Backup'))){$v=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue;if($v){[pscustomobject]@{name=$name;state=[string]$v.State;lastResult=(Get-ScheduledTaskInfo -TaskName $name).LastTaskResult}}})
$actors=@(Get-CimInstance Win32_Process|Where-Object {$_.ProcessId -ne $PID -and (($_.ExecutablePath -and $_.ExecutablePath.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) -or ($_.CommandLine -and $_.CommandLine.IndexOf($root,[StringComparison]::OrdinalIgnoreCase) -ge 0))}|ForEach-Object {[pscustomobject]@{pid=$_.ProcessId;name=$_.Name}})
$results=@(foreach($role in @('primary','backup')){$p=Join-Path $root ('result-'+$role+'.json');if(Test-Path -LiteralPath $p){$v=[IO.File]::ReadAllText($p)|ConvertFrom-Json;if($v.run -cne $id){throw 'Result identity differs'};[pscustomobject]@{role=$role;success=[bool]$v.success;jobActiveAfter=$v.jobActiveAfter}}})
$failures=@(foreach($role in @('controller','actor','primary','backup')){$p=Join-Path $root ('failure-'+$role+'.json');if(Test-Path -LiteralPath $p){$v=[IO.File]::ReadAllText($p)|ConvertFrom-Json;if($v.run -cne $id -or $v.role -cne $role -or $v.errorType -cnotmatch '^[A-Za-z][A-Za-z0-9]{0,100}$' -or ($v.stage -and $v.stage -cnotin @('InitialReadiness','StartRefusal','SCMStop','Admission','ActorStart','RetireJob','RecoveryLock','SCMStart','StartupReady'))){throw 'Failure identity differs'};[pscustomobject]@{role=$role;stage=$v.stage;errorType=$v.errorType;hresult=$v.hresult;win32NativeCode=$v.win32NativeCode}}})
$svc=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'"
[pscustomobject]@{run=$id;rootExists=$exists;tasks=$tasks;actors=$actors;results=$results;failures=$failures;serviceState=$svc.State;servicePid=$svc.ProcessId;scope='Read-only; no retry or cleanup'}|ConvertTo-Json -Depth 6 -Compress
'@
$probe=$probe.Replace('__ROOT__',$j.root).Replace('__TASK__',$j.primaryTask).Replace('__ID__',$j.run)
$observation=(Invoke-BoundedRm $probe -ViaRadmin:$ViaRadmin)|ConvertFrom-Json
if($observation.run -cne $j.run){throw 'Remote observation identity differs.'}
if(!$CleanupVerified){return $observation}
if($j.state -cne 'VerifiedRetained'){throw 'Cleanup requires verified terminal receipt.'}
$resultPath=Join-Path $workspace ('runtime/service-cycle-result-'+$j.run+'.json')
if(!(Test-Path -LiteralPath $resultPath) -or (Get-FileHash -LiteralPath $resultPath).Hash -cne $j.resultSha256){throw 'Cycle result receipt differs.'}
$result=Get-Content -LiteralPath $resultPath -Raw|ConvertFrom-Json
if($result.run -cne $j.run -or !$result.success -or !$result.intentUnchanged -or !$result.agentUnchanged -or !$result.manifestUnchanged -or $result.jobActiveAfter -ne 0){throw 'Cycle result is unconfirmed.'}
if(!$observation.rootExists){
 if(@($observation.tasks).Count -or @($observation.actors).Count){throw 'Absent root has surviving actors.'}
 [pscustomobject]@{run=$j.run;outcome='AlreadyAbsent';scope='Read-only absence proof; no service revalidation'}|ConvertTo-Json -Compress|Set-Content -LiteralPath (Join-Path $workspace ('runtime/service-cycle-cleanup-'+$j.run+'.json')) -Encoding UTF8
 Remove-Item -LiteralPath $journal -Force;return
}
$script=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Cleanup-PhysicalCycle.ps1')).Replace('__ROOT__',$j.root).Replace('__ID__',$j.run).Replace('__TASK__',$j.primaryTask).Replace('__HELPER__',$j.helperSha256)
$name='cleanup-'+[guid]::NewGuid().ToString('N')+'.ps1';$local=Join-Path $workspace ('runtime/'+$name)
[IO.File]::WriteAllText($local,$script,[Text.UTF8Encoding]::new($false));$sha=(Get-FileHash -LiteralPath $local).Hash
try{
 $pre=@'
$ErrorActionPreference='Stop';$root='__ROOT__'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong station'}
$item=Get-Item -LiteralPath $root -Force;if(!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Root refused'}
for($a=$item.Parent;$a;$a=$a.Parent){if($a.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Ancestor link refused'}}
$acl=Get-Acl -LiteralPath $root;$trusted=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted){throw 'Owner refused'}
foreach($r in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){if($r.AccessControlType -eq 'Allow' -and -not ($r.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and $r.IdentityReference.Value -notin $trusted -and (([int64]$r.FileSystemRights -band 0xffffffff) -band 0x500d0156)){throw 'ACL refused'}};'ready'
'@
 if((Invoke-BoundedRm $pre.Replace('__ROOT__',$j.root) -ViaRadmin:$ViaRadmin).Trim() -ne 'ready'){throw 'Protected transfer root unconfirmed.'}
 Invoke-BoundedScp $local ("citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/cita-cycle-$($j.run)/$name") 45 $ViaRadmin
 $remote=Join-Path $j.root $name
 $result=(Invoke-BoundedRm "if((Get-FileHash -LiteralPath '$remote').Hash -ne '$sha'){throw 'Cleanup differs'};& ([ScriptBlock]::Create([IO.File]::ReadAllText('$remote')))" 60 -ViaRadmin:$ViaRadmin)|ConvertFrom-Json
 if($result.run -cne $j.run -or !$result.success){throw 'Cleanup unconfirmed; journal retained.'}
 $result|ConvertTo-Json -Compress|Set-Content -LiteralPath (Join-Path $workspace ('runtime/service-cycle-cleanup-'+$j.run+'.json')) -Encoding UTF8
 Remove-Item -LiteralPath $journal -Force
}finally{Remove-Item -LiteralPath $local -Force -ErrorAction SilentlyContinue}
$result
