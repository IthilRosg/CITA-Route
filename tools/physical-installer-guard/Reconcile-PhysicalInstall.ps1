[CmdletBinding()]
param([switch]$CleanupVerified)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
$path=Join-Path $workspace 'runtime/installer-guard-install-active.json'
if(!(Test-Path -LiteralPath $path)){throw 'No retained installation journal.'}
$j=Get-Content -LiteralPath $path -Raw -Encoding UTF8|ConvertFrom-Json
if($j.schema -ne 1 -or $j.run -cnotmatch '^[a-f0-9]{32}$'){throw 'Invalid installation journal.'}
$root='C:\Program Files\RoutePilot\Agent\cita-installer-'+$j.run;$task='CitaInstallerRepair-'+$j.run
$taskNames=@($task,($task+'-Backup'));if($taskNames.Count -ne 2 -or $taskNames[0] -ne $task -or $taskNames[1] -ne ($task+'-Backup')){throw 'Watcher task name array differs.'}
if($j.root -ine $root -or $j.primaryTask -ne $task -or $j.backupTask -ne ($task+'-Backup')){throw 'Installation journal identity differs.'}
$probe=@'
$ErrorActionPreference='Stop';$root='__ROOT__';$task='__TASK__';$id='__ID__'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong station'}
$exists=Test-Path -LiteralPath $root
$names=@();if($exists){$item=Get-Item -LiteralPath $root -Force;if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Retained root is a link'};$names=@(Get-ChildItem -LiteralPath $root -Force|ForEach-Object {$_.Name})}
$tasks=@(foreach($name in @($task,($task+'-Backup'))){if($item=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){[pscustomobject]@{name=$name;state=[string]$item.State;lastResult=(Get-ScheduledTaskInfo -TaskName $name).LastTaskResult}}})
$processes=@(Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -ieq ($root+'\Cita.InstallerPhysical.exe') -or ($_.CommandLine -and $_.CommandLine.IndexOf($root,[StringComparison]::OrdinalIgnoreCase) -ge 0)}|ForEach-Object {[pscustomobject]@{pid=$_.ProcessId;name=$_.Name;created=$_.CreationDate}})
$results=@(foreach($role in @('primary','backup')){$p=Join-Path $root ('result-'+$role+'.json');if(Test-Path -LiteralPath $p){$v=[IO.File]::ReadAllText($p)|ConvertFrom-Json;if($v.run -ne $id){throw 'Retained result identity differs'};[pscustomobject]@{role=$role;success=[bool]$v.success;outcome=$v.outcome;jobActiveAfter=$v.jobActiveAfter}}})
$failure=$null;$failurePath=Join-Path $root 'start-failure.json';if(Test-Path -LiteralPath $failurePath){$v=[IO.File]::ReadAllText($failurePath)|ConvertFrom-Json;if($v.run -ne $id -or $v.errorType -cnotmatch '^[A-Za-z][A-Za-z0-9]{0,80}$'){throw 'Retained failure identity differs'};$failure=[pscustomobject]@{errorType=$v.errorType;line=[int]$v.line;utc=$v.utc}}
$transaction=$null;$file='C:\Users\CITASTATION\AppData\Local\Programs\CITA Route\client\runtime\installation-transaction.json'
if(Test-Path -LiteralPath $file){$v=[IO.File]::ReadAllText($file)|ConvertFrom-Json;$transaction=[pscustomobject]@{phase=$v.phase;isCurrent=($v.id -ceq $id)}}
[pscustomobject]@{run=$id;rootExists=$exists;names=$names;tasks=$tasks;processes=$processes;watcherResults=$results;startFailure=$failure;transaction=$transaction;scope='Read-only observation; no cleanup, restart or retry'}|ConvertTo-Json -Depth 6 -Compress
'@
$probe=$probe.Replace('__ROOT__',$root).Replace('__TASK__',$task).Replace('__ID__',$j.run)
$result=(Invoke-BoundedRm $probe)|ConvertFrom-Json
if($result.run -ne $j.run){throw 'Remote reconciliation identity differs.'}
if($CleanupVerified){
 $receiptPath=Join-Path $workspace 'runtime/installer-guard-install-result.json'
 if($j.state -ne 'VerifiedRetained' -or !(Test-Path -LiteralPath $receiptPath)){throw 'Cleanup requires a completed local installation receipt.'}
 $receipt=Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8|ConvertFrom-Json
 if($receipt.run -ne $j.run -or !$receipt.success -or $receipt.outcome -ne 'Committed' -or $receipt.journalId -cne $j.run -or $receipt.processesRemaining -ne 0 -or !$receipt.intentUnchanged -or !$receipt.servicePidUnchanged -or !$receipt.protectedModulesUnchanged -or $receipt.installerLogSha256 -notmatch '^[a-fA-F0-9]{64}$'){throw 'Installation cleanup receipt is unconfirmed.'}
 foreach($key in @('Cita.InstallerPhysical.exe','RestoreWorker.ps1','InstallationTransaction.ps1','Invoke-PhysicalRestore.ps1','setup.exe','payload-manifest.json')){if($receipt.componentHashes.$key -ne $j.hashes.$key){throw 'Installation cleanup component identity differs.'}}
 $receiptSha=(Get-FileHash -LiteralPath $receiptPath).Hash
 $evidencePath=Join-Path $workspace ('runtime/installer-guard-install-cleanup-evidence-'+$j.run+'.json')
 $evidence=[ordered]@{schema=1;run=$j.run;receiptSha256=$receiptSha;payloadManifestSha256=$j.hashes.'payload-manifest.json';installerSha256=$j.hashes.'setup.exe';installerLogSha256=$receipt.installerLogSha256;setupEvidence=$receipt.setupEvidence;guardEvidence=$receipt.guardEvidence;processes=$receipt.processes;taskResults=$receipt.taskResults;scope='Public client files only; no installer log content'}
 if(Test-Path -LiteralPath $evidencePath){$old=Get-Content -LiteralPath $evidencePath -Raw -Encoding UTF8|ConvertFrom-Json;if($old.run -ne $j.run -or $old.receiptSha256 -ne $receiptSha){throw 'Existing cleanup evidence differs.'}}
 else{$f=[IO.File]::Open($evidencePath,'CreateNew','Write','Read');try{$bytes=[Text.Encoding]::UTF8.GetBytes(($evidence|ConvertTo-Json -Depth 8 -Compress));$f.Write($bytes,0,$bytes.Length);$f.Flush($true)}finally{$f.Dispose()}}
 if(!$result.rootExists){
  $absence=@'
$ErrorActionPreference='Stop';$root='__ROOT__';$task='__TASK__';$id='__ID__'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong station'}
if((Test-Path -LiteralPath $root) -or @(@($task,($task+'-Backup'))|Where-Object {Get-ScheduledTask -TaskName $_ -ErrorAction SilentlyContinue}).Count){throw 'Root or watcher exists'}
$actors=@(Get-CimInstance Win32_Process|Where-Object {$_.ProcessId -ne $PID -and (($_.ExecutablePath -and $_.ExecutablePath.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) -or ($_.CommandLine -and $_.CommandLine.IndexOf($root,[StringComparison]::OrdinalIgnoreCase) -ge 0))});if($actors.Count){throw 'Actors remain'}
[pscustomobject]@{run=$id;outcome='AlreadyAbsent';rootRemaining=$false;tasksRemaining=$false;actorsRemaining=0;protectedModulesRechecked=$false;scope='Read-only absence proof after prior verified receipt'}|ConvertTo-Json -Compress
'@
  $absence=$absence.Replace('__ROOT__',$root).Replace('__TASK__',$task).Replace('__ID__',$j.run)
  $clean=(Invoke-BoundedRm $absence 30)|ConvertFrom-Json
 } else {
 $cleanup=@'
$ErrorActionPreference='Stop';$root='__ROOT__';$id='__ID__';$task='__TASK__';$parent='C:\Program Files\RoutePilot\Agent';$actual='C:\Users\CITASTATION\AppData\Local\Programs\CITA Route\client'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100' -or [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($root)) -ine $parent -or (Split-Path $root -Leaf) -cne ('cita-installer-'+$id)){throw 'Cleanup identity refused'}
function Assert-NoActors {
 $actors=@(Get-CimInstance Win32_Process|Where-Object {$_.ProcessId -ne $PID -and (($_.ExecutablePath -and $_.ExecutablePath.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) -or ($_.CommandLine -and $_.CommandLine.IndexOf($root,[StringComparison]::OrdinalIgnoreCase) -ge 0))})
 if($actors.Count){throw 'Installation actors remain'}
}
Assert-NoActors
$tasks=@($task,($task+'-Backup'));if($tasks.Count -ne 2 -or $tasks[1] -ne ($task+'-Backup')){throw 'Task array differs'}
foreach($name in $tasks){
 $item=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue;if(!$item){continue}
 $role=if($name -eq $task){'--primary'}else{'--backup'};$principal=[string]$item.Principal.UserId;$sid=if($principal -match '^S-1-'){$principal}else{([Security.Principal.NTAccount]::new($principal)).Translate([Security.Principal.SecurityIdentifier]).Value}
 if([string]$item.State -notin @('Ready','Disabled') -or $sid -ne 'S-1-5-18' -or $item.Actions.Count -ne 1 -or $item.Actions[0].Execute -ine "$root\Cita.InstallerPhysical.exe" -or $item.Actions[0].Arguments -ne ($role+' "'+$root+'"')){throw 'Watcher identity or terminal state differs'}
}
if(!(Test-Path -LiteralPath $root)){
 if(@($tasks|Where-Object {Get-ScheduledTask -TaskName $_ -ErrorAction SilentlyContinue}).Count){throw 'Root absent but watcher task remains'}
 [pscustomobject]@{run=$id;outcome='AlreadyAbsent';rootRemaining=$false;tasksRemaining=$false;actorsRemaining=0;protectedModulesRechecked=$false;scope='Read-only absence proof after prior verified receipt'}|ConvertTo-Json -Compress;return
}
$item=Get-Item -LiteralPath $root -Force;if(!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Protected root refused'}
for($ancestor=$item.Parent;$ancestor;$ancestor=$ancestor.Parent){if($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Protected root ancestor link refused'}}
$acl=Get-Acl -LiteralPath $root;$trusted=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted){throw 'Protected root owner refused'}
foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){if($rule.AccessControlType -eq 'Allow' -and -not ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and $rule.IdentityReference.Value -notin $trusted -and (([int64]$rule.FileSystemRights -band 0xffffffff) -band 0x500d0156)){throw 'Protected root ACL refused'}}
$children=@(Get-ChildItem -LiteralPath $root -Force)
if(@($children|Where-Object {$_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint)}).Count){throw 'Protected root is not flat or contains links'}
$plan=[IO.File]::ReadAllText("$root\plan.json")|ConvertFrom-Json;$baseline=[IO.File]::ReadAllText("$root\actual-baseline.json")|ConvertFrom-Json
if($plan.Run -ne $id -or $plan.Mode -ne 'Physical' -or $plan.PayloadManifestSha256 -ne '__PAYLOAD__' -or $plan.InstallerSha256 -ne '__INSTALLER__'){throw 'Protected plan differs'}
if((Get-FileHash -LiteralPath "$root\payload-manifest.json").Hash -ne '__PAYLOAD__' -or (Get-FileHash -LiteralPath "$root\setup.exe").Hash -ne '__INSTALLER__' -or (Get-FileHash -LiteralPath "$root\setup.log").Hash -ne '__LOGHASH__'){throw 'Protected payload or log differs'}
$processEvidence=[IO.File]::ReadAllText("$root\process-evidence.json")|ConvertFrom-Json
if($processEvidence.run -ne $id -or !$processEvidence.controllerStopped -or [bool]$processEvidence.setupInJob -ne ('__SETUPSEEN__' -eq 'True') -or [bool]$processEvidence.guardInJob -ne ('__GUARDSEEN__' -eq 'True')){throw 'Process evidence identity differs'}
$journalPath=Join-Path $actual 'runtime\installation-transaction.json';$transaction=[IO.File]::ReadAllText($journalPath)|ConvertFrom-Json
if($transaction.id -cne $id -or $transaction.phase -ne 'Committed' -or $transaction.incomingManifestHash -ne '__PAYLOAD__' -or (Get-FileHash -LiteralPath (Join-Path $actual 'manifest.json')).Hash -ne '__PAYLOAD__'){throw 'Current installation transaction differs'}
function NativeDigest {$native=Join-Path $parent 'native';$offset=$native.Length+1;$rows=New-Object 'System.Collections.Generic.List[string]';$items=@(Get-ChildItem -LiteralPath $native -Recurse -Force);if(@($items|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Native module link refused'};foreach($file in @($items|Where-Object {!$_.PSIsContainer}|Sort-Object FullName)){$relative=$file.FullName.Substring($offset);if($relative -ieq 'intent.json'){continue};$rows.Add($relative+'|'+$file.Length+'|'+(Get-FileHash -LiteralPath $file.FullName).Hash)};$sha=[Security.Cryptography.SHA256]::Create();try{return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($rows -join "`n"))))).Replace('-','')}finally{$sha.Dispose()}}
$svc=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'"
if(!$svc -or $svc.State -ne 'Running' -or $svc.ProcessId -ne $baseline.servicePid -or (Get-FileHash -LiteralPath (Join-Path $parent 'native\intent.json')).Hash -ne $baseline.intentSha -or (Get-FileHash -LiteralPath (Join-Path $parent 'RoutePilot.Agent.exe')).Hash -ne $baseline.agentSha -or (NativeDigest) -ne $baseline.nativeDigest){throw 'Protected service or module baseline changed'}
# Fence task restart before the final process checks and removal.
foreach($name in $tasks){if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){Disable-ScheduledTask -TaskName $name|Out-Null}}
for($i=0;$i -lt 50;$i++){if(@($tasks|Where-Object {$v=Get-ScheduledTask -TaskName $_ -ErrorAction SilentlyContinue;$v -and [string]$v.State -notin @('Ready','Disabled')}).Count -eq 0){break};Start-Sleep -Milliseconds 100}
foreach($name in $tasks){
 $v=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue;if(!$v){continue}
 $role=if($name -eq $task){'--primary'}else{'--backup'};$principal=[string]$v.Principal.UserId;$sid=if($principal -match '^S-1-'){$principal}else{([Security.Principal.NTAccount]::new($principal)).Translate([Security.Principal.SecurityIdentifier]).Value}
 if([string]$v.State -ne 'Disabled' -or $sid -ne 'S-1-5-18' -or $v.Actions.Count -ne 1 -or $v.Actions[0].Execute -ine "$root\Cita.InstallerPhysical.exe" -or $v.Actions[0].Arguments -ne ($role+' "'+$root+'"')){throw 'Disabled watcher identity unconfirmed'}
}
Assert-NoActors
foreach($name in $tasks){if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){Unregister-ScheduledTask -TaskName $name -Confirm:$false}}
if(@($tasks|Where-Object {Get-ScheduledTask -TaskName $_ -ErrorAction SilentlyContinue}).Count){throw 'Watcher removal unconfirmed'}
Assert-NoActors
$again=Get-Item -LiteralPath $root -Force;if(!$again.PSIsContainer -or ($again.Attributes -band [IO.FileAttributes]::ReparsePoint) -or [IO.Path]::GetFullPath($again.FullName) -ine $root){throw 'Protected root changed before removal'}
$children=@(Get-ChildItem -LiteralPath $root -Force)
if(@($children|Where-Object {$_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint)}).Count){throw 'Protected root changed or is not flat'}
foreach($child in $children){if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($child.FullName)) -ine $root){throw 'Unexpected cleanup file path'};Remove-Item -LiteralPath $child.FullName -Force}
Remove-Item -LiteralPath $root -Force
if((Test-Path -LiteralPath $root) -or @($tasks|Where-Object {Get-ScheduledTask -TaskName $_ -ErrorAction SilentlyContinue}).Count){throw 'Cleanup remains unconfirmed'}
[pscustomobject]@{run=$id;outcome='VerifiedCleanup';rootRemaining=$false;tasksRemaining=$false;actorsRemaining=0;protectedModulesRechecked=$true;scope='Verified installer evidence and protected scratch root only'}|ConvertTo-Json -Compress
'@
 foreach($pair in @(@('__ROOT__',$root),@('__TASK__',$task),@('__ID__',$j.run),@('__PAYLOAD__',$j.hashes.'payload-manifest.json'),@('__INSTALLER__',$j.hashes.'setup.exe'),@('__LOGHASH__',$receipt.installerLogSha256),@('__SETUPSEEN__',[string]($receipt.setupEvidence -eq 'ObservedInJob')),@('__GUARDSEEN__',[string]($receipt.guardEvidence -eq 'ObservedInJob')))){$cleanup=$cleanup.Replace($pair[0],$pair[1])}
 $cleanupName='cleanup-'+[Guid]::NewGuid().ToString('N')+'.ps1'
 $localCleanup=Join-Path $workspace ('runtime/'+$cleanupName)
 $cleanupFile=[IO.File]::Open($localCleanup,'CreateNew','Write','Read')
 try{$bytes=[Text.Encoding]::UTF8.GetBytes($cleanup);$cleanupFile.Write($bytes,0,$bytes.Length);$cleanupFile.Flush($true)}finally{$cleanupFile.Dispose()}
 try {
  $cleanupSha=(Get-FileHash -LiteralPath $localCleanup -Algorithm SHA256).Hash
  $pre=@'
$ErrorActionPreference='Stop';$root='__ROOT__';$file='__NAME__'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong station'}
$item=Get-Item -LiteralPath $root -Force;if(!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Root refused'}
for($a=$item.Parent;$a;$a=$a.Parent){if($a.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Ancestor link refused'}}
$acl=Get-Acl -LiteralPath $root;$trusted=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted){throw 'Owner refused'}
foreach($r in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){if($r.AccessControlType -eq 'Allow' -and -not ($r.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and $r.IdentityReference.Value -notin $trusted -and (([int64]$r.FileSystemRights -band 0xffffffff) -band 0x500d0156)){throw 'ACL refused'}}
if(Test-Path -LiteralPath (Join-Path $root $file)){throw 'Cleanup file already exists'};'ready'
'@
  $pre=$pre.Replace('__ROOT__',$root).Replace('__NAME__',$cleanupName)
  if((Invoke-BoundedRm $pre 30).Trim() -ne 'ready'){throw 'Cleanup transfer target unconfirmed.'}
  Invoke-BoundedScp $localCleanup ("citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/cita-installer-$($j.run)/$cleanupName") 45
  $remoteCleanup=Join-Path $root $cleanupName
  $invoke="`$p='$remoteCleanup';if((Get-FileHash -LiteralPath `$p -Algorithm SHA256).Hash -ne '$cleanupSha'){throw 'Cleanup script differs'};& ([ScriptBlock]::Create([IO.File]::ReadAllText(`$p)))"
  if([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($invoke)).Length -gt 6800){throw 'Cleanup invoke command exceeds transport budget.'}
  $clean=(Invoke-BoundedRm $invoke 60)|ConvertFrom-Json
 } finally {Remove-Item -LiteralPath $localCleanup -Force -ErrorAction SilentlyContinue}
 }
 if($clean.run -ne $j.run -or $clean.rootRemaining -ne $false -or $clean.tasksRemaining -ne $false -or $clean.actorsRemaining -ne 0){throw 'Cleanup remains unconfirmed; journal retained.'}
 $absencePath=Join-Path $workspace ('runtime/installer-guard-install-absence-'+$j.run+'.json')
 if(Test-Path -LiteralPath $absencePath){$prior=Get-Content -LiteralPath $absencePath -Raw -Encoding UTF8|ConvertFrom-Json;if($prior.run -ne $j.run -or $prior.rootRemaining -ne $false -or $prior.tasksRemaining -ne $false){throw 'Existing absence proof differs.'}}
 else{$f=[IO.File]::Open($absencePath,'CreateNew','Write','Read');try{$bytes=[Text.Encoding]::UTF8.GetBytes(($clean|ConvertTo-Json -Compress));$f.Write($bytes,0,$bytes.Length);$f.Flush($true)}finally{$f.Dispose()}}
 Remove-Item -LiteralPath $path -Force
 return $clean
}
$result
