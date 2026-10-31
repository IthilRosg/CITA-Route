[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
$journal=Join-Path $workspace 'runtime/installer-guard-preflight-active.json'
if(Test-Path -LiteralPath $journal){throw 'Earlier preflight requires reconciliation; no new run was started.'}
$sources=[ordered]@{
 'Cita.InstallerPhysical.exe'=(Join-Path $workspace 'runtime/physical-installer-physical-build/Cita.InstallerPhysical.exe')
 'RestoreWorker.ps1'=(Join-Path $PSScriptRoot 'RestoreWorker.ps1')
 'InstallationTransaction.ps1'=(Join-Path $workspace 'installer/InstallationTransaction.ps1')
 'Invoke-PhysicalRestore.ps1'=(Join-Path $PSScriptRoot 'physical/Invoke-PhysicalRestore.ps1')
}
$hashes=[ordered]@{};foreach($key in $sources.Keys){$hashes[$key]=(Get-FileHash -LiteralPath $sources[$key]).Hash}
$recipe=Join-Path $workspace 'tools/native-control-probe/Test-PhysicalTransfer.ps1'
if((Get-FileHash -LiteralPath $recipe).Hash -ne '42CB3AA03261C76905617CAE5EBFAFE3F927229E5672B3278B4EFC9A6758BECE'){throw 'Protected directory recipe differs.'}
$create=[regex]::Match([IO.File]::ReadAllText($recipe),"(?s)\`$create=@'\r?\n(.*?)\r?\n'@").Groups[1].Value
if(!$create){throw 'Protected directory recipe missing.'}
$id=[guid]::NewGuid().ToString('N');$folder='cita-installer-'+$id
$remote='C:\Program Files\RoutePilot\Agent\'+$folder
$task='CitaInstallerScratch-'+$id;$backup=$task+'-Backup';$deniedTask=$task+'-DeniedController'
$taskNames=@($task,$backup);if($taskNames.Count -ne 2 -or $taskNames[0] -ne $task -or $taskNames[1] -ne ($task+'-Backup')){throw 'Watcher task name array differs.'}
$entry=[ordered]@{schema=1;run=$id;root=$remote;primaryTask=$task;backupTask=$backup;deniedTask=$deniedTask;hashes=$hashes;state='MayCreate';utc=[DateTimeOffset]::UtcNow.ToString('o')}
$f=[IO.File]::Open($journal,'CreateNew','Write','Read');try{$bytes=[Text.Encoding]::UTF8.GetBytes(($entry|ConvertTo-Json -Depth 5 -Compress));$f.Write($bytes,0,$bytes.Length);$f.Flush($true)}finally{$f.Dispose()}
if((Invoke-BoundedRm $create.Replace('__DIR__',$remote)).Trim() -ne 'ready'){throw 'Exclusive remote root is unconfirmed.'}
foreach($key in $sources.Keys){Invoke-BoundedScp $sources[$key] ("citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/$folder/$key")}
$start=@'
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong station'}
$root='__ROOT__';$id='__ID__';$task='__TASK__';$backup=$task+'-Backup';$agent='C:\Program Files\RoutePilot\Agent'
$expected=@{'Cita.InstallerPhysical.exe'='__HELPER__';'RestoreWorker.ps1'='__WORKER__';'InstallationTransaction.ps1'='__TX__';'Invoke-PhysicalRestore.ps1'='__INVOKER__'}
foreach($name in $expected.Keys){$file=Get-Item -LiteralPath (Join-Path $root $name) -Force;if($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne $expected[$name]){throw 'Transferred component differs'}}
function Protect($path){
 $item=Get-Item -LiteralPath $path -Force;if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Protected link refused'}
 if($item.PSIsContainer){$acl=New-Object Security.AccessControl.DirectorySecurity;$inherit='ContainerInherit,ObjectInherit'}else{$acl=New-Object Security.AccessControl.FileSecurity;$inherit='None'}
 $admin=New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544');$acl.SetOwner($admin);$acl.SetAccessRuleProtection($true,$false)
 foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl',$inherit,'None','Allow')))}
 Set-Acl -LiteralPath $path -AclObject $acl
}
Protect $root;foreach($name in $expected.Keys){Protect (Join-Path $root $name)}
$config=[IO.File]::ReadAllText((Join-Path $agent 'agent-config.json'))|ConvertFrom-Json
$actual='C:\Users\CITASTATION\AppData\Local\Programs\CITA Route\client'
if($config.UserSid -cnotmatch '^S-1-5-21-(\d+-){3}\d+$' -or [IO.Path]::GetFullPath($config.AppRoot).TrimEnd('\') -ine $actual){throw 'Protected client ownership differs'}
$intent=Join-Path $agent 'native\intent.json';$svc=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'"
if(!$svc -or $svc.State -ne 'Running' -or !$svc.ProcessId){throw 'Protected service unavailable'}
$baseline=[ordered]@{appSha=(Get-FileHash -LiteralPath (Join-Path $actual 'RoutePilot.exe')).Hash;manifestSha=(Get-FileHash -LiteralPath (Join-Path $actual 'manifest.json')).Hash;intentSha=(Get-FileHash -LiteralPath $intent).Hash;servicePid=[int]$svc.ProcessId}
[IO.File]::WriteAllText((Join-Path $root 'actual-baseline.json'),($baseline|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false));Protect (Join-Path $root 'actual-baseline.json')
$client=Join-Path $root 'scratch\client';$runtime=Join-Path $client 'runtime';[void][IO.Directory]::CreateDirectory($runtime)
$files=@();for($i=0;$i -lt 20;$i++){
 $name=if($i -eq 0){'example.txt'}else{'dummy-'+$i+'.txt'};$path=Join-Path $client $name
 [IO.File]::WriteAllText($path,'old',[Text.UTF8Encoding]::new($false));$files+=@{path=$name;bytes=(Get-Item -LiteralPath $path).Length;sha256=(Get-FileHash -LiteralPath $path).Hash}
}
$manifest=Join-Path $client 'manifest.json';[IO.File]::WriteAllText($manifest,(@{version='0.6.9';architecture='win-x64';files=$files}|ConvertTo-Json -Depth 8 -Compress),[Text.UTF8Encoding]::new($false))
$oldHash=(Get-FileHash -LiteralPath $manifest).Hash;$oldId=[guid]::NewGuid().ToString('N');if($oldId -ceq $id){throw 'Transaction identities collide'}
$oldJournal=@{schema=1;id=$oldId;root=$client;userSid=$config.UserSid;backup=(Join-Path $runtime ('installation-backup-'+$oldId));phase='Committed';files=@();version='0.6.9';incomingManifestHash=$oldHash}
[IO.File]::WriteAllText((Join-Path $runtime 'installation-transaction.json'),($oldJournal|ConvertTo-Json -Depth 6 -Compress),[Text.UTF8Encoding]::new($false))
$stateFiles=@($files|ForEach-Object {@{name=$_.path;existed=$true;bytes=$_.bytes;sha256=$_.sha256}});$stateFiles+=@{name='manifest.json';existed=$true;bytes=(Get-Item -LiteralPath $manifest).Length;sha256=$oldHash}
$snapshot=@{schema=1;manifestSha256=$oldHash;journalId=$oldId;files=$stateFiles}
$statePath=Join-Path $root 'previous-state.json';[IO.File]::WriteAllText($statePath,($snapshot|ConvertTo-Json -Depth 8 -Compress),[Text.UTF8Encoding]::new($false));Protect $statePath
$payload=Join-Path $root 'payload-manifest.json';[IO.File]::WriteAllText($payload,'{}',[Text.UTF8Encoding]::new($false));Protect $payload
$now=[DateTimeOffset]::UtcNow;$plan=@{Schema=1;Run=$id;Mode='Scratch';DeadlineUtc=$now.AddSeconds(150).ToString('o');UserSid=$config.UserSid;AppRoot=$actual;HelperSha256=$expected['Cita.InstallerPhysical.exe'];WorkerSha256=$expected['RestoreWorker.ps1'];TransactionSha256=$expected['InstallationTransaction.ps1'];InvokeSha256=$expected['Invoke-PhysicalRestore.ps1'];PayloadManifestSha256=(Get-FileHash -LiteralPath $payload).Hash;InstallerSha256='';PreviousManifestSha256=$oldHash;PreviousJournalId=$oldId;PreviousStateSha256=(Get-FileHash -LiteralPath $statePath).Hash}
$planPath=Join-Path $root 'plan.json';[IO.File]::WriteAllText($planPath,($plan|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false));Protect $planPath
$principal=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$trigger=New-ScheduledTaskTrigger -AtStartup
$denied=$task+'-DeniedController'
if(Get-ScheduledTask -TaskName $denied -ErrorAction SilentlyContinue){throw 'Foreign negative-test task exists'}
$deniedAction=New-ScheduledTaskAction -Execute (Join-Path $root 'Cita.InstallerPhysical.exe') -Argument ('--controller "'+$root+'"')
Register-ScheduledTask -TaskName $denied -Action $deniedAction -Principal $principal -Settings $settings -Trigger $trigger|Out-Null
$negativeStart=[DateTime]::Now.AddSeconds(-1);Start-ScheduledTask -TaskName $denied;$negativeDone=$false
for($i=0;$i -lt 200;$i++){$d=Get-ScheduledTask -TaskName $denied;$di=Get-ScheduledTaskInfo -TaskName $denied;if($di.LastRunTime -ge $negativeStart -and $d.State -ne 'Running'){$negativeDone=$true;break};Start-Sleep -Milliseconds 100}
if(!$negativeDone -or $di.LastTaskResult -eq 0 -or (Test-Path -LiteralPath "$root\controller.json")){throw 'SYSTEM controller rejection unconfirmed'}
Unregister-ScheduledTask -TaskName $denied -Confirm:$false
$image=Join-Path $root 'Cita.InstallerPhysical.exe';$info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$image;$info.Arguments='--controller "'+$root+'"';$info.UseShellExecute=$false;$info.CreateNoWindow=$true
$controller=[Diagnostics.Process]::Start($info);if(!$controller){throw 'Controller start failed'}
for($i=0;$i -lt 100 -and !(Test-Path -LiteralPath "$root\controller.json");$i++){if($controller.HasExited){throw 'Controller exited before readiness'};Start-Sleep -Milliseconds 100}
$controllerPath=Join-Path $root 'controller.json';Protect $controllerPath
$identity=[IO.File]::ReadAllText($controllerPath)|ConvertFrom-Json
if($identity.run -ne $id -or $identity.pid -ne $controller.Id -or $identity.start -ne $controller.StartTime.ToUniversalTime().Ticks -or $identity.tokenSid -ne $config.UserSid -or $controller.MainModule.FileName -ine $image){throw 'Controller identity differs'}
foreach($name in @($task,$backup)){if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){throw 'Foreign task exists'}}
$primaryAction=New-ScheduledTaskAction -Execute $image -Argument ('--primary "'+$root+'"')
$backupAction=New-ScheduledTaskAction -Execute $image -Argument ('--backup "'+$root+'"')
Register-ScheduledTask -TaskName $task -Action $primaryAction -Principal $principal -Settings $settings -Trigger $trigger|Out-Null
Register-ScheduledTask -TaskName $backup -Action $backupAction -Principal $principal -Settings $settings -Trigger $trigger|Out-Null
Start-ScheduledTask -TaskName $task;Start-ScheduledTask -TaskName $backup
for($i=0;$i -lt 250 -and (!(Test-Path -LiteralPath "$root\ready-primary.json") -or !(Test-Path -LiteralPath "$root\ready-backup.json") -or !(Test-Path -LiteralPath "$root\dirty.json"));$i++){if($controller.HasExited){throw 'Controller exited before scratch fixture'};Start-Sleep -Milliseconds 100}
$held=@();$controllerTermination=''
try{
 foreach($role in @('primary','backup')){
  $ready=[IO.File]::ReadAllText((Join-Path $root ('ready-'+$role+'.json')))|ConvertFrom-Json
  if($ready.run -ne $id -or $ready.role -ne $role -or !$ready.jobHeld -or !$ready.maintenanceHeld -or $ready.tokenSid -ne 'S-1-5-18'){throw 'Watcher readiness unconfirmed'}
  $process=Get-Process -Id $ready.pid -ErrorAction Stop;$held+=,$process
  if($process.HasExited -or $process.StartTime.ToUniversalTime().Ticks -ne $ready.start -or $process.MainModule.FileName -ine $image){throw 'Watcher identity differs'}
 }
 if($held.Count -ne 2 -or $held[0].Id -eq $held[1].Id -or $controller.HasExited -or $controller.StartTime.ToUniversalTime().Ticks -ne $identity.start){throw 'Controller or watchers changed before fault injection'}
 $held[0].Kill();if(!$held[0].WaitForExit(5000)){throw 'Primary termination unconfirmed'}
 if($controller.HasExited){$controllerTermination='WatcherLoss'}else{try{$controller.Kill();$controllerTermination='InjectedKill'}catch{if(!$controller.HasExited){throw};$controllerTermination='WatcherLoss'}}
 if(!$controller.WaitForExit(5000)){throw 'Controller termination unconfirmed'}
}finally{foreach($process in $held){$process.Dispose()}}
[pscustomobject]@{run=$id;controller=$identity.pid;controllerStopped=$true;controllerTermination=$controllerTermination;primaryStopped=$true;systemControllerRefused=$true;baseline=$baseline}|ConvertTo-Json -Depth 5 -Compress
'@
foreach($pair in @(@('__ROOT__',$remote),@('__ID__',$id),@('__TASK__',$task),@('__HELPER__',$hashes['Cita.InstallerPhysical.exe']),@('__WORKER__',$hashes['RestoreWorker.ps1']),@('__TX__',$hashes['InstallationTransaction.ps1']),@('__INVOKER__',$hashes['Invoke-PhysicalRestore.ps1']))){$start=$start.Replace($pair[0],$pair[1])}
$startPath=Join-Path $workspace ("runtime/installer-scratch-start-"+$id+'.ps1')
[IO.File]::WriteAllText($startPath,$start,[Text.UTF8Encoding]::new($true));$startHash=(Get-FileHash -LiteralPath $startPath).Hash
Invoke-BoundedScp $startPath ("citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/$folder/start.ps1")
$started=(Invoke-BoundedRm "if((Get-FileHash -LiteralPath '$remote\start.ps1').Hash -ne '$startHash'){throw 'Start script differs'};& ([ScriptBlock]::Create([IO.File]::ReadAllText('$remote\start.ps1')))" 60)|ConvertFrom-Json
if($started.run -ne $id -or !$started.controllerStopped -or !$started.primaryStopped -or !$started.systemControllerRefused){throw 'Fault injection result unconfirmed.'}
$collect=@'
$ErrorActionPreference='Stop';$root='__ROOT__';$id='__ID__';$task='__TASK__';$backup=$task+'-Backup'
for($i=0;$i -lt 500 -and !(Test-Path -LiteralPath "$root\result-backup.json");$i++){Start-Sleep -Milliseconds 100}
if(!(Test-Path -LiteralPath "$root\result-backup.json")){throw 'Backup recovery result unavailable'}
$result=[IO.File]::ReadAllText("$root\result-backup.json")|ConvertFrom-Json;$retired=[IO.File]::ReadAllText("$root\retired-backup.json")|ConvertFrom-Json
if($result.run -ne $id -or !$result.success -or $result.outcome -ne 'RolledBack' -or $result.jobActiveAfter -ne 0 -or $retired.run -ne $id -or !$retired.controllerStopped -or $retired.jobActiveAfter -ne 0){throw 'Backup job or rollback unconfirmed'}
$client=Join-Path $root 'scratch\client';$state=[IO.File]::ReadAllText("$root\previous-state.json")|ConvertFrom-Json
foreach($entry in $state.files){$path=Join-Path $client $entry.name;if(!$entry.existed -or !(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $path).Hash -ne $entry.sha256){throw 'Scratch baseline differs'}}
$baseline=[IO.File]::ReadAllText("$root\actual-baseline.json")|ConvertFrom-Json;$actual='C:\Users\CITASTATION\AppData\Local\Programs\CITA Route\client';$svc=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'"
if((Get-FileHash -LiteralPath (Join-Path $actual 'RoutePilot.exe')).Hash -ne $baseline.appSha -or (Get-FileHash -LiteralPath (Join-Path $actual 'manifest.json')).Hash -ne $baseline.manifestSha -or (Get-FileHash -LiteralPath 'C:\Program Files\RoutePilot\Agent\native\intent.json').Hash -ne $baseline.intentSha -or !$svc -or $svc.State -ne 'Running' -or $svc.ProcessId -ne $baseline.servicePid){throw 'Actual client or service changed'}
foreach($name in @($task,$backup)){$definition=Get-ScheduledTask -TaskName $name -ErrorAction Stop;$principal=[string]$definition.Principal.UserId;$sid=if($principal -match '^S-1-'){$principal}else{([Security.Principal.NTAccount]::new($principal)).Translate([Security.Principal.SecurityIdentifier]).Value};$role=if($name -eq $task){'--primary'}else{'--backup'};if($sid -ne 'S-1-5-18' -or $definition.Actions.Count -ne 1 -or $definition.Actions[0].Execute -ine "$root\Cita.InstallerPhysical.exe" -or $definition.Actions[0].Arguments -ne ($role+' "'+$root+'"')){throw 'Watcher task identity differs'}}
for($i=0;$i -lt 40 -and (Get-ScheduledTask -TaskName $backup).State -eq 'Running';$i++){Start-Sleep -Milliseconds 100}
if((Get-ScheduledTask -TaskName $backup).State -eq 'Running' -or (Get-ScheduledTaskInfo -TaskName $backup).LastTaskResult -ne 0){throw 'Backup watcher did not finish'}
if(@(Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -like ($root+'\*')}).Count){throw 'Scratch processes remain'}
[pscustomobject]@{run=$id;success=$true;outcome=$result.outcome;jobActiveAfter=$result.jobActiveAfter;scratchPublicFiles=$state.files.Count;actualAppUnchanged=$true;intentUnchanged=$true;servicePidUnchanged=$true;rootRetained=$true;tasksRetained=$true;scope='Scratch public files only; no real installation or network changes'}|ConvertTo-Json -Compress
'@
$collect=$collect.Replace('__ROOT__',$remote).Replace('__ID__',$id).Replace('__TASK__',$task)
$collectPath=Join-Path $workspace ("runtime/installer-scratch-collect-"+$id+'.ps1');[IO.File]::WriteAllText($collectPath,$collect,[Text.UTF8Encoding]::new($true));$collectHash=(Get-FileHash -LiteralPath $collectPath).Hash
Invoke-BoundedScp $collectPath ("citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/$folder/collect.ps1")
$result=(Invoke-BoundedRm "if((Get-FileHash -LiteralPath '$remote\collect.ps1').Hash -ne '$collectHash'){throw 'Collect script differs'};& ([ScriptBlock]::Create([IO.File]::ReadAllText('$remote\collect.ps1')))" 60)|ConvertFrom-Json
if($result.run -ne $id -or !$result.success){throw 'Scratch acceptance unconfirmed; preserve journal.'}
$result|Add-Member -NotePropertyName componentHashes -NotePropertyValue $hashes
$result|Add-Member -NotePropertyName systemControllerRefused -NotePropertyValue $started.systemControllerRefused
$result|Add-Member -NotePropertyName controllerTermination -NotePropertyValue $started.controllerTermination
$result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $workspace 'runtime/installer-guard-preflight-result.json') -Encoding UTF8
$entry.state='VerifiedRetained';$update=$journal+'.'+[guid]::NewGuid().ToString('N')+'.new'
try{$stream=[IO.File]::Open($update,'CreateNew','Write','Read');try{$bytes=[Text.Encoding]::UTF8.GetBytes(($entry|ConvertTo-Json -Depth 5 -Compress));$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()};[IO.File]::Replace($update,$journal,[NullString]::Value)}finally{if(Test-Path -LiteralPath $update){Remove-Item -LiteralPath $update -Force}}
Remove-Item -LiteralPath $startPath,$collectPath -Force
$result
