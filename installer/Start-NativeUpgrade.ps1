param([Parameter(Mandatory)][string]$RunRoot,[Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$ExpectedHelperSha,[Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$NewAgentSha,[Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$ArchiveSha)
$ErrorActionPreference='Stop'
. "$PSScriptRoot\NativeUpgradeTransaction.ps1"
. "$PSScriptRoot\NativeUpgradeService.ps1"
if(-not([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Native upgrade requires administrator approval.'}
$agent=Join-Path $env:ProgramFiles 'RoutePilot\Agent';$root=[IO.Path]::GetFullPath($RunRoot).TrimEnd('\');$leaf=[IO.Path]::GetFileName($root)
if($root -ine $PSScriptRoot -or [IO.Path]::GetDirectoryName($root) -ine $agent -or $leaf -cnotmatch '^native-update-[a-f0-9]{32}$'){throw 'Upgrade payload root differs.'}
$id=$leaf.Substring(14);$image=Join-Path $root 'Cita.NativeUpdater.exe'
function Protect-NativeUpgradeInput([string]$Path) {
    Assert-NativeUpgradePath $Path;$item=Get-Item -LiteralPath $Path -Force
    $acl=if($item.PSIsContainer){[Security.AccessControl.DirectorySecurity]::new()}else{[Security.AccessControl.FileSecurity]::new()};$inherit=if($item.PSIsContainer){'ContainerInherit,ObjectInherit'}else{'None'}
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'));$acl.SetAccessRuleProtection($true,$false)
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl',$inherit,'None','Allow'))}
    Set-Acl -LiteralPath $Path -AclObject $acl
}
Protect-NativeUpgradeInput $root
$payload=@('Cita.NativeUpdater.exe','Actor.ps1','Recover.ps1','NativeUpgradeWorker.ps1','NativeUpgradeRecovery.ps1','NativeUpgradeFlow.ps1','NativeUpgradeTransaction.ps1','NativeUpgradePair.ps1','NativeUpgradeService.ps1','Install-NativeComponents.ps1','NativeMaintenance.ps1','AgentLease.ps1','Incoming.Agent.exe','native-components.zip','Start-NativeUpgrade.ps1')
$items=@(Get-ChildItem -LiteralPath $root -Force)
if($items.Count -ne $payload.Count){throw 'Upgrade payload count differs.'}
foreach($file in $items){if($file.PSIsContainer -or $file.Name -cnotin $payload){throw 'Upgrade payload contains unknown entries.'};Protect-NativeUpgradeInput $file.FullName}
if((Get-NativeUpgradeHash $image) -cne $ExpectedHelperSha -or (Get-NativeUpgradeHash (Join-Path $root 'Incoming.Agent.exe')) -cne $NewAgentSha -or (Get-NativeUpgradeHash (Join-Path $root 'native-components.zip')) -cne $ArchiveSha){throw 'Upgrade payload digest differs.'}
if(Test-Path -LiteralPath (Join-Path $agent 'native-upgrade.pending')){throw 'An unfinished native upgrade requires recovery.'}
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$config=[IO.File]::ReadAllText((Join-Path $agent 'agent-config.json'))|ConvertFrom-Json
if($config.UserSid -cne $sid){throw 'Native upgrade belongs to another user.'}
$lease=Open-NativeUpgradeLease $agent
try {
    $svc=Get-NativeUpgradeServiceSnapshot $lease
    if($svc.State -cne 'Running' -or $svc.ProcessId -le 0){throw 'Native upgrade requires a running, disconnected service.'}
    $process=Get-Process -Id $svc.ProcessId -ErrorAction Stop
    try{$null=$process.Handle;if($process.HasExited -or $process.Path -ine (Join-Path $agent 'RoutePilot.Agent.exe')){throw 'Original native service process differs.'};$ticks=$process.StartTime.ToUniversalTime().Ticks}finally{$process.Dispose()}
    Initialize-NativeUpgradePipe
    $state=[Cita.NativeUpgradePipe]::Exchange('{"Command":"native-status"}',$svc.ProcessId)|ConvertFrom-Json
    if(!$state.Success -or !$state.Native){throw 'Native route state unavailable.'}
    Assert-NativePreparationState $state.Native
    $intent=[IO.File]::ReadAllText((Join-Path $agent 'native\intent.json'))|ConvertFrom-Json
    Assert-NativeUpgradeReadyReply $state $intent.Operation $intent.Generation $state.Native.phase
    $serviceProof=[pscustomobject]@{ProcessId=$svc.ProcessId;StartedUtcTicks=$ticks;StartType=$svc.StartType}
    $upgradeInput=New-NativeUpgradeInput $lease $id $config.AppRoot $serviceProof $intent $NewAgentSha $ArchiveSha $state.Native.phase
    $inputPath=Join-Path $root 'upgrade-input.json'
    if(Test-Path -LiteralPath $inputPath){throw 'Upgrade input already exists.'}
    [IO.File]::WriteAllText($inputPath,($upgradeInput|ConvertTo-Json -Depth 8 -Compress),[Text.UTF8Encoding]::new($false));Protect-NativeUpgradeInput $inputPath
    $pins=@{};foreach($name in @($payload+'upgrade-input.json')){if($name -cne 'Cita.NativeUpdater.exe'){$pins[$name]=Get-NativeUpgradeHash (Join-Path $root $name)}}
    $plan=@{Schema=1;Run=$id;OwnerSid=$sid;Boot=[string](Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().Ticks;DeadlineUtc=[DateTimeOffset]::UtcNow.AddMinutes(5).ToString('O');HelperSha=$ExpectedHelperSha;IntentSha=(Get-NativeUpgradeHash (Join-Path $agent 'native\intent.json'));Files=$pins}
    [IO.File]::WriteAllText((Join-Path $root 'plan.json'),($plan|ConvertTo-Json -Depth 5 -Compress),[Text.UTF8Encoding]::new($false));Protect-NativeUpgradeInput (Join-Path $root 'plan.json')
}finally{$lease.Handle.Dispose()}
$task='CitaNativeUpgrade-'+$id
foreach($name in @($task,($task+'-Backup'),($task+'-Boot'),($task+'-Controller'))){if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){throw 'Upgrade task already exists.'}}
$principal=New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount -RunLevel Highest
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 10) -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
foreach($role in @('primary','backup','recover-on-boot')){
    $name=if($role -ceq 'primary'){$task}elseif($role -ceq 'backup'){$task+'-Backup'}else{$task+'-Boot'}
    $action=New-ScheduledTaskAction -Execute $image -Argument ('--'+$role+' "'+$root+'"')
    if($role -ceq 'recover-on-boot'){Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings -Trigger (New-ScheduledTaskTrigger -AtStartup)|Out-Null}
    else{Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings|Out-Null}
}
# A direct descendant of Windows OpenSSH can be killed with the SSH session's
# outer job. Task Scheduler owns the controller independently of the launcher.
$owner=New-ScheduledTaskPrincipal -UserId $sid -LogonType S4U -RunLevel Highest
$controllerAction=New-ScheduledTaskAction -Execute $image -Argument ('--controller "'+$root+'"')
Register-ScheduledTask -TaskName ($task+'-Controller') -Action $controllerAction -Principal $owner -Settings $settings|Out-Null
Start-ScheduledTask -TaskName ($task+'-Controller')
for($i=0;$i -lt 200 -and ![IO.File]::Exists((Join-Path $root 'controller.json'));$i++){Start-Sleep -Milliseconds 100}
if(![IO.File]::Exists((Join-Path $root 'controller.json'))){throw 'Upgrade supervisor admission deadline.'}
$proof=[IO.File]::ReadAllText((Join-Path $root 'controller.json'))|ConvertFrom-Json
$controller=Get-Process -Id $proof.Process.Pid -ErrorAction Stop;$null=$controller.Handle
if($proof.Run -cne $id -or $proof.Process.Pid -ne $controller.Id -or $proof.Process.StartedUtcTicks -ne $controller.StartTime.ToUniversalTime().Ticks -or $controller.Path -ine $image){throw 'Upgrade supervisor identity differs.'}
Start-ScheduledTask -TaskName $task;Start-ScheduledTask -TaskName ($task+'-Backup')
[pscustomobject]@{Run=$id;Root=$root;ControllerPid=$controller.Id;ControllerStartTicks=$proof.Process.StartedUtcTicks;PrimaryTask=$task;BackupTask=($task+'-Backup');BootTask=($task+'-Boot');ControllerTask=($task+'-Controller')}|ConvertTo-Json -Compress
