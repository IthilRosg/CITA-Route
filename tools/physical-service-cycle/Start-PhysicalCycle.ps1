$ErrorActionPreference='Stop';$root='__ROOT__';$id='__ID__';$task='__TASK__';$backup=$task+'-Backup';$agent='C:\Program Files\RoutePilot\Agent';$image=Join-Path $root 'Cita.PhysicalServiceCycle.exe'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong station'}
if((Get-FileHash -LiteralPath $image).Hash -cne '__HELPER__'){throw 'Helper differs'}
function Protect($path){
 $item=Get-Item -LiteralPath $path -Force;if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Protected link refused'}
 if($item.PSIsContainer){$acl=New-Object Security.AccessControl.DirectorySecurity;$inherit='ContainerInherit,ObjectInherit'}else{$acl=New-Object Security.AccessControl.FileSecurity;$inherit='None'}
 $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')));$acl.SetAccessRuleProtection($true,$false)
 foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl',$inherit,'None','Allow')))}
 Set-Acl -LiteralPath $path -AclObject $acl
}
Protect $root;Protect $image
$config=[IO.File]::ReadAllText((Join-Path $agent 'agent-config.json'))|ConvertFrom-Json
$actual='C:\Users\CITASTATION\AppData\Local\Programs\CITA Route\client';$sid=[string]$config.UserSid
if($sid -cnotmatch '^S-1-5-21-(\d+-){3}\d+$' -or [IO.Path]::GetFullPath($config.AppRoot).TrimEnd('\') -ine $actual){throw 'Owner differs'}
$svc=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'";$intentPath=Join-Path $agent 'native\intent.json';$intent=[IO.File]::ReadAllText($intentPath)|ConvertFrom-Json
if(!$svc -or $svc.State -ne 'Running' -or !$svc.ProcessId -or $intent.Desired -isnot [bool] -or $intent.Desired){throw 'Service or intent not quiescent'}
$baseline=@{servicePid=[int]$svc.ProcessId;intentSha=(Get-FileHash -LiteralPath $intentPath).Hash;agentSha=(Get-FileHash -LiteralPath (Join-Path $agent 'RoutePilot.Agent.exe')).Hash;manifestSha=(Get-FileHash -LiteralPath (Join-Path $actual 'manifest.json')).Hash;operation=$intent.Operation;generation=$intent.Generation}
if($baseline.agentSha -cne '69AADD1AD109B67FC43360E655CE01BF21D34635DFD793A281CAF03F797DE217' -or $baseline.manifestSha -cne 'BC8E46DD1D8FE4E93F2F8E8B4273AF102F76D22D632C902786DA15F24703606A'){throw 'Initial version differs'}
$baselineFile=Join-Path $root 'baseline.json';[IO.File]::WriteAllText($baselineFile,($baseline|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false));Protect $baselineFile
$plan=@{Schema=1;Run=$id;UserSid=$sid;AgentSha256=$baseline.agentSha;HelperSha256='__HELPER__';DeadlineUtc=[DateTimeOffset]::UtcNow.AddSeconds(115).ToString('o')};$planPath=Join-Path $root 'plan.json';[IO.File]::WriteAllText($planPath,($plan|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false));Protect $planPath
foreach($name in @($task,$backup)){$existing=@(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue|Where-Object TaskName -ceq $name);if($existing.Count){throw 'Task name already exists'}}
$info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$image;$info.Arguments='--controller "'+$root+'"';$info.UseShellExecute=$false;$info.CreateNoWindow=$true
$controller=[Diagnostics.Process]::Start($info);if(!$controller){throw 'Controller start failed'}
for($i=0;$i -lt 100 -and !(Test-Path -LiteralPath "$root\controller.json");$i++){if($controller.HasExited){throw 'Controller exited before readiness'};Start-Sleep -Milliseconds 100}
$descriptor=Join-Path $root 'controller.json';Protect $descriptor;$identity=[IO.File]::ReadAllText($descriptor)|ConvertFrom-Json
if($identity.run -cne $id -or $identity.pid -ne $controller.Id -or $identity.start -ne $controller.StartTime.ToUniversalTime().Ticks -or $identity.tokenSid -cne 'S-1-5-18' -or $identity.servicePid -ne $baseline.servicePid -or $controller.MainModule.FileName -ine $image){throw 'Controller identity differs'}
$principal=New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount -RunLevel Highest;$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries;$trigger=New-ScheduledTaskTrigger -AtStartup
foreach($role in @('primary','backup')){$name=if($role -eq 'primary'){$task}else{$backup};$action=New-ScheduledTaskAction -Execute $image -Argument ('--'+$role+' "'+$root+'"');Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings -Trigger $trigger|Out-Null}
Start-ScheduledTask -TaskName $task;Start-ScheduledTask -TaskName $backup
for($i=0;$i -lt 550 -and !(Test-Path -LiteralPath "$root\stopped.json");$i++){if($controller.HasExited){throw 'Controller exited before stop evidence'};Start-Sleep -Milliseconds 100}
if(!(Test-Path -LiteralPath "$root\stopped.json")){throw 'Stop evidence unavailable'}
$stopped=[IO.File]::ReadAllText("$root\stopped.json")|ConvertFrom-Json;if($stopped.run -cne $id -or !$stopped.oldStopped -or !$stopped.nativeStartRefusedWhileIntentWriteDenied -or $stopped.oldPid -ne $baseline.servicePid){throw 'Stopped identity differs'}
$held=@();try{
 foreach($role in @('primary','backup')){$ready=[IO.File]::ReadAllText((Join-Path $root ('ready-'+$role+'.json')))|ConvertFrom-Json;if($ready.run -cne $id -or $ready.role -cne $role -or !$ready.jobHeld -or !$ready.maintenanceHeld -or $ready.tokenSid -cne 'S-1-5-18'){throw 'Watcher readiness differs'};$p=Get-Process -Id $ready.pid -ErrorAction Stop;$held+=,$p;if($p.HasExited -or $p.StartTime.ToUniversalTime().Ticks -ne $ready.start -or $p.MainModule.FileName -ine $image){throw 'Watcher process differs'}}
 if($held.Count -ne 2 -or $held[0].Id -eq $held[1].Id -or $controller.HasExited -or $controller.StartTime.ToUniversalTime().Ticks -ne $identity.start){throw 'Fault injection identities differ'}
 $held[0].Kill();if(!$held[0].WaitForExit(5000)){throw 'Primary exit unconfirmed'}
 $why='InjectedKill';if($controller.HasExited){$why='WatcherLoss'}else{try{$controller.Kill()}catch{if(!$controller.HasExited){throw};$why='WatcherLoss'}};if(!$controller.WaitForExit(5000)){throw 'Controller exit unconfirmed'}
}finally{foreach($p in $held){$p.Dispose()}}
[pscustomobject]@{run=$id;primaryStopped=$true;controllerStopped=$true;controllerTermination=$why;baseline=$baseline}|ConvertTo-Json -Depth 5 -Compress
