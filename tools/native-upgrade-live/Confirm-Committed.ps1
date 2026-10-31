param([switch]$Cleanup,[ValidateSet('Committed','RolledBack')][string]$ExpectedPhase='Committed')
$ErrorActionPreference='Stop';$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
$journal=Join-Path $workspace 'runtime/native-upgrade-live-active.json';$e=[IO.File]::ReadAllText($journal)|ConvertFrom-Json
if($e.Run -cnotmatch '^[a-f0-9]{32}$' -or $e.Root -cne ('C:\Program Files\RoutePilot\Agent\native-update-'+$e.Run)){throw 'Live scope differs.'}
$code=@'
$ErrorActionPreference='Stop';$r='__ROOT__';$id='__ID__';$agent='C:\Program Files\RoutePilot\Agent'
$plan=[IO.File]::ReadAllText((Join-Path $r 'plan.json'))|ConvertFrom-Json
foreach($p in $plan.Files.PSObject.Properties){if((Get-FileHash -LiteralPath (Join-Path $r $p.Name)).Hash -cne $p.Value){throw 'Frozen update inputs differ.'}}
. ([ScriptBlock]::Create([IO.File]::ReadAllText((Join-Path $r 'NativeUpgradeTransaction.ps1'))))
. ([ScriptBlock]::Create([IO.File]::ReadAllText((Join-Path $r 'NativeUpgradeService.ps1'))))
$lease=Open-NativeUpgradeLease $agent
try{
 $j=Read-NativeUpgradeJournal $lease
 if($j.id -cne $id -or $j.phase -cne '__PHASE__' -or (Test-Path (Join-Path $agent 'native-upgrade.pending'))){throw 'Live transaction terminal phase differs.'}
 $before=[IO.File]::ReadAllText((Join-Path $r 'upgrade-input.json'))|ConvertFrom-Json
 $actual=Get-NativeUpgradeInventory $agent
 $expectedAgent=if($j.phase -ceq 'Committed'){$j.newAgentSha}else{$j.oldAgentSha};$expectedNative=if($j.phase -ceq 'Committed'){$j.newNative}else{$j.oldNative}
 if((Get-NativeUpgradeHash (Join-Path $agent 'RoutePilot.Agent.exe')) -cne $expectedAgent -or (ConvertTo-Json -InputObject @($actual) -Depth 6 -Compress) -cne (ConvertTo-Json -InputObject @($expectedNative) -Depth 6 -Compress)){throw 'Terminal pair differs.'}
 if($j.phase -ceq 'Committed'){
  $proof=[IO.File]::ReadAllText((Join-Path $r 'started-service.json'))|ConvertFrom-Json
  $reply=Invoke-NativeUpgradeServiceRpc $lease $proof 'native-status' $id
 }else{
  # Independent read-only observation. Launch provenance was verified inside
  # the pinned recovery worker; older worker builds did not persist its proof.
  foreach($role in @('primary','backup')){$v=[IO.File]::ReadAllText((Join-Path $r ('verified-'+$role+'.json')))|ConvertFrom-Json;if($v.Run -cne $id -or !$v.Success -or $v.ActorProcesses -ne 0 -or $v.RecoveryProcesses -ne 0){throw 'Recovery witness missing.'}}
  $s=Get-NativeUpgradeServiceSnapshot $lease;$p=Get-Process -Id $s.ProcessId -ErrorAction Stop;$null=$p.Handle
  try{if($s.State -cne 'Running' -or $p.HasExited -or $p.Path -ine (Join-Path $agent 'RoutePilot.Agent.exe')){throw 'Restored service observation differs.'};Initialize-NativeUpgradePipe;$reply=[Cita.NativeUpgradePipe]::Exchange('{"Command":"native-status"}',$s.ProcessId)|ConvertFrom-Json;if($p.HasExited -or (Get-NativeUpgradeServiceSnapshot $lease).ProcessId -ne $s.ProcessId){throw 'Restored service changed during observation.'}}finally{$p.Dispose()}
 }
 Assert-NativeUpgradeReadyReply $reply $before.Operation $before.Generation $before.ExpectedPhase
 $state=Get-NativeUpgradeServiceSnapshot $lease
 if($state.StartType -cne $before.OriginalStartType){throw 'Service startup was not restored.'}
 $catalog=[IO.File]::ReadAllText((Join-Path $agent 'native\native-catalog.json'))|ConvertFrom-Json
 $profile=[string]$catalog.Profile.Name
 if(!$profile){throw 'Configured profile missing.'}
 $old=@($before.OldNative|Where-Object {$_.path -ceq $profile})
 if($old.Count -ne 1 -or (Get-NativeUpgradeHash (Join-Path (Join-Path $agent 'native') $profile)) -cne $old[0].sha256){throw 'Configured profile bytes changed.'}
 foreach($role in @('primary','backup')){$v=[IO.File]::ReadAllText((Join-Path $r ('verified-'+$role+'.json')))|ConvertFrom-Json;if($v.Run -cne $id -or !$v.Success -or $v.ActorProcesses -ne 0 -or $v.RecoveryProcesses -ne 0){throw 'Independent completion witness invalid.'}}
 $processes=@(Get-CimInstance Win32_Process -Filter "Name='Cita.NativeUpdater.exe' OR Name='powershell.exe'"|Where-Object {$_.ExecutablePath -ieq (Join-Path $r 'Cita.NativeUpdater.exe') -or ($_.Name -ieq 'powershell.exe' -and $_.CommandLine -like ('*'+$r+'*'))})
 if($processes.Count){throw 'A temporary worker remains.'}
 [pscustomobject]@{Run=$id;Phase=$j.phase;AgentSha=$expectedAgent;NativeFiles=@($actual).Count;ProfilePreserved=$true;Desired=$reply.Native.desired;OperationPreserved=$true;Generation=$reply.Native.generation;ServicePid=$state.ProcessId;Startup=$state.StartType;ActorProcesses=0;RecoveryProcesses=0;GuardSha=(Get-NativeUpgradeHash (Join-Path $agent 'native\native-guard.exe'))}|ConvertTo-Json -Compress
}finally{$lease.Handle.Dispose()}
'@
$proof=(Invoke-BoundedRm $code.Replace('__ROOT__',$e.Root).Replace('__ID__',$e.Run).Replace('__PHASE__',$ExpectedPhase) 30)|ConvertFrom-Json
$proof|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $workspace ('runtime/native-upgrade-live-committed-'+$e.Run+'.json'))
if(!$Cleanup){$proof;return}
$clean=@'
$ErrorActionPreference='Stop';$r='__ROOT__';$id='__ID__';$task='CitaNativeUpgrade-'+$id
if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($r)) -ine 'C:\Program Files\RoutePilot\Agent' -or [IO.Path]::GetFileName($r) -cne ('native-update-'+$id)){throw 'Cleanup root escaped scope.'}
$names=@($task,($task+'-Backup'),($task+'-Boot'),($task+'-Controller'))
foreach($name in $names){Disable-ScheduledTask -TaskName $name|Out-Null}
if(@(Get-CimInstance Win32_Process -Filter "Name='Cita.NativeUpdater.exe' OR Name='powershell.exe'"|Where-Object {$_.ExecutablePath -ieq (Join-Path $r 'Cita.NativeUpdater.exe') -or ($_.Name -ieq 'powershell.exe' -and $_.CommandLine -like ('*'+$r+'*'))}).Count){throw 'A temporary process remains.'}
foreach($name in $names){if((Get-ScheduledTask -TaskName $name).State -eq 'Running'){throw 'Task still running.'};Unregister-ScheduledTask -TaskName $name -Confirm:$false}
$files=@(Get-ChildItem -LiteralPath $r -Force)
if((Get-Item -LiteralPath $r -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Root linked.'}
foreach($f in $files){if($f.PSIsContainer -or ($f.Attributes -band [IO.FileAttributes]::ReparsePoint) -or [IO.Path]::GetDirectoryName($f.FullName) -ine $r){throw 'Unexpected owned child.'}}
foreach($f in $files){Remove-Item -LiteralPath $f.FullName -Force};Remove-Item -LiteralPath $r
foreach($suffix in @('.log','.log.err')){$p=Join-Path $env:TEMP ('cita-native-start-'+$id+$suffix);if([IO.File]::Exists($p)){if((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Log linked.'};Remove-Item -LiteralPath $p -Force}}
if(Test-Path -LiteralPath $r){throw 'Root remains.'}
[pscustomobject]@{Run=$id;RootAbsent=$true;TasksAbsent=$true;Processes=0;PreviousPairRetained=$true}|ConvertTo-Json -Compress
'@
$absence=(Invoke-BoundedRm $clean.Replace('__ROOT__',$e.Root).Replace('__ID__',$e.Run) 30)|ConvertFrom-Json
$absence|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $workspace ('runtime/native-upgrade-live-cleanup-'+$e.Run+'.json'))
Move-Item -LiteralPath $journal -Destination (Join-Path $workspace ('runtime/native-upgrade-live-completed-'+$e.Run+'.json'))
$proof;$absence
