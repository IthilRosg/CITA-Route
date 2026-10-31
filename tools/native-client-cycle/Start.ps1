$ErrorActionPreference='Stop';$root='__ROOT__';$id='__ID__';$agent='C:\Program Files\RoutePilot\Agent'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -cne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong station.'}
function Protect-Cycle([string]$Path){
 $item=Get-Item -LiteralPath $Path -Force;if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cycle path linked.'}
 $acl=if($item.PSIsContainer){[Security.AccessControl.DirectorySecurity]::new()}else{[Security.AccessControl.FileSecurity]::new()};$inherit=if($item.PSIsContainer){'ContainerInherit,ObjectInherit'}else{'None'}
 $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'));$acl.SetAccessRuleProtection($true,$false)
 foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl',$inherit,'None','Allow'))};Set-Acl -LiteralPath $Path -AclObject $acl
}
Protect-Cycle $root;foreach($file in Get-ChildItem -LiteralPath $root -Force){Protect-Cycle $file.FullName}
$sources='__PINS__'|ConvertFrom-Json
foreach($pin in $sources.PSObject.Properties){if((Get-FileHash -LiteralPath (Join-Path $root $pin.Name)).Hash -cne $pin.Value){throw 'Cycle source differs.'}}
$agentSha=(Get-FileHash -LiteralPath (Join-Path $agent 'RoutePilot.Agent.exe')).Hash
if($agentSha -cne '__AGENT__'){throw 'Cycle installed agent differs.'}
$intent=[IO.File]::ReadAllText((Join-Path $agent 'native\intent.json'))|ConvertFrom-Json
$selection=[IO.File]::ReadAllText((Join-Path $agent 'native\service.json'))|ConvertFrom-Json
$catalogName=$selection.CatalogName;if(!$catalogName){$catalogName='native-catalog.json'}
if([IO.Path]::GetFileName($catalogName) -cne $catalogName){throw 'Cycle catalog path differs.'}
$catalogPath=Join-Path $agent ('native\'+$catalogName)
if((Get-FileHash -LiteralPath $catalogPath).Hash -ine $selection.CatalogSha256){throw 'Cycle catalog pin differs.'}
$catalog=[IO.File]::ReadAllText($catalogPath)|ConvertFrom-Json
if([IO.Path]::GetFileName($catalog.Policy.Name) -cne $catalog.Policy.Name){throw 'Cycle policy path differs.'}
$policyPath=Join-Path $agent ('native\'+$catalog.Policy.Name)
if((Get-FileHash -LiteralPath $policyPath).Hash -ine $catalog.Policy.Sha256){throw 'Cycle policy pin differs.'}
$policy=[IO.File]::ReadAllText($policyPath)|ConvertFrom-Json
if($intent.Desired -isnot [bool] -or $intent.Desired -or @($policy.Direct) -cnotcontains '192.168.0.0/16'){throw 'Disconnected state or management LAN bypass unavailable.'}
$config=[IO.File]::ReadAllText((Join-Path $agent 'agent-config.json'))|ConvertFrom-Json
$owner='CitaNativeCycle-'+$id+'-Owner';$rescue='CitaNativeCycle-'+$id+'-Rescue';$backup=$rescue+'-Backup';$cid=[guid]::NewGuid().ToString('N')
$plan=@{Schema=1;Run=$id;AgentSha=$agentSha;ConnectionId=$cid;DeadlineUtc=[DateTimeOffset]::UtcNow.AddSeconds(120).ToString('O');InitialOperation=$intent.Operation;InitialGeneration=$intent.Generation;ProfileSha=$catalog.Profile.Sha256;ExpectedExitHash=$catalog.ExpectedExitHash;ControllerTask=$owner;FilesSource=$sources;RoutingSample=__ROUTING_SAMPLE__}
$plan.GuiMode=__GUI__
$plan.CoreCrash=__CORE_CRASH__;$plan.CoreSha='__CORE__'
if($plan.CoreCrash -and (Get-FileHash -LiteralPath (Join-Path $agent 'native\native-core.exe')).Hash -cne $plan.CoreSha){throw 'Fault-test core differs.'}
if($plan.GuiMode){
 $plan.AppRoot=$config.AppRoot;$plan.AppSha='__APP__';$plan.GuiSession=2
 . ([ScriptBlock]::Create([IO.File]::ReadAllText((Join-Path $root 'Gui.ps1'))));Assert-GuiCyclePlan $plan $root
}
[IO.File]::WriteAllText((Join-Path $root 'plan.json'),($plan|ConvertTo-Json -Depth 5 -Compress),[Text.UTF8Encoding]::new($false));Protect-Cycle (Join-Path $root 'plan.json')
$shell=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 4) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
foreach($name in @($owner,$rescue,$backup)){if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){throw 'Cycle task exists.'}}
$ownerPrincipal=New-ScheduledTaskPrincipal -UserId $config.UserSid -LogonType S4U -RunLevel Highest
$ownerAction=New-ScheduledTaskAction -Execute $shell -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $root 'Controller.ps1')+'" -RunRoot "'+$root+'"')
Register-ScheduledTask -TaskName $owner -Action $ownerAction -Principal $ownerPrincipal -Settings $settings|Out-Null
if($plan.GuiMode){
 $guiPrincipal=New-ScheduledTaskPrincipal -UserId $config.UserSid -LogonType Interactive -RunLevel Limited
 $guiAction=New-ScheduledTaskAction -Execute (Join-Path $plan.AppRoot 'RoutePilot.exe') -WorkingDirectory $plan.AppRoot -Argument ('--cita-lab-id='+$id)
 Register-ScheduledTask -TaskName ('CitaNativeCycle-'+$id+'-Gui') -Action $guiAction -Principal $guiPrincipal -Settings $settings|Out-Null
}
$system=New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount -RunLevel Highest
$held=[Collections.Generic.List[object]]::new();$rescuePids=@()
try{
 foreach($role in @('primary','backup')){
  $task=if($role -ceq 'primary'){$rescue}else{$backup}
  $rescueAction=New-ScheduledTaskAction -Execute $shell -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $root 'Rescue.ps1')+'" -RunRoot "'+$root+'" -Role '+$role)
  Register-ScheduledTask -TaskName $task -Action $rescueAction -Principal $system -Settings $settings|Out-Null;Start-ScheduledTask -TaskName $task
 }
 foreach($role in @('primary','backup')){
  $path=Join-Path $root ('rescue-armed-'+$role+'.json');for($i=0;$i -lt 250 -and ![IO.File]::Exists($path);$i++){Start-Sleep -Milliseconds 100}
  if(![IO.File]::Exists($path)){throw 'Independent rescue did not arm; native start was not requested.'}
  $armed=[IO.File]::ReadAllText($path)|ConvertFrom-Json
  if($armed.Run -cne $id -or $armed.ConnectionId -cne $cid){throw 'Cycle rescue witness differs.'}
  $p=Get-Process -Id $armed.Pid -ErrorAction Stop;$held.Add($p);$null=$p.Handle
  if($p.HasExited -or $p.StartTime.ToUniversalTime().Ticks -ne $armed.StartedUtcTicks -or $p.Path -ine $shell){throw 'Cycle rescue is not live.'};$rescuePids+=,$p.Id
 }
 if($held[0].Id -eq $held[1].Id -or @($held|Where-Object {$_.HasExited}).Count){throw 'Independent rescues changed before owner admission.'}
 if(__ARM_ONLY__){[IO.File]::WriteAllText((Join-Path $root 'cleanup-request.json'),(@{Run=$id}|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false));Protect-Cycle (Join-Path $root 'cleanup-request.json')}
 else{Start-ScheduledTask -TaskName $owner}
}finally{foreach($p in $held){$p.Dispose()}}
[pscustomobject]@{Run=$id;ConnectionId=$cid;OwnerTask=$owner;RescueTask=$rescue;BackupTask=$backup;RescuePids=$rescuePids;Root=$root}|ConvertTo-Json -Compress
