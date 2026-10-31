[CmdletBinding()]
param([switch]$ResumePrepared,[string]$CandidateDirectory='runtime/physical-acceptance/candidate-1516d495-owned-transaction',[string]$ExpectedInstallerSha='F4E65F0C743054F91CC6BBFB042EE3D0137A4100CB53E3814D1E121F46A5EE37')
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
$journal=Join-Path $workspace 'runtime/installer-guard-install-active.json'
if(!$ResumePrepared -and (Test-Path -LiteralPath $journal)){throw 'Earlier installation attempt requires reconciliation; no retry was started.'}
if($ResumePrepared -and !(Test-Path -LiteralPath $journal)){throw 'Prepared continuation requires the existing local journal.'}
$proofPath=Join-Path $workspace 'runtime/installer-guard-preflight-result.json'
if(!(Test-Path -LiteralPath $proofPath)){throw 'Successful SYSTEM scratch preflight is required.'}
$proof=Get-Content -LiteralPath $proofPath -Raw -Encoding UTF8|ConvertFrom-Json
if(!$proof.success -or !$proof.systemControllerRefused -or $proof.outcome -ne 'RolledBack' -or $proof.jobActiveAfter -ne 0 -or !$proof.actualAppUnchanged -or !$proof.intentUnchanged -or !$proof.servicePidUnchanged){throw 'Scratch preflight is not confirmed.'}
$candidate=[IO.Path]::GetFullPath((Join-Path $workspace $CandidateDirectory)).TrimEnd('\')
$candidateBase=[IO.Path]::GetFullPath((Join-Path $workspace 'runtime/physical-acceptance')).TrimEnd('\')
if(!$candidate.StartsWith($candidateBase+'\',[StringComparison]::OrdinalIgnoreCase) -or $ExpectedInstallerSha -cnotmatch '^[A-F0-9]{64}$'){throw 'Physical candidate must be explicitly pinned inside the acceptance directory.'}
for($part=$candidate;$part;$part=[IO.Path]::GetDirectoryName($part)){if((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Candidate path linked.'}}
$name='CITA-Route-0.6.9-win-x64-PRIVATE-PREVIEW-physical-acceptance-setup'
$receipt=Get-Content -LiteralPath (Join-Path $candidate ($name+'-receipt.json')) -Raw -Encoding UTF8|ConvertFrom-Json
$sources=[ordered]@{
 'Cita.InstallerPhysical.exe'=(Join-Path $workspace 'runtime/physical-installer-physical-build/Cita.InstallerPhysical.exe')
 'RestoreWorker.ps1'=(Join-Path $PSScriptRoot 'RestoreWorker.ps1')
 'InstallationTransaction.ps1'=(Join-Path $workspace 'installer/InstallationTransaction.ps1')
 'Invoke-PhysicalRestore.ps1'=(Join-Path $PSScriptRoot 'physical/Invoke-PhysicalRestore.ps1')
 'setup.exe'=(Join-Path $candidate ($name+'.exe'))
 'payload-manifest.json'=(Join-Path $candidate ($name+'-payload-manifest.json'))
}
$hashes=[ordered]@{};foreach($key in $sources.Keys){$hashes[$key]=(Get-FileHash -LiteralPath $sources[$key]).Hash}
if($hashes['setup.exe'] -cne $ExpectedInstallerSha -or $receipt.installerSha256 -ne $hashes['setup.exe'] -or $receipt.payloadManifestSha256 -ne $hashes['payload-manifest.json']){throw 'Pinned candidate or sidecar differs.'}
foreach($key in @('Cita.InstallerPhysical.exe','RestoreWorker.ps1','InstallationTransaction.ps1','Invoke-PhysicalRestore.ps1')){if($proof.componentHashes.$key -ne $hashes[$key]){throw 'Scratch preflight component differs from installation attempt.'}}
$recipe=Join-Path $workspace 'tools/native-control-probe/Test-PhysicalTransfer.ps1'
if((Get-FileHash -LiteralPath $recipe).Hash -ne '42CB3AA03261C76905617CAE5EBFAFE3F927229E5672B3278B4EFC9A6758BECE'){throw 'Protected directory recipe differs.'}
$create=[regex]::Match([IO.File]::ReadAllText($recipe),"(?s)\`$create=@'\r?\n(.*?)\r?\n'@").Groups[1].Value;if(!$create){throw 'Protected directory recipe missing.'}
if($ResumePrepared){
 $entry=Get-Content -LiteralPath $journal -Raw -Encoding UTF8|ConvertFrom-Json
 if($entry.schema -ne 1 -or $entry.state -ne 'MayCreate' -or $entry.run -cne '23212dbfafc443e18b097d9ee162a69a'){throw 'Prepared continuation journal refused.'}
 $id=[string]$entry.run
}else{$id=[guid]::NewGuid().ToString('N')}
$folder='cita-installer-'+$id;$remote='C:\Program Files\RoutePilot\Agent\'+$folder;$task='CitaInstallerRepair-'+$id
$taskNames=@($task,($task+'-Backup'));if($taskNames.Count -ne 2 -or $taskNames[0] -ne $task -or $taskNames[1] -ne ($task+'-Backup')){throw 'Watcher task name array differs.'}
if($ResumePrepared){
 if($entry.root -ine $remote -or $entry.primaryTask -ne $task -or $entry.backupTask -ne ($task+'-Backup')){throw 'Prepared journal target differs.'}
 foreach($key in $sources.Keys){if($entry.hashes.$key -ne $hashes[$key]){throw 'Prepared source digest differs.'}}
 $oldStartPath=Join-Path $workspace ("runtime/installer-install-start-"+$id+'.ps1')
 if(!(Test-Path -LiteralPath $oldStartPath)){throw 'Original local start script required for continuation.'}
 $oldStartHash=(Get-FileHash -LiteralPath $oldStartPath).Hash
 $resumeReceipt=Join-Path $workspace ("runtime/installer-guard-resume-"+$id+'.json')
 if(Test-Path -LiteralPath $resumeReceipt){throw 'This prepared continuation was already attempted; reconcile first.'}
}else{
 $entry=[ordered]@{schema=1;run=$id;root=$remote;primaryTask=$task;backupTask=($task+'-Backup');hashes=$hashes;state='MayCreate';utc=[DateTimeOffset]::UtcNow.ToString('o')}
 $f=[IO.File]::Open($journal,'CreateNew','Write','Read');try{$bytes=[Text.Encoding]::UTF8.GetBytes(($entry|ConvertTo-Json -Depth 5 -Compress));$f.Write($bytes,0,$bytes.Length);$f.Flush($true)}finally{$f.Dispose()}
 if((Invoke-BoundedRm $create.Replace('__DIR__',$remote)).Trim() -ne 'ready'){throw 'Exclusive remote root unconfirmed.'}
 foreach($key in $sources.Keys){Invoke-BoundedScp $sources[$key] ("citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/$folder/$key") 180}
}
if($ResumePrepared){
 $precheck=@'
$ErrorActionPreference='Stop';$root='__ROOT__';$id='__ID__';$task='__TASK__';$parent='C:\Program Files\RoutePilot\Agent'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100' -or [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($root)) -ine $parent -or (Split-Path $root -Leaf) -cne ('cita-installer-'+$id)){throw 'Prepared root identity refused'}
$item=Get-Item -LiteralPath $root -Force;if(!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Prepared root refused'}
$acl=Get-Acl -LiteralPath $root;$trusted=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted){throw 'Prepared owner refused'}
foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){if($rule.AccessControlType -eq 'Allow' -and -not ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and $rule.IdentityReference.Value -notin $trusted -and (([int64]$rule.FileSystemRights -band 0xffffffff) -band 0x500d0156)){throw 'Prepared root writable by untrusted identity'}}
$expected=@{'Cita.InstallerPhysical.exe'='__HELPER__';'RestoreWorker.ps1'='__WORKER__';'InstallationTransaction.ps1'='__TX__';'Invoke-PhysicalRestore.ps1'='__INVOKER__';'setup.exe'='__SETUP__';'payload-manifest.json'='__PAYLOAD__';'start.ps1'='__OLDSTART__'}
$items=@(Get-ChildItem -LiteralPath $root -Force);if($items.Count -ne $expected.Count){throw 'Prepared directory contents changed'}
foreach($file in $items){if($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or !$expected.ContainsKey($file.Name) -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne $expected[$file.Name]){throw 'Prepared file identity differs'}}
foreach($name in @($task,($task+'-Backup'))){if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){throw 'Prepared task exists'}}
if(@(Get-CimInstance Win32_Process|Where-Object {$_.ProcessId -ne $PID -and (($_.ExecutablePath -and $_.ExecutablePath.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) -or ($_.CommandLine -and $_.CommandLine.IndexOf($root,[StringComparison]::OrdinalIgnoreCase) -ge 0))}).Count){throw 'Prepared actor exists'}
$journal='C:\Users\CITASTATION\AppData\Local\Programs\CITA Route\client\runtime\installation-transaction.json'
if(Test-Path -LiteralPath $journal){$v=[IO.File]::ReadAllText($journal)|ConvertFrom-Json;if($v.id -ceq $id){throw 'Prepared transaction already exists'}}
[pscustomobject]@{run=$id;files=7;preStart=$true;startSha='__OLDSTART__'}|ConvertTo-Json -Compress
'@
 foreach($pair in @(@('__ROOT__',$remote),@('__ID__',$id),@('__TASK__',$task),@('__HELPER__',$hashes['Cita.InstallerPhysical.exe']),@('__WORKER__',$hashes['RestoreWorker.ps1']),@('__TX__',$hashes['InstallationTransaction.ps1']),@('__INVOKER__',$hashes['Invoke-PhysicalRestore.ps1']),@('__SETUP__',$hashes['setup.exe']),@('__PAYLOAD__',$hashes['payload-manifest.json']),@('__OLDSTART__',$oldStartHash))){$precheck=$precheck.Replace($pair[0],$pair[1])}
 $prepared=(Invoke-BoundedRm $precheck)|ConvertFrom-Json
 if($prepared.run -ne $id -or !$prepared.preStart -or $prepared.files -ne 7 -or $prepared.startSha -ne $oldStartHash){throw 'Prepared remote state unconfirmed.'}
}
$start=@'
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong station'}
$root='__ROOT__';$id='__ID__';$task='__TASK__';$backup=$task+'-Backup';$agent='C:\Program Files\RoutePilot\Agent';$actual='C:\Users\CITASTATION\AppData\Local\Programs\CITA Route\client'
$expected=@{'Cita.InstallerPhysical.exe'='__HELPER__';'RestoreWorker.ps1'='__WORKER__';'InstallationTransaction.ps1'='__TX__';'Invoke-PhysicalRestore.ps1'='__INVOKER__';'setup.exe'='__SETUP__';'payload-manifest.json'='__PAYLOAD__'}
foreach($name in $expected.Keys){$file=Get-Item -LiteralPath (Join-Path $root $name) -Force;if($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne $expected[$name]){throw 'Transferred component differs'}}
if('__RESUME__' -eq 'True'){
 $items=@(Get-ChildItem -LiteralPath $root -Force);if($items.Count -ne 7 -or @($items|Where-Object {$_.PSIsContainer -or $_.Name -notin @('Cita.InstallerPhysical.exe','RestoreWorker.ps1','InstallationTransaction.ps1','Invoke-PhysicalRestore.ps1','setup.exe','payload-manifest.json','start.ps1') -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint)}).Count){throw 'Prepared contents changed before startup'}
 foreach($name in @($task,($task+'-Backup'))){if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){throw 'Prepared task appeared'}}
 if(@(Get-CimInstance Win32_Process|Where-Object {$_.ProcessId -ne $PID -and (($_.ExecutablePath -and $_.ExecutablePath.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) -or ($_.CommandLine -and $_.CommandLine.IndexOf($root,[StringComparison]::OrdinalIgnoreCase) -ge 0))}).Count){throw 'Prepared actor appeared'}
 $previous='C:\Users\CITASTATION\AppData\Local\Programs\CITA Route\client\runtime\installation-transaction.json';if(Test-Path -LiteralPath $previous){$v=[IO.File]::ReadAllText($previous)|ConvertFrom-Json;if($v.id -ceq $id){throw 'Prepared transaction appeared'}}
}
function Protect($path){$item=Get-Item -LiteralPath $path -Force;if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Protected link refused'};if($item.PSIsContainer){$acl=New-Object Security.AccessControl.DirectorySecurity;$inherit='ContainerInherit,ObjectInherit'}else{$acl=New-Object Security.AccessControl.FileSecurity;$inherit='None'};$admin=New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544');$acl.SetOwner($admin);$acl.SetAccessRuleProtection($true,$false);foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl',$inherit,'None','Allow')))};Set-Acl -LiteralPath $path -AclObject $acl}
Protect $root;foreach($name in $expected.Keys){Protect (Join-Path $root $name)};Protect (Join-Path $root 'start.ps1')
$config=[IO.File]::ReadAllText((Join-Path $agent 'agent-config.json'))|ConvertFrom-Json
if($config.UserSid -cnotmatch '^S-1-5-21-(\d+-){3}\d+$' -or [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $config.UserSid -or [IO.Path]::GetFullPath($config.AppRoot).TrimEnd('\') -ine $actual){throw 'Protected client ownership differs'}
$transaction=Join-Path $root 'InstallationTransaction.ps1';. ([ScriptBlock]::Create([IO.File]::ReadAllText($transaction)) )
$manifest=Join-Path $actual 'manifest.json';$oldHash=(Get-FileHash -LiteralPath $manifest).Hash;$old=Read-InstallJournal $actual $config.UserSid
if(!$old -or $old.phase -ne 'Committed' -or $old.incomingManifestHash -ne $oldHash -or $old.id -ceq $id){throw 'Previous committed baseline unavailable'}
$oldManifest=[IO.File]::ReadAllText($manifest)|ConvertFrom-Json
if(@($oldManifest.files).Count -lt 20 -or @($oldManifest.files).Count -gt 4096){throw 'Previous public inventory refused'}
$names=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase);$null=$names.Add('manifest.json')
foreach($item in $oldManifest.files){if($item.path -in @('force-direct.txt','force-vpn.txt')){continue};$null=Get-InstallPublicPath $actual $item.path;$null=$names.Add($item.path.Replace('/','\'))}
$files=@();foreach($name in $names){$path=Get-InstallPublicPath $actual $name;$exists=Test-Path -LiteralPath $path;if($exists){$file=Get-Item -LiteralPath $path -Force;if($file.PSIsContainer){throw 'Public file is a directory'};$files+=@{name=$name;existed=$true;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $path).Hash}}else{$files+=@{name=$name;existed=$false;bytes=0;sha256=''}}}
$snapshot=@{schema=1;manifestSha256=$oldHash;journalId=$old.id;files=$files};$statePath=Join-Path $root 'previous-state.json';[IO.File]::WriteAllText($statePath,($snapshot|ConvertTo-Json -Depth 8 -Compress),[Text.UTF8Encoding]::new($false));Protect $statePath
function NativeDigest {
 $native=Join-Path $agent 'native';$rootLength=$native.Length+1;$rows=New-Object 'System.Collections.Generic.List[string]'
 $items=@(Get-ChildItem -LiteralPath $native -Recurse -Force);if(@($items|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Native module link refused'}
 foreach($file in @($items|Where-Object {!$_.PSIsContainer}|Sort-Object FullName)){
  $relative=$file.FullName.Substring($rootLength);if($relative -ieq 'intent.json'){continue}
  $rows.Add($relative+'|'+$file.Length+'|'+(Get-FileHash -LiteralPath $file.FullName).Hash)
 }
 $sha=[Security.Cryptography.SHA256]::Create();try{return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($rows -join "`n"))))).Replace('-','')}finally{$sha.Dispose()}
}
$intent=Join-Path $agent 'native\intent.json';$svc=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'"
if(!$svc -or $svc.State -ne 'Running' -or !$svc.ProcessId){throw 'Protected service unavailable'}
$baseline=@{intentSha=(Get-FileHash -LiteralPath $intent).Hash;servicePid=[int]$svc.ProcessId;agentSha=(Get-FileHash -LiteralPath (Join-Path $agent 'RoutePilot.Agent.exe')).Hash;nativeDigest=(NativeDigest);manifestSha=$oldHash}
[IO.File]::WriteAllText((Join-Path $root 'actual-baseline.json'),($baseline|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false));Protect (Join-Path $root 'actual-baseline.json')
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class CitaJobEvidence {
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool DuplicateHandle(IntPtr source,IntPtr handle,IntPtr target,out IntPtr copy,uint access,bool inherit,uint options);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool QueryInformationJobObject(IntPtr job,int kind,IntPtr data,uint bytes,out uint returned);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool member);
 [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
}
"@
$now=[DateTimeOffset]::UtcNow;$plan=@{Schema=1;Run=$id;Mode='Physical';DeadlineUtc=$now.AddSeconds(240).ToString('o');UserSid=$config.UserSid;AppRoot=$actual;HelperSha256=$expected['Cita.InstallerPhysical.exe'];WorkerSha256=$expected['RestoreWorker.ps1'];TransactionSha256=$expected['InstallationTransaction.ps1'];InvokeSha256=$expected['Invoke-PhysicalRestore.ps1'];PayloadManifestSha256=$expected['payload-manifest.json'];InstallerSha256=$expected['setup.exe'];PreviousManifestSha256=$oldHash;PreviousJournalId=$old.id;PreviousStateSha256=(Get-FileHash -LiteralPath $statePath).Hash}
$planPath=Join-Path $root 'plan.json';[IO.File]::WriteAllText($planPath,($plan|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false));Protect $planPath
$image=Join-Path $root 'Cita.InstallerPhysical.exe';$info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$image;$info.Arguments='--controller "'+$root+'"';$info.UseShellExecute=$false;$info.CreateNoWindow=$true
$controller=[Diagnostics.Process]::Start($info);if(!$controller){throw 'Controller start failed'}
for($i=0;$i -lt 100 -and !(Test-Path -LiteralPath "$root\controller.json");$i++){if($controller.HasExited){throw 'Controller exited before readiness'};Start-Sleep -Milliseconds 100}
$controllerPath=Join-Path $root 'controller.json';Protect $controllerPath;$identity=[IO.File]::ReadAllText($controllerPath)|ConvertFrom-Json
if($identity.run -ne $id -or $identity.pid -ne $controller.Id -or $identity.start -ne $controller.StartTime.ToUniversalTime().Ticks -or $identity.tokenSid -ne $config.UserSid -or $controller.MainModule.FileName -ine $image){throw 'Controller identity differs'}
$job=[IntPtr]::Zero;if(![CitaJobEvidence]::DuplicateHandle($controller.Handle,[IntPtr]([long]$identity.handle),[IntPtr](-1),[ref]$job,0,$false,2)){throw 'Job evidence handle unavailable'}
try{
 $principal=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest;$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries;$trigger=New-ScheduledTaskTrigger -AtStartup
 foreach($name in @($task,$backup)){if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){throw 'Foreign task exists'}}
 Register-ScheduledTask -TaskName $task -Action (New-ScheduledTaskAction -Execute $image -Argument ('--primary "'+$root+'"')) -Principal $principal -Settings $settings -Trigger $trigger|Out-Null
 Register-ScheduledTask -TaskName $backup -Action (New-ScheduledTaskAction -Execute $image -Argument ('--backup "'+$root+'"')) -Principal $principal -Settings $settings -Trigger $trigger|Out-Null
 Start-ScheduledTask -TaskName $task;Start-ScheduledTask -TaskName $backup
 for($i=0;$i -lt 200 -and (!(Test-Path -LiteralPath "$root\ready-primary.json") -or !(Test-Path -LiteralPath "$root\ready-backup.json"));$i++){if($controller.HasExited){throw 'Controller exited before watcher readiness'};Start-Sleep -Milliseconds 100}
 foreach($role in @('primary','backup')){$ready=[IO.File]::ReadAllText((Join-Path $root ('ready-'+$role+'.json')))|ConvertFrom-Json;if($ready.run -ne $id -or $ready.role -ne $role -or !$ready.jobHeld -or !$ready.maintenanceHeld -or $ready.tokenSid -ne 'S-1-5-18'){throw 'SYSTEM watcher readiness unconfirmed'}}
 $evidence=New-Object 'System.Collections.Generic.List[object]';$seenSetup=$false;$seenGuard=$false;$evidenceErrors=0;$watch=[Diagnostics.Stopwatch]::StartNew();$buffer=[Runtime.InteropServices.Marshal]::AllocHGlobal(8+256*[IntPtr]::Size)
 try{
  while($watch.Elapsed.TotalSeconds -lt 180 -and (!$controller.HasExited -or (Get-ScheduledTask -TaskName $backup).State -eq 'Running')){
   $returned=0;if(![CitaJobEvidence]::QueryInformationJobObject($job,3,$buffer,[uint32](8+256*[IntPtr]::Size),[ref]$returned)){$evidenceErrors++;Start-Sleep -Milliseconds 100;continue}
   $count=[Runtime.InteropServices.Marshal]::ReadInt32($buffer,4);if($count -lt 0 -or $count -gt 256){$evidenceErrors++;Start-Sleep -Milliseconds 100;continue}
   for($i=0;$i -lt $count;$i++){
    $pidValue=[int][Runtime.InteropServices.Marshal]::ReadIntPtr($buffer,8+$i*[IntPtr]::Size).ToInt64()
    $proc=Get-Process -Id $pidValue -ErrorAction SilentlyContinue;if(!$proc){continue}
    try{$inJob=$false;if(![CitaJobEvidence]::IsProcessInJob($proc.Handle,$job,[ref]$inJob) -or !$inJob){$evidenceErrors++;continue};$data=Get-CimInstance Win32_Process -Filter ('ProcessId='+$pidValue);if(!$data){continue};$isSetup=$data.ExecutablePath -ieq (Join-Path $root 'setup.exe');$isGuard=$data.Name -ieq 'powershell.exe' -and $data.CommandLine -match '(?i)InstallationGuard\.ps1';if(($isSetup -and !$seenSetup) -or ($isGuard -and !$seenGuard)){$ownerSid='';try{$owner=Invoke-CimMethod -InputObject $data -MethodName GetOwnerSid;if($owner.ReturnValue -eq 0){$ownerSid=[string]$owner.Sid}}catch{$evidenceErrors++};$evidence.Add([pscustomobject]@{pid=$pidValue;startedTicks=$proc.StartTime.ToUniversalTime().Ticks;kind=if($isSetup){'Setup'}else{'InstallationGuard'};ownerSid=$ownerSid;ownerMatchesPlan=($ownerSid -eq $config.UserSid);inJob=$true});if($isSetup){$seenSetup=$true};if($isGuard){$seenGuard=$true}}}catch{$evidenceErrors++}finally{$proc.Dispose()}
   }
   Start-Sleep -Milliseconds 100
  }
 }finally{[Runtime.InteropServices.Marshal]::FreeHGlobal($buffer)}
 if(!$controller.HasExited){throw 'Controller did not terminate within observation budget'}
 $proof=@{run=$id;setupInJob=$seenSetup;guardInJob=$seenGuard;observationErrors=$evidenceErrors;processes=$evidence;controllerStopped=$true}
 [IO.File]::WriteAllText((Join-Path $root 'process-evidence.json'),($proof|ConvertTo-Json -Depth 5 -Compress),[Text.UTF8Encoding]::new($false));Protect (Join-Path $root 'process-evidence.json')
 [pscustomobject]@{run=$id;controllerStopped=$true;setupInJob=$seenSetup;guardInJob=$seenGuard;processes=$evidence}|ConvertTo-Json -Depth 5 -Compress
}finally{if($job -ne [IntPtr]::Zero){[void][CitaJobEvidence]::CloseHandle($job)};$controller.Dispose()}
'@
foreach($pair in @(@('__ROOT__',$remote),@('__ID__',$id),@('__TASK__',$task),@('__HELPER__',$hashes['Cita.InstallerPhysical.exe']),@('__WORKER__',$hashes['RestoreWorker.ps1']),@('__TX__',$hashes['InstallationTransaction.ps1']),@('__INVOKER__',$hashes['Invoke-PhysicalRestore.ps1']),@('__SETUP__',$hashes['setup.exe']),@('__PAYLOAD__',$hashes['payload-manifest.json']),@('__RESUME__',[string][bool]$ResumePrepared))){$start=$start.Replace($pair[0],$pair[1])}
$startPath=Join-Path $workspace ($(if($ResumePrepared){"runtime/installer-install-resume-"}else{"runtime/installer-install-start-"})+$id+'.ps1');[IO.File]::WriteAllText($startPath,$start,[Text.UTF8Encoding]::new($true));$startHash=(Get-FileHash -LiteralPath $startPath).Hash
if($ResumePrepared){
 $resume=[ordered]@{schema=1;run=$id;previousAttemptState='PreStart';originalStartSha256=$oldStartHash;continuationStartSha256=$startHash;originalFailureRetained=$true;state='MayReplaceStart';utc=[DateTimeOffset]::UtcNow.ToString('o')}
 $stream=[IO.File]::Open($resumeReceipt,'CreateNew','Write','Read');try{$bytes=[Text.Encoding]::UTF8.GetBytes(($resume|ConvertTo-Json -Compress));$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
}
Invoke-BoundedScp $startPath ("citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/$folder/start.ps1")
$invokeStart=@'
$ErrorActionPreference='Stop';$root='__ROOT__';$id='__ID__'
try{
 if((Get-FileHash -LiteralPath "$root\start.ps1").Hash -ne '__STARTHASH__'){throw 'Start script differs'}
 & ([ScriptBlock]::Create([IO.File]::ReadAllText("$root\start.ps1")))
}catch{
 try{$errorType=$_.Exception.GetType().Name;if($errorType -cnotmatch '^[A-Za-z][A-Za-z0-9]{0,80}$'){$errorType='UnknownError'};$report=@{run=$id;errorType=$errorType;line=[int]$_.InvocationInfo.ScriptLineNumber;utc=[DateTimeOffset]::UtcNow.ToString('o')};[IO.File]::WriteAllText("$root\start-failure.json",($report|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))}catch{}
 exit 1
}
'@
$invokeStart=$invokeStart.Replace('__ROOT__',$remote).Replace('__ID__',$id).Replace('__STARTHASH__',$startHash)
$started=(Invoke-BoundedRm $invokeStart 180)|ConvertFrom-Json
if($started.run -ne $id -or !$started.controllerStopped){throw 'Controller completion unconfirmed.'}
$collect=@'
$ErrorActionPreference='Stop';$root='__ROOT__';$id='__ID__';$task='__TASK__';$agent='C:\Program Files\RoutePilot\Agent';$actual='C:\Users\CITASTATION\AppData\Local\Programs\CITA Route\client'
for($i=0;$i -lt 500 -and !(Test-Path -LiteralPath "$root\result-primary.json") -and !(Test-Path -LiteralPath "$root\result-backup.json");$i++){Start-Sleep -Milliseconds 100}
$results=@(foreach($role in @('primary','backup')){$path=Join-Path $root ('result-'+$role+'.json');if(Test-Path -LiteralPath $path){[IO.File]::ReadAllText($path)|ConvertFrom-Json}})
if(!$results.Count -or @($results|Where-Object {$_.run -ne $id -or !$_.success -or $_.outcome -ne 'Committed' -or $_.jobActiveAfter -ne 0}).Count){throw 'New committed installation not confirmed'}
$plan=[IO.File]::ReadAllText("$root\plan.json")|ConvertFrom-Json;. ([ScriptBlock]::Create([IO.File]::ReadAllText("$root\InstallationTransaction.ps1")));$j=Read-InstallJournal $actual $plan.UserSid
if(!$j -or $j.id -cne $id -or $j.phase -ne 'Committed' -or $j.incomingManifestHash -ne '__PAYLOAD__' -or (Get-FileHash -LiteralPath (Join-Path $actual 'manifest.json')).Hash -ne '__PAYLOAD__'){throw 'New transaction or manifest differs'}
$manifest=[IO.File]::ReadAllText("$root\payload-manifest.json")|ConvertFrom-Json
foreach($item in $manifest.files){if($item.path -in @('force-direct.txt','force-vpn.txt')){continue};$file=Get-InstallPublicPath $actual $item.path;if(!(Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).Length -ne $item.bytes -or (Get-FileHash -LiteralPath $file).Hash -ne $item.sha256){throw 'Installed public file differs'}}
function NativeDigest {$native=Join-Path $agent 'native';$rootLength=$native.Length+1;$rows=New-Object 'System.Collections.Generic.List[string]';$items=@(Get-ChildItem -LiteralPath $native -Recurse -Force);if(@($items|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Native module link refused'};foreach($file in @($items|Where-Object {!$_.PSIsContainer}|Sort-Object FullName)){$relative=$file.FullName.Substring($rootLength);if($relative -ieq 'intent.json'){continue};$rows.Add($relative+'|'+$file.Length+'|'+(Get-FileHash -LiteralPath $file.FullName).Hash)};$sha=[Security.Cryptography.SHA256]::Create();try{return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($rows -join "`n"))))).Replace('-','')}finally{$sha.Dispose()}}
$baseline=[IO.File]::ReadAllText("$root\actual-baseline.json")|ConvertFrom-Json;$svc=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'"
if((Get-FileHash -LiteralPath (Join-Path $agent 'native\intent.json')).Hash -ne $baseline.intentSha -or (Get-FileHash -LiteralPath (Join-Path $agent 'RoutePilot.Agent.exe')).Hash -ne $baseline.agentSha -or (NativeDigest) -ne $baseline.nativeDigest -or !$svc -or $svc.State -ne 'Running' -or $svc.ProcessId -ne $baseline.servicePid){throw 'Protected service, intent or modules changed'}
foreach($name in @($task,($task+'-Backup'))){$definition=Get-ScheduledTask -TaskName $name -ErrorAction Stop;$principal=[string]$definition.Principal.UserId;$sid=if($principal -match '^S-1-'){$principal}else{([Security.Principal.NTAccount]::new($principal)).Translate([Security.Principal.SecurityIdentifier]).Value};$role=if($name -eq $task){'--primary'}else{'--backup'};if($sid -ne 'S-1-5-18' -or $definition.Actions.Count -ne 1 -or $definition.Actions[0].Execute -ine "$root\Cita.InstallerPhysical.exe" -or $definition.Actions[0].Arguments -ne ($role+' "'+$root+'"')){throw 'Watcher task identity differs'}}
for($i=0;$i -lt 100;$i++){if((Get-ScheduledTask -TaskName $task).State -ne 'Running' -and (Get-ScheduledTask -TaskName ($task+'-Backup')).State -ne 'Running'){break};Start-Sleep -Milliseconds 100}
if((Get-ScheduledTask -TaskName $task).State -eq 'Running' -or (Get-ScheduledTask -TaskName ($task+'-Backup')).State -eq 'Running'){throw 'Watcher completion unconfirmed'}
$taskResults=@(foreach($name in @($task,($task+'-Backup'))){$info=Get-ScheduledTaskInfo -TaskName $name;[pscustomobject]@{role=if($name -eq $task){'primary'}else{'backup'};lastResult=[int64]$info.LastTaskResult}})
$watcherFailures=@($taskResults|Where-Object {$_.lastResult -ne 0}).Count
$remaining=@();for($i=0;$i -lt 50;$i++){
 $remaining=@(Get-CimInstance Win32_Process|Where-Object {$_.ProcessId -ne $PID -and (($_.ExecutablePath -and $_.ExecutablePath.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) -or ($_.CommandLine -and $_.CommandLine.IndexOf($root,[StringComparison]::OrdinalIgnoreCase) -ge 0))})
 if(!$remaining.Count){break};Start-Sleep -Milliseconds 100
}
if($remaining.Count){throw 'Installer or helper processes remain; completion is unconfirmed'}
$evidence=[IO.File]::ReadAllText("$root\process-evidence.json")|ConvertFrom-Json
if($evidence.run -ne $id -or !$evidence.controllerStopped){throw 'Process evidence differs'}
$log=Join-Path $root 'setup.log';if(!(Test-Path -LiteralPath $log)){throw 'Setup log missing'};$logHash=(Get-FileHash -LiteralPath $log).Hash;$lines=[IO.File]::ReadAllLines($log);$sameVersion=@($lines|Where-Object {$_ -match 'CITA_VERSION_DECISION: same 0\.6\.9\.0'}).Count -gt 0
[pscustomobject]@{run=$id;success=$true;outcome='Committed';journalId=$j.id;publicFiles=@($manifest.files).Count;setupEvidence=if($evidence.setupInJob){'ObservedInJob'}else{'Unobserved'};guardEvidence=if($evidence.guardInJob){'ObservedInJob'}else{'Unobserved'};observationErrors=$evidence.observationErrors;processes=$evidence.processes;processesRemaining=0;taskResults=$taskResults;watcherFailures=$watcherFailures;sameVersionDecisionObserved=$sameVersion;installerLogSha256=$logHash;intentUnchanged=$true;servicePidUnchanged=$true;protectedModulesUnchanged=$true;rootRetained=$true;tasksRetained=$true;scope='Public client files only; registry, shortcuts and Inno uninstaller are outside rollback proof'}|ConvertTo-Json -Depth 6 -Compress
'@
$collect=$collect.Replace('__ROOT__',$remote).Replace('__ID__',$id).Replace('__TASK__',$task).Replace('__PAYLOAD__',$hashes['payload-manifest.json'])
$collectPath=Join-Path $workspace ("runtime/installer-install-collect-"+$id+'.ps1');[IO.File]::WriteAllText($collectPath,$collect,[Text.UTF8Encoding]::new($true));$collectHash=(Get-FileHash -LiteralPath $collectPath).Hash
Invoke-BoundedScp $collectPath ("citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/$folder/collect.ps1")
$result=(Invoke-BoundedRm "if((Get-FileHash -LiteralPath '$remote\collect.ps1').Hash -ne '$collectHash'){throw 'Collect script differs'};& ([ScriptBlock]::Create([IO.File]::ReadAllText('$remote\collect.ps1')))" 180)|ConvertFrom-Json
if($result.run -ne $id -or !$result.success -or $result.outcome -ne 'Committed' -or $result.journalId -cne $id){throw 'Installation result unconfirmed; journal retained.'}
$result|Add-Member -NotePropertyName componentHashes -NotePropertyValue $hashes
$result|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $workspace 'runtime/installer-guard-install-result.json') -Encoding UTF8
$entry.state='VerifiedRetained';$update=$journal+'.'+[guid]::NewGuid().ToString('N')+'.new'
try{$stream=[IO.File]::Open($update,'CreateNew','Write','Read');try{$bytes=[Text.Encoding]::UTF8.GetBytes(($entry|ConvertTo-Json -Depth 5 -Compress));$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()};[IO.File]::Replace($update,$journal,[NullString]::Value)}finally{if(Test-Path -LiteralPath $update){Remove-Item -LiteralPath $update -Force}}
if($ResumePrepared){
 $resume.state='CommittedVerified';$resume.resultSha256=(Get-FileHash -LiteralPath (Join-Path $workspace 'runtime/installer-guard-install-result.json')).Hash
 $resumeUpdate=$resumeReceipt+'.'+[guid]::NewGuid().ToString('N')+'.new'
 try{$stream=[IO.File]::Open($resumeUpdate,'CreateNew','Write','Read');try{$bytes=[Text.Encoding]::UTF8.GetBytes(($resume|ConvertTo-Json -Compress));$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()};[IO.File]::Replace($resumeUpdate,$resumeReceipt,[NullString]::Value)}finally{if(Test-Path -LiteralPath $resumeUpdate){Remove-Item -LiteralPath $resumeUpdate -Force}}
}
Remove-Item -LiteralPath $startPath,$collectPath -Force
$result
