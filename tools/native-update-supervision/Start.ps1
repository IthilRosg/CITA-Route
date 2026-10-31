$ErrorActionPreference='Stop'
$root='__ROOT__';$id='__ID__';$task='__TASK__';$image=Join-Path $root 'Cita.NativeUpdater.exe'
$agent=Join-Path $env:ProgramFiles 'RoutePilot\Agent'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -cne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong physical station.'}
function Protect([string]$Path){
 $item=Get-Item -LiteralPath $Path -Force;if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Fixture path linked.'}
 $acl=if($item.PSIsContainer){[Security.AccessControl.DirectorySecurity]::new()}else{[Security.AccessControl.FileSecurity]::new()};$inherit=if($item.PSIsContainer){'ContainerInherit,ObjectInherit'}else{'None'}
 $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'));$acl.SetAccessRuleProtection($true,$false)
 foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl',$inherit,'None','Allow'))}
 Set-Acl -LiteralPath $Path -AclObject $acl
}
Protect $root
foreach($f in Get-ChildItem -LiteralPath $root -Force){Protect $f.FullName}
if((Get-FileHash -LiteralPath $image).Hash -cne '__HELPER__'){throw 'Helper bytes differ.'}
foreach($pair in @(@('Actor.ps1','__ACTOR__'),@('Recover.ps1','__RECOVER__'))){if((Get-FileHash -LiteralPath (Join-Path $root $pair[0])).Hash -cne $pair[1]){throw 'Fixture script bytes differ.'}}
if(Test-Path -LiteralPath (Join-Path $agent 'native-upgrade.pending')){throw 'Production upgrade is pending.'}
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if($sid -cne 'S-1-5-21-226018510-1244436870-2661740948-1001'){throw 'Wrong station owner.'}
$s=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'"
$intent=[IO.File]::ReadAllText((Join-Path $agent 'native\intent.json'))|ConvertFrom-Json
if($s.State -cne 'Running' -or $intent.Desired -isnot [bool] -or $intent.Desired){throw 'Station is not quiescent.'}
$base=@{ServicePid=[int]$s.ProcessId;StartMode=$s.StartMode;AgentSha=(Get-FileHash -LiteralPath (Join-Path $agent 'RoutePilot.Agent.exe')).Hash;IntentSha=(Get-FileHash -LiteralPath (Join-Path $agent 'native\intent.json')).Hash}
[IO.File]::WriteAllText((Join-Path $root 'baseline.json'),($base|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false));Protect (Join-Path $root 'baseline.json')
$plan=@{Schema=1;Run=$id;OwnerSid=$sid;Boot=[string](Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().Ticks;DeadlineUtc=[DateTimeOffset]::UtcNow.AddMinutes(4).ToString('O');HelperSha='__HELPER__';IntentSha=$base.IntentSha;Files=@{'Actor.ps1'='__ACTOR__';'Recover.ps1'='__RECOVER__';'baseline.json'=(Get-FileHash -LiteralPath (Join-Path $root 'baseline.json')).Hash}}
[IO.File]::WriteAllText((Join-Path $root 'plan.json'),($plan|ConvertTo-Json -Depth 5 -Compress),[Text.UTF8Encoding]::new($false));Protect (Join-Path $root 'plan.json')
$names=@($task,($task+'-Backup'))
foreach($name in $names){if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){throw 'Task already exists.'}}
$principal=New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount -RunLevel Highest
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 6) -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
foreach($role in @('primary','backup')){
 $name=if($role -ceq 'primary'){$task}else{$task+'-Backup'}
 $action=New-ScheduledTaskAction -Execute $image -Argument ('--'+$role+' "'+$root+'"')
 Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings|Out-Null
}
$info=[Diagnostics.ProcessStartInfo]::new();$info.FileName=$image;$info.Arguments='--controller "'+$root+'"';$info.UseShellExecute=$false;$info.CreateNoWindow=$true
$controller=[Diagnostics.Process]::Start($info)
for($i=0;$i -lt 150 -and !(Test-Path -LiteralPath (Join-Path $root 'controller.json'));$i++){if($controller.HasExited){throw 'Controller exited before descriptor.'};Start-Sleep -Milliseconds 100}
if(!(Test-Path -LiteralPath (Join-Path $root 'controller.json'))){throw 'Controller descriptor deadline.'}
$d=[IO.File]::ReadAllText((Join-Path $root 'controller.json'))|ConvertFrom-Json
if($d.Run -cne $id -or $d.Process.Pid -ne $controller.Id -or $d.Process.StartedUtcTicks -ne $controller.StartTime.ToUniversalTime().Ticks -or $controller.MainModule.FileName -ine $image){throw 'Controller identity differs.'}
foreach($name in $names){Start-ScheduledTask -TaskName $name}
for($i=0;$i -lt 350 -and !(Test-Path -LiteralPath (Join-Path $root 'fixture-ready.json'));$i++){if($controller.HasExited){throw 'Controller exited before fixture.'};Start-Sleep -Milliseconds 100}
if(!(Test-Path -LiteralPath (Join-Path $root 'fixture-ready.json'))){throw 'Fixture readiness deadline.'}
$ready=[IO.File]::ReadAllText((Join-Path $root 'ready-primary.json'))|ConvertFrom-Json
$primary=Get-Process -Id $ready.Process.Pid -ErrorAction Stop
try{
 if($ready.Run -cne $id -or $ready.Role -cne 'primary' -or $primary.HasExited -or $primary.Path -ine $image -or $primary.StartTime.ToUniversalTime().Ticks -ne $ready.Process.StartedUtcTicks){throw 'Primary identity differs.'}
 $primary.Kill();if(!$primary.WaitForExit(5000)){throw 'Primary exit unconfirmed.'}
}finally{$primary.Dispose()}
if(!$controller.HasExited){try{$controller.Kill()}catch{if(!$controller.HasExited){throw}}}
if(!$controller.WaitForExit(5000)){throw 'Controller exit unconfirmed.'}
[pscustomobject]@{Run=$id;PrimaryKilled=$true;ControllerExited=$true;Baseline=$base}|ConvertTo-Json -Depth 5 -Compress
