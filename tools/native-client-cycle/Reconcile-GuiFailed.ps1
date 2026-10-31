$ErrorActionPreference='Stop';$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
$journal=Join-Path $workspace 'runtime/native-client-cycle-active.json';$e=Get-Content -LiteralPath $journal -Raw|ConvertFrom-Json
if($e.Run -cnotmatch '^[a-f0-9]{32}$' -or $e.Root -cne ('C:\Program Files\RoutePilot\Agent\native-cycle-'+$e.Run)){throw 'GUI reconciliation scope differs.'}
$code=@'
$ErrorActionPreference='Stop';$r='__ROOT__';$id='__ID__';$agent='C:\Program Files\RoutePilot\Agent'
if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($r)) -ine $agent -or [IO.Path]::GetFileName($r) -cne ('native-cycle-'+$id) -or ((Get-Item -LiteralPath $r).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reconciliation scope differs.'}
$p=Get-Content -LiteralPath (Join-Path $r 'plan.json') -Raw|ConvertFrom-Json
if(!$p.GuiMode -or $p.Run -cne $id){throw 'Not a GUI cycle.'}
foreach($pin in $p.FilesSource.PSObject.Properties){if((Get-FileHash -LiteralPath (Join-Path $r $pin.Name)).Hash -cne $pin.Value){throw 'Source differs.'}}
foreach($role in @('primary','backup')){$proof=Get-Content -LiteralPath (Join-Path $r ('rescue-result-'+$role+'.json')) -Raw|ConvertFrom-Json;if($proof.run -cne $id -or $proof.outcome -cne 'Attention'){throw 'Expected terminal failure evidence missing.'}}
$names=@('Owner','Gui','Rescue','Rescue-Backup')|ForEach-Object {'CitaNativeCycle-'+$id+'-'+$_}
foreach($name in $names){$task=Get-ScheduledTask -TaskName $name;if([string]$task.State -ceq 'Running'){throw 'A task still runs.'}}
if([string](Get-ScheduledTask -TaskName ('CitaNativeCycle-'+$id+'-Owner')).State -cne 'Disabled' -or [string](Get-ScheduledTask -TaskName ('CitaNativeCycle-'+$id+'-Gui')).State -cne 'Disabled'){throw 'A sender task was not retired.'}
if(@(Get-CimInstance Win32_Process|Where-Object {$_.ProcessId -ne $PID -and ($_.Name -eq 'RoutePilot.exe' -or ($_.CommandLine -and $_.CommandLine.IndexOf($r,[StringComparison]::OrdinalIgnoreCase) -ge 0))}).Count){throw 'An actor remains.'}
. ([ScriptBlock]::Create([IO.File]::ReadAllText((Join-Path $r 'NativeUpgradeTransaction.ps1'))));. ([ScriptBlock]::Create([IO.File]::ReadAllText((Join-Path $r 'NativeUpgradeService.ps1'))))
$lease=Open-NativeUpgradeLease $agent
try{
 $service=Get-NativeUpgradeServiceSnapshot $lease;if($service.State -cne 'Running' -or (Get-NativeUpgradeHash (Join-Path $agent 'RoutePilot.Agent.exe')) -cne $p.AgentSha){throw 'Service differs.'}
 Initialize-NativeUpgradePipe;$reply=[Cita.NativeUpgradePipe]::Exchange('{"Command":"native-status"}',$service.ProcessId)|ConvertFrom-Json
 $initial=$reply.Native.operation -ceq $p.InitialOperation -and $reply.Native.generation -eq $p.InitialGeneration
 $ownStopped=$false;$ownPath=Join-Path $r 'gui-connection.json'
 if(Test-Path -LiteralPath $ownPath){$own=Get-Content -LiteralPath $ownPath -Raw|ConvertFrom-Json;$ownStopped=$own.Run -ceq $id -and $own.ProfileSha -ceq $p.ProfileSha -and $own.InitialOperation -ceq $p.InitialOperation -and $own.InitialGeneration -eq $p.InitialGeneration -and $reply.Native.connectionId -ceq $own.ConnectionId -and $reply.Native.generation -gt $p.InitialGeneration}
 if(!$reply.Success -or $reply.Native.desired -isnot [bool] -or $reply.Native.desired -or $reply.Native.phase -cne 'Disconnected' -or !($initial -or $ownStopped) -or $reply.Native.profileId -cne $p.ProfileSha){throw 'Disconnected state is not confirmed; no connection will be stopped.'}
 . ([ScriptBlock]::Create([IO.File]::ReadAllText((Join-Path $r 'Gui.ps1'))));Restore-GuiCyclePreferences $p $r
}finally{$lease.Handle.Dispose()}
foreach($name in $names){Disable-ScheduledTask -TaskName $name|Out-Null}
$files=@(Get-ChildItem -LiteralPath $r -Force);foreach($f in $files){if($f.PSIsContainer -or ($f.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unknown cleanup child.'}}
foreach($name in $names){Unregister-ScheduledTask -TaskName $name -Confirm:$false}
foreach($f in $files){Remove-Item -LiteralPath $f.FullName -Force};Remove-Item -LiteralPath $r
@{Run=$id;Outcome='FailedTestReconciled';InitialStatePreserved=$initial;OwnConnectionAlreadyStopped=$ownStopped;Generation=$reply.Native.generation;PreferencesRestored=$true;NoStopCommandSent=$true;TasksAbsent=$true;RootAbsent=!(Test-Path -LiteralPath $r)}|ConvertTo-Json -Compress
'@
$result=Invoke-BoundedRm $code.Replace('__ROOT__',$e.Root).Replace('__ID__',$e.Run) 35
$result|Set-Content -LiteralPath (Join-Path $workspace ('runtime/native-gui-failed-reconciliation-'+$e.Run+'.json'))
Move-Item -LiteralPath $journal -Destination (Join-Path $workspace ('runtime/native-client-cycle-failed-reconciled-'+$e.Run+'.json'))
$result
