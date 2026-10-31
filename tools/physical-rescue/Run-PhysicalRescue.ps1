param([switch]$CrashWatcher,[switch]$ArmTest,[switch]$MaintenanceCheck,[string]$SourceReceipt,[string]$HelperPath)
$ErrorActionPreference='Stop'
if($CrashWatcher -and $ArmTest){throw 'Prove watchdog restart separately before the narrow WFP test'}
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
$journal=Join-Path $workspace 'runtime/physical-rescue-active.json'
if(Test-Path -LiteralPath $journal){throw 'Earlier rescue run needs reconciliation before a retry'}
$runtime=[IO.Path]::GetFullPath((Join-Path $workspace 'runtime'))
if(!$SourceReceipt){$SourceReceipt=Join-Path $runtime 'native-corresponding-source.zip.receipt.json'}
if(!$HelperPath){$HelperPath=Join-Path $runtime 'physical-rescue-build/Cita.PhysicalRescue.exe'}
function Resolve-RuntimeFile([string]$path){
 if(![IO.Path]::IsPathRooted($path)){$path=Join-Path $workspace $path}
 $full=[IO.Path]::GetFullPath($path)
 if(!$full.StartsWith($runtime+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'File must be inside runtime'}
 $item=Get-Item -LiteralPath $full -Force -ErrorAction Stop
 if($item.PSIsContainer){throw 'Expected runtime file'}
 while($item -and $item.FullName.Length -ge $runtime.Length){if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked runtime path refused'};if($item.FullName -eq $runtime){break};$item=$item.Parent}
 return $full
}
$image=Resolve-RuntimeFile $HelperPath
$SourceReceipt=Resolve-RuntimeFile $SourceReceipt
$guard=Join-Path $workspace 'runtime/native-guard-lab.exe'
$guard=Resolve-RuntimeFile $guard
$imageHash=(Get-FileHash -LiteralPath $image).Hash;$guardHash=(Get-FileHash -LiteralPath $guard).Hash;$receiptHash=(Get-FileHash -LiteralPath $SourceReceipt).Hash
$source=Get-Content -LiteralPath $SourceReceipt -Raw -Encoding UTF8|ConvertFrom-Json
$sourceZip=Resolve-RuntimeFile $source.path
$sourceHash=(Get-FileHash -LiteralPath $sourceZip).Hash
if(!$source.rebuildVerified -or $source.sha256 -ne $sourceHash -or $source.verifiedBinaries.'native-guard.exe' -ne $guardHash){throw 'Rescue guard or source archive differs from verified receipt'}
function Confirm-DualProof($proof,$imageHash,$guardHash,$sourceHash,$receiptHash){
 if(!$proof.success -or $proof.armTest -ne $false -or !$proof.backupSurvivedPrimary -or !$proof.controllerStopped -or $proof.jobActiveAfter -ne 0 -or $proof.rules -ne 0 -or $proof.folder -ne $false -or $proof.taskRemaining -ne $false -or $proof.helperSha256 -ne $imageHash -or $proof.guardSha256 -ne $guardHash -or $proof.sourceSha256 -ne $sourceHash -or $proof.sourceReceiptSha256 -ne $receiptHash){throw 'Current matching dual-watch no-Arm proof required'}
}
if($ArmTest){
 $proofPath=Resolve-RuntimeFile (Join-Path $runtime 'physical-rescue-dual-current-result.json')
 $proof=Get-Content -LiteralPath $proofPath -Raw -Encoding UTF8|ConvertFrom-Json
 Confirm-DualProof $proof $imageHash $guardHash $sourceHash $receiptHash
 if(!$proof.maintenanceCheck -or !$proof.deleteSharingConfirmed){throw 'Matching physical maintenance sharing proof required'}
}
$serviceHash=''
if($ArmTest -or $MaintenanceCheck){
 $contractPath=Resolve-RuntimeFile (Join-Path $runtime 'physical-service-commit-contract.json')
 $contract=Get-Content -LiteralPath $contractPath -Raw -Encoding UTF8|ConvertFrom-Json
 if(!$contract.saveUsesAtomicOverwrite -or !$contract.cfgCounterexamplesRejected -or $contract.destination -ne 'this.statePath' -or $contract.imageSha256 -cnotmatch '^[0-9A-F]{64}$' -or @($contract.methods).Count -ne 2 -or @($contract.methods|Where-Object {!$_.saveDominatesCommit}).Count){throw 'Service commit contract not confirmed'}
 $serviceHash=$contract.imageSha256
}
$id=[guid]::NewGuid().ToString('N');$folder="cita-rescue-$id";$remote="C:\Program Files\RoutePilot\Agent\$folder";$task="CitaRouteLabRescue-$id"
$entry=@{id=$id;folder=$remote;task=$task;backupTask=($task+'-Backup');imageHash=$imageHash;guardHash=$guardHash;sourceHash=$sourceHash;receiptHash=$receiptHash;state='MayCreate'}|ConvertTo-Json -Compress
$f=[IO.File]::Open($journal,'CreateNew','Write','Read');try{$b=[Text.Encoding]::UTF8.GetBytes($entry);$f.Write($b,0,$b.Length);$f.Flush($true)}finally{$f.Dispose()}
$recipe=Join-Path $workspace 'tools/native-control-probe/Test-PhysicalTransfer.ps1'
if((Get-FileHash -LiteralPath $recipe).Hash -ne '42CB3AA03261C76905617CAE5EBFAFE3F927229E5672B3278B4EFC9A6758BECE'){throw 'Protected transfer recipe differs'}
$create=[regex]::Match([IO.File]::ReadAllText($recipe),"(?s)\`$create=@'\r?\n(.*?)\r?\n'@").Groups[1].Value
if(!$create -or (Invoke-BoundedRm $create.Replace('__DIR__',$remote)).Trim() -ne 'ready'){throw 'Protected rescue directory unconfirmed'}
Invoke-BoundedScp $image "citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/$folder/Cita.PhysicalRescue.exe"
Invoke-BoundedScp $guard "citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/$folder/native-guard.exe"
$inspectFunction=@'
function Inspect-Run([string]$run){
 if($run -cnotmatch '^[0-9a-f]{32}$'){throw 'Inspect run refused'}
 $fixture='C:\Program Files\RoutePilot\Agent\zapret-live-lab\native\native-guard.exe'
 $lock=[IO.File]::Open($fixture,'Open','Read','Read')
 try{
  if((Get-FileHash -InputStream $lock).Hash -ne '__GUARD__'){throw 'Inspect fixture differs'}
  $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$fixture;$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;$info.EnvironmentVariables['CITA_PHYSICAL_GUARD_LAB']='1'
  $p=[Diagnostics.Process]::Start($info);if(!$p){throw 'Inspect process not started'}
  try{
   $out=$p.StandardOutput.ReadToEndAsync();$err=$p.StandardError.ReadToEndAsync()
   $p.StandardInput.WriteLine((@{Action='Inspect';Run=$run}|ConvertTo-Json -Compress));$p.StandardInput.Close()
   if(!$p.WaitForExit(10000)){try{$p.Kill();[void]$p.WaitForExit(5000)}catch{};throw 'Inspect timed out'}
   if(!$out.Wait(3000) -or !$err.Wait(3000) -or $out.Result.Length -gt 8192 -or $err.Result.Length -gt 8192){throw 'Inspect response unconfirmed'}
   $reply=$out.Result|ConvertFrom-Json
   if($p.ExitCode -ne 0 -or !$reply.success -or $reply.run -ne $run -or $null -eq $reply.rules){throw 'Inspect failed'}
   return [pscustomobject]@{run=$run;policyDigest=[string]$reply.policyDigest;endpointDigest=[string]$reply.endpointDigest;rules=[int]$reply.rules}
  }finally{$p.Dispose()}
 }finally{$lock.Dispose()}
}
'@
$inspectFunction=$inspectFunction.Replace('__GUARD__',$guardHash)
$start=@'
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong station'}
$root='__ROOT__';$task='__TASK__';$image="$root\Cita.PhysicalRescue.exe";$guard="$root\native-guard.exe"
if((Get-FileHash -LiteralPath $image).Hash -ne '__IMAGE__' -or (Get-FileHash -LiteralPath $guard).Hash -ne '__GUARD__'){throw 'Transferred rescue identities differ'}
function Protect($path){
 $item=Get-Item -LiteralPath $path -Force;if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Rescue linked path'}
 if($item.PSIsContainer){$acl=New-Object Security.AccessControl.DirectorySecurity;$inherit='ContainerInherit,ObjectInherit'}else{$acl=New-Object Security.AccessControl.FileSecurity;$inherit='None'}
 $admin=New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544');$acl.SetOwner($admin);$acl.SetAccessRuleProtection($true,$false)
 foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl',$inherit,'None','Allow')))}
 Set-Acl -LiteralPath $path -AclObject $acl
}
Protect $root;Protect $image;Protect $guard
__INSPECT_FN__
$maintenancePassed=$false
$deleteSharingConfirmed=$false
if('__MAINTENANCE__' -eq 'True'){
 if((Get-FileHash -LiteralPath 'C:\Program Files\RoutePilot\Agent\RoutePilot.Agent.exe').Hash -ne '__SERVICE_HASH__'){throw 'Installed service differs from inspected commit contract'}
 $checkInfo=New-Object Diagnostics.ProcessStartInfo;$checkInfo.FileName=$image;$checkInfo.Arguments='--maintenance-check "'+$root+'"';$checkInfo.UseShellExecute=$false;$checkInfo.CreateNoWindow=$true;$checkInfo.RedirectStandardOutput=$true;$checkInfo.RedirectStandardError=$true
 $check=[Diagnostics.Process]::Start($checkInfo)
 try{
  $checkOut=$check.StandardOutput.ReadToEndAsync();$checkErr=$check.StandardError.ReadToEndAsync()
  if(!$check.WaitForExit(35000)){try{$check.Kill();[void]$check.WaitForExit(5000)}catch{};throw 'Maintenance observation expired'}
  if(!$checkOut.Wait(3000) -or !$checkErr.Wait(3000) -or $checkOut.Result.Length -gt 8192 -or $checkErr.Result.Length -gt 8192){throw 'Maintenance output unconfirmed'}
  if($check.ExitCode -ne 0){throw ('Maintenance check failed: '+$checkErr.Result)}
  $proof=$checkOut.Result|ConvertFrom-Json
  if(!$proof.success -or $proof.leases -ne 3 -or $proof.sharingChecks -ne 3 -or $proof.deleteSharingChecks -ne 3 -or !$proof.afterFirstAndSecondClose){throw 'Maintenance sharing evidence unconfirmed'}
  $maintenancePassed=$true
  $deleteSharingConfirmed=$true
 }finally{$check.Dispose()}
}
if('__ARM__' -eq 'True'){
 $intent='C:\Program Files\RoutePilot\Agent\native\intent.json'
 $foreign=[ordered]@{present=$false;run=$null;policyDigest=$null;endpointDigest=$null;rules=$null}
 if(Test-Path -LiteralPath $intent){
  $item=Get-Item -LiteralPath $intent -Force;if($item.Attributes -band [IO.FileAttributes]::ReparsePoint -or $item.Length -gt 4096){throw 'Foreign intent refused'}
  $intentData=Get-Content -LiteralPath $intent -Raw -Encoding UTF8|ConvertFrom-Json
  if($intentData.Desired -isnot [bool] -or $intentData.Run -cnotmatch '^[0-9a-f]{32}$'){throw 'Foreign intent invalid'}
  if($intentData.Desired){throw 'Active native session prevents narrow test'}
  $seen=Inspect-Run $intentData.Run
  $foreign=[ordered]@{present=$true;run=$seen.run;policyDigest=$seen.policyDigest;endpointDigest=$seen.endpointDigest;rules=$seen.rules}
 }
 [IO.File]::WriteAllText("$root\foreign.json",($foreign|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false));Protect "$root\foreign.json"
}
$controllerMode=if('__ARM__' -eq 'True'){'--controller-arm'}else{'--controller'}
$info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$image;$info.Arguments=$controllerMode+' "'+$root+'"';$info.UseShellExecute=$false;$info.CreateNoWindow=$true
$controller=[Diagnostics.Process]::Start($info);if(!$controller){throw 'Controller failed'}
for($i=0;$i -lt 40 -and !(Test-Path -LiteralPath "$root\controller.json");$i++){if($controller.HasExited){throw 'Controller exited before identity capture'};Start-Sleep -Milliseconds 100}
$identity=Get-Content -LiteralPath "$root\controller.json" -Raw|ConvertFrom-Json
if($identity.pid -ne $controller.Id -or $identity.startedTicks -ne $controller.StartTime.ToUniversalTime().Ticks){throw 'Controller identity differs'}
Protect "$root\controller.json"
$now=[DateTimeOffset]::UtcNow
$seconds=30
$lease=@{Run='__ID__';ControllerPid=$controller.Id;ControllerStartedTicks=$identity.startedTicks;JobHandle=$identity.jobHandle;ChildPid=$identity.childPid;ChildStartedTicks=$identity.childStartedTicks;CreatedUtc=$now.ToString('o');DeadlineUtc=$now.AddSeconds($seconds).ToString('o');GuardSha256='__GUARD__';EnableArm=('__ARM__' -eq 'True')}|ConvertTo-Json -Compress
[IO.File]::WriteAllText("$root\rescue.json",$lease,[Text.UTF8Encoding]::new($false));Protect "$root\rescue.json"
$principal=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$trigger=New-ScheduledTaskTrigger -AtStartup
$backup=$task+'-Backup'
foreach($name in @($task,$backup)){if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){throw 'Foreign task exists'}}
$primaryAction=New-ScheduledTaskAction -Execute $image -Argument ('--watch-primary "'+$root+'"')
$backupAction=New-ScheduledTaskAction -Execute $image -Argument ('--watch-backup "'+$root+'"')
Register-ScheduledTask -TaskName $task -Action $primaryAction -Principal $principal -Settings $settings -Trigger $trigger|Out-Null
Register-ScheduledTask -TaskName $backup -Action $backupAction -Principal $principal -Settings $settings -Trigger $trigger|Out-Null
Start-ScheduledTask -TaskName $task;Start-ScheduledTask -TaskName $backup
for($i=0;$i -lt 100 -and (!(Test-Path -LiteralPath "$root\ready-primary.json") -or !(Test-Path -LiteralPath "$root\ready-backup.json"));$i++){if($controller.HasExited){throw 'Controller exited before both watchers'};Start-Sleep -Milliseconds 100}
$ready=Get-Content -LiteralPath "$root\ready-primary.json" -Raw|ConvertFrom-Json
$backupReady=Get-Content -LiteralPath "$root\ready-backup.json" -Raw|ConvertFrom-Json
foreach($item in @($ready,$backupReady)){if($item.run -ne '__ID__' -or $item.controller -ne $controller.Id -or !$item.jobDuplicated -or $item.jobActiveProcesses -ne 1 -or $item.pid -eq $controller.Id){throw 'Watcher readiness unconfirmed'}}
if('__ARM__' -eq 'True' -and (!$ready.maintenanceHeld -or !$backupReady.maintenanceHeld)){throw 'Both maintenance leases required'}
if($ready.role -ne 'primary' -or $backupReady.role -ne 'backup' -or $ready.pid -eq $backupReady.pid -or $controller.HasExited){throw 'Two independent watchers unconfirmed'}
foreach($item in @($ready,$backupReady)){$held=Get-Process -Id $item.pid -ErrorAction Stop;try{if($held.HasExited -or $held.StartTime.ToUniversalTime().Ticks -ne $item.startedTicks -or $held.MainModule.FileName -ne $image){throw 'Watcher identity differs'}}finally{$held.Dispose()}}
if('__ARM__' -eq 'True'){
 for($i=0;$i -lt 150 -and !(Test-Path -LiteralPath "$root\network.json");$i++){if($controller.HasExited){throw 'Controller exited before narrow test evidence'};Start-Sleep -Milliseconds 100}
 if(!(Test-Path -LiteralPath "$root\network.json")){throw 'Controller narrow test evidence timed out'}
 $network=[IO.File]::ReadAllText("$root\network.json")|ConvertFrom-Json
 if($network.run -ne '__ID__' -or !$network.baseline -or !$network.blocked -or $network.armedRules -ne 1){throw 'Controller narrow test evidence refused'}
}
if('__CRASH__' -eq 'True'){
 $watcher=Get-Process -Id $ready.pid -ErrorAction Stop
 if($watcher.StartTime.ToUniversalTime().Ticks -ne $ready.startedTicks -or $watcher.MainModule.FileName -ne $image){throw 'Watcher identity differs'}
 $watcher.Kill();if(!$watcher.WaitForExit(5000)){throw 'Injected watcher exit unconfirmed'};$watcher.Dispose()
}
[pscustomobject]@{run='__ID__';watcher=$ready.pid;backupWatcher=$backupReady.pid;controller=$controller.Id;controllerAlive=$true;jobActiveBefore=$ready.jobActiveProcesses;maintenanceCheck=$maintenancePassed;deleteSharingConfirmed=$deleteSharingConfirmed;startedUtc=$now.ToString('o');task=$task}|ConvertTo-Json -Compress
'@
$start=$start.Replace('__INSPECT_FN__',$inspectFunction).Replace('__ROOT__',$remote).Replace('__TASK__',$task).Replace('__IMAGE__',$imageHash).Replace('__GUARD__',$guardHash).Replace('__ID__',$id).Replace('__CRASH__',[string][bool]$CrashWatcher).Replace('__ARM__',[string][bool]$ArmTest).Replace('__MAINTENANCE__',[string][bool]($MaintenanceCheck -or $ArmTest)).Replace('__SERVICE_HASH__',$serviceHash)
$startPath=Join-Path $workspace "runtime/rescue-start-$id.ps1"
[IO.File]::WriteAllText($startPath,$start,[Text.UTF8Encoding]::new($true));$startHash=(Get-FileHash -LiteralPath $startPath).Hash
Invoke-BoundedScp $startPath "citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/$folder/start.ps1"
$ready=(Invoke-BoundedRm "if((Get-FileHash -LiteralPath '$remote\start.ps1').Hash -ne '$startHash'){throw 'Rescue startup script differs'};& ([ScriptBlock]::Create([IO.File]::ReadAllText('$remote\start.ps1')))" 60)|ConvertFrom-Json
Remove-Item -LiteralPath $startPath -Force
if($ready.run -ne $id -or !$ready.controllerAlive){throw 'Independent watcher readiness unconfirmed'}
$collect=@'
$ErrorActionPreference='Stop';$root='__ROOT__';$task='__TASK__'
__INSPECT_FN__
for($i=0;$i -lt 45 -and !(Test-Path -LiteralPath "$root\result.json");$i++){Start-Sleep -Seconds 1}
if(!(Test-Path -LiteralPath "$root\result.json")){[pscustomobject]@{run='__ID__';pending=$true}|ConvertTo-Json -Compress;exit 0}
$result=Get-Content -LiteralPath "$root\result.json" -Raw|ConvertFrom-Json
if($result.run -ne '__ID__' -or !$result.success -or !$result.controllerStopped -or $result.rules -ne 0 -or $result.jobActiveAfter -ne 0){throw 'Rescue result unconfirmed'}
$network=$null
if('__ARM__' -eq 'True'){
 $network=[IO.File]::ReadAllText("$root\network.json")|ConvertFrom-Json
 if($network.run -ne '__ID__' -or !$network.baseline -or !$network.blocked -or $network.armedRules -ne 1){throw 'Narrow block evidence missing'}
 $client=New-Object Net.Sockets.TcpClient
 try{$pending=$client.ConnectAsync('1.1.1.1',443);if(!$pending.Wait(3000) -or !$client.Connected){throw 'Control access not restored'}}finally{$client.Dispose()}
 $own=Inspect-Run '__ID__';if($own.rules -ne 0){throw 'Independent own-run inspect found rules'}
 $foreign=Get-Content -LiteralPath "$root\foreign.json" -Raw -Encoding UTF8|ConvertFrom-Json
 $intent='C:\Program Files\RoutePilot\Agent\native\intent.json'
 if($foreign.present){
  if(!(Test-Path -LiteralPath $intent)){throw 'Foreign intent disappeared'}
  $intentData=Get-Content -LiteralPath $intent -Raw -Encoding UTF8|ConvertFrom-Json
  if($intentData.Desired -isnot [bool] -or $intentData.Desired -or $intentData.Run -ne $foreign.run){throw 'Foreign intent changed'}
  $after=Inspect-Run $foreign.run
  if($after.rules -ne $foreign.rules -or $after.policyDigest -ne $foreign.policyDigest -or $after.endpointDigest -ne $foreign.endpointDigest){throw 'Foreign guard state changed'}
 }elseif(Test-Path -LiteralPath $intent){throw 'Foreign intent appeared during test'}
}
$finalReady=Get-Content -LiteralPath "$root\ready-backup.json" -Raw|ConvertFrom-Json
if('__CRASH__' -eq 'True' -and $finalReady.pid -eq __WATCHER__){throw 'Backup watcher identity differs'}
$definition=Get-ScheduledTask -TaskName $task
$principal=[string]$definition.Principal.UserId
$principalSid=if($principal -match '^S-1-'){$principal}else{([Security.Principal.NTAccount]::new($principal)).Translate([Security.Principal.SecurityIdentifier]).Value}
if($definition.Actions.Count -ne 1 -or $definition.Actions[0].Execute -ne "$root\Cita.PhysicalRescue.exe" -or $definition.Actions[0].Arguments -ne ('--watch-primary "'+$root+'"') -or $principalSid -ne 'S-1-5-18'){throw 'Rescue task definition differs'}
for($i=0;$i -lt 30 -and (Get-ScheduledTask -TaskName $task).State -eq 'Running';$i++){Start-Sleep -Milliseconds 100}
$backup=$task+'-Backup';$backupDefinition=Get-ScheduledTask -TaskName $backup
if($backupDefinition.Actions.Count -ne 1 -or $backupDefinition.Actions[0].Execute -ne "$root\Cita.PhysicalRescue.exe" -or $backupDefinition.Actions[0].Arguments -ne ('--watch-backup "'+$root+'"')){throw 'Backup task identity differs'}
$backupPrincipal=[string]$backupDefinition.Principal.UserId
$backupSid=if($backupPrincipal -match '^S-1-'){$backupPrincipal}else{([Security.Principal.NTAccount]::new($backupPrincipal)).Translate([Security.Principal.SecurityIdentifier]).Value}
$backupXml=[xml](Export-ScheduledTask -TaskName $backup)
if($backupSid -ne 'S-1-5-18' -or $null -eq $backupXml.Task.Triggers.BootTrigger){throw 'Backup principal or independent triggers differ'}
for($i=0;$i -lt 30 -and $backupDefinition.State -eq 'Running';$i++){Start-Sleep -Milliseconds 100;$backupDefinition=Get-ScheduledTask -TaskName $backup}
if((Get-ScheduledTask -TaskName $task).State -eq 'Running' -or $backupDefinition.State -eq 'Running'){throw 'Watcher task exit unconfirmed'}
if('__CRASH__' -eq 'True'){if((Get-ScheduledTaskInfo -TaskName $backup).LastTaskResult -ne 0){throw 'Independent backup task did not finish successfully'}}
elseif((Get-ScheduledTaskInfo -TaskName $task).LastTaskResult -ne 0){throw 'Primary task exit unconfirmed'}
if(@(Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -like ($root+'\*')}).Count){throw 'Rescue processes remain'}
Unregister-ScheduledTask -TaskName $task -Confirm:$false
Unregister-ScheduledTask -TaskName $backup -Confirm:$false
$allowed=@('Cita.PhysicalRescue.exe','native-guard.exe','controller.json','rescue.json','ready-primary.json','ready-backup.json','result.json','start.ps1','collect.ps1','cleanup.lock')
if('__ARM__' -eq 'True'){$allowed+=@('network.json','foreign.json')}
$files=@(Get-ChildItem -LiteralPath $root -Force)
if($files.Count -ne $allowed.Count -or @($files|Where-Object {$_.PSIsContainer -or $_.Name -notin $allowed -or $_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Unexpected rescue content'}
foreach($file in $files){Remove-Item -LiteralPath $file.FullName -Force};Remove-Item -LiteralPath $root -ErrorAction Stop
$scope=if('__ARM__' -eq 'True'){'WFP 1.1.1.1/32, no TUN/routes'}else{'Concurrent backup after primary failure; Cleanup of unused own Run only, no WFP Arm or route change'}
[pscustomobject]@{run='__ID__';success=$true;controllerStopped=$true;jobActiveAfter=$result.jobActiveAfter;backupSurvivedPrimary=('__CRASH__' -eq 'True');strategy='ConcurrentScheduledWatchers';armTest=('__ARM__' -eq 'True');rules=0;foreignPresent=if('__ARM__' -eq 'True'){[bool]$foreign.present}else{$null};ownInspectZero=if('__ARM__' -eq 'True'){$true}else{$null};tcpRestored=if('__ARM__' -eq 'True'){$true}else{$null};folder=(Test-Path -LiteralPath $root);taskRemaining=([bool](Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) -or [bool](Get-ScheduledTask -TaskName $backup -ErrorAction SilentlyContinue));scope=$scope}|ConvertTo-Json -Compress
'@
$collect=$collect.Replace('__INSPECT_FN__',$inspectFunction).Replace('__ROOT__',$remote).Replace('__TASK__',$task).Replace('__ID__',$id).Replace('__CRASH__',[string][bool]$CrashWatcher).Replace('__WATCHER__',[string]$ready.watcher).Replace('__ARM__',[string][bool]$ArmTest)
$collectPath=Join-Path $workspace "runtime/rescue-collect-$id.ps1"
[IO.File]::WriteAllText($collectPath,$collect,[Text.UTF8Encoding]::new($true));$collectHash=(Get-FileHash -LiteralPath $collectPath).Hash
Invoke-BoundedScp $collectPath "citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/$folder/collect.ps1"
$readResult="if((Get-FileHash -LiteralPath '$remote\collect.ps1').Hash -ne '$collectHash'){throw 'Rescue collection script differs'};& ([ScriptBlock]::Create([IO.File]::ReadAllText('$remote\collect.ps1')))"
$result=(Invoke-BoundedRm $readResult 60)|ConvertFrom-Json
if($result.pending){$result=(Invoke-BoundedRm $readResult 60)|ConvertFrom-Json}
if($result.run -ne $id -or !$result.success -or $result.folder -or $result.taskRemaining){throw 'Rescue cleanup unconfirmed'}
$result|Add-Member -NotePropertyName helperSha256 -NotePropertyValue $imageHash
$result|Add-Member -NotePropertyName guardSha256 -NotePropertyValue $guardHash
$result|Add-Member -NotePropertyName sourceSha256 -NotePropertyValue $sourceHash
$result|Add-Member -NotePropertyName sourceReceiptSha256 -NotePropertyValue $receiptHash
$result|Add-Member -NotePropertyName startedUtc -NotePropertyValue $ready.startedUtc
$result|Add-Member -NotePropertyName maintenanceCheck -NotePropertyValue ([bool]$ready.maintenanceCheck)
$result|Add-Member -NotePropertyName deleteSharingConfirmed -NotePropertyValue ([bool]$ready.deleteSharingConfirmed)
$result|Add-Member -NotePropertyName serviceImageSha256 -NotePropertyValue $serviceHash
$result|Add-Member -NotePropertyName cleanupVerifiedUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('o'))
$result|Add-Member -NotePropertyName cleanupScope -NotePropertyValue $result.scope
$resultPath=if($ArmTest){'runtime/physical-rescue-arm-result.json'}else{'runtime/physical-rescue-result.json'}
$result|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $workspace $resultPath) -Encoding UTF8
Remove-Item -LiteralPath $journal -Force
Remove-Item -LiteralPath $collectPath -Force
$result
