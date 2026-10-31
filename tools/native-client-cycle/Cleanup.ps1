$ErrorActionPreference='Stop';$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
$journal=Join-Path $workspace 'runtime/native-client-cycle-active.json';$e=[IO.File]::ReadAllText($journal)|ConvertFrom-Json
if($e.Run -cnotmatch '^[a-f0-9]{32}$' -or $e.Root -cne ('C:\Program Files\RoutePilot\Agent\native-cycle-'+$e.Run)){throw 'Cycle cleanup scope differs.'}
$guiCleanupHash=''
if($e.Pins.'Gui.ps1'){
 $guiSource=Join-Path $PSScriptRoot 'Gui.ps1';$guiCleanupHash=(Get-FileHash -LiteralPath $guiSource).Hash
 Invoke-BoundedScp $guiSource ('citastation@192.168.1.124:'+($e.Root.Replace('\','/')+'/GuiCleanup.ps1'))
}
$code=@'
$ErrorActionPreference='Stop';$r='__ROOT__';$id='__ID__';$agent='C:\Program Files\RoutePilot\Agent'
$plan=[IO.File]::ReadAllText((Join-Path $r 'plan.json'))|ConvertFrom-Json
$guiRecord=Join-Path $r 'gui-connection.json'
if($plan.GuiMode -and (Test-Path -LiteralPath $guiRecord)){
 $g=Get-Content -LiteralPath $guiRecord -Raw|ConvertFrom-Json
 if($g.Run -cne $id -or $g.ConnectionId -cnotmatch '^[a-f0-9]{32}$' -or $g.ProfileSha -cne $plan.ProfileSha -or $g.InitialOperation -cne $plan.InitialOperation -or $g.InitialGeneration -ne $plan.InitialGeneration){throw 'GUI cleanup connection differs.'}
 $plan.ConnectionId=$g.ConnectionId
}
$proofs=@();foreach($role in @('primary','backup')){
 $p=[IO.File]::ReadAllText((Join-Path $r ('rescue-result-'+$role+'.json')))|ConvertFrom-Json
 if($p.run -cne $id -or $p.connectionId -cne $plan.ConnectionId -or $p.outcome -cnotin @('Stopped','NoSession')){throw 'Cycle rescue result is not verified.'};$proofs+=,$p
}
foreach($pin in $plan.FilesSource.PSObject.Properties){if((Get-FileHash -LiteralPath (Join-Path $r $pin.Name)).Hash -cne $pin.Value){throw 'Cycle source changed.'}}
. ([ScriptBlock]::Create([IO.File]::ReadAllText((Join-Path $r 'NativeUpgradeTransaction.ps1'))))
. ([ScriptBlock]::Create([IO.File]::ReadAllText((Join-Path $r 'NativeUpgradeService.ps1'))))
$lease=Open-NativeUpgradeLease $agent
try{
 $service=Get-NativeUpgradeServiceSnapshot $lease
 if($service.State -cne 'Running' -or (Get-NativeUpgradeHash (Join-Path $agent 'RoutePilot.Agent.exe')) -cne $plan.AgentSha){throw 'Cycle agent changed.'}
 Initialize-NativeUpgradePipe;$reply=[Cita.NativeUpgradePipe]::Exchange('{"Command":"native-status"}',$service.ProcessId)|ConvertFrom-Json
 if(!$reply.Success -or $reply.Native.desired -isnot [bool] -or $reply.Native.desired -or $reply.Native.phase -cne 'Disconnected'){throw 'Cycle routing cleanup unconfirmed.'}
}finally{$lease.Handle.Dispose()}
$names=@(('CitaNativeCycle-'+$id+'-Owner'),('CitaNativeCycle-'+$id+'-Rescue'),('CitaNativeCycle-'+$id+'-Rescue-Backup'))
if($plan.GuiMode){$names+=('CitaNativeCycle-'+$id+'-Gui')}
foreach($name in $names){Disable-ScheduledTask -TaskName $name|Out-Null}
if(@(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'"|Where-Object {$_.ProcessId -ne $PID -and $_.CommandLine -like ('*'+$r+'*')}).Count){throw 'A cycle process remains.'}
if($plan.GuiMode){
 $library=Join-Path $r 'GuiCleanup.ps1';if((Get-FileHash -LiteralPath $library).Hash -cne '__GUI_CLEANUP__'){throw 'GUI cleanup implementation differs.'}
 . ([ScriptBlock]::Create([IO.File]::ReadAllText($library)))
 if((Get-GuiCycleActors $plan).Count){throw 'GUI sender remains.'}
 Restore-GuiCyclePreferences $plan $r
}
foreach($name in $names){if((Get-ScheduledTask -TaskName $name).State -eq 'Running'){throw 'A cycle task remains running.'};Unregister-ScheduledTask -TaskName $name -Confirm:$false}
$result=@{Run=$id;State=$reply.Native.phase;Desired=$reply.Native.desired;Generation=$reply.Native.generation;Results=$proofs;RootAbsent=$true;TasksAbsent=$true;Processes=0}
if($plan.GuiMode){$result.GuiPreferences=Get-Content -LiteralPath (Join-Path $r 'gui-preferences-restored.json') -Raw|ConvertFrom-Json;$result.GuiProcesses=@(Get-GuiCycleActors $plan).Count;$result.GuiCleanupSha='__GUI_CLEANUP__'}
$children=@(Get-ChildItem -LiteralPath $r -Force)
if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($r)) -ine $agent -or [IO.Path]::GetFileName($r) -cne ('native-cycle-'+$id) -or ((Get-Item -LiteralPath $r -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Owned cleanup root differs.'}
foreach($f in $children){if($f.PSIsContainer -or ($f.Attributes -band [IO.FileAttributes]::ReparsePoint) -or [IO.Path]::GetDirectoryName($f.FullName) -ine $r){throw 'Unknown cleanup child.'}}
foreach($f in $children){Remove-Item -LiteralPath $f.FullName -Force};Remove-Item -LiteralPath $r
if(Test-Path -LiteralPath $r){throw 'Cycle root remains.'}
$result|ConvertTo-Json -Depth 5 -Compress
'@
$proof=Invoke-BoundedRm $code.Replace('__ROOT__',$e.Root).Replace('__ID__',$e.Run).Replace('__GUI_CLEANUP__',$guiCleanupHash) 30
$proof|Set-Content -LiteralPath (Join-Path $workspace ('runtime/native-client-cycle-cleanup-'+$e.Run+'.json'))
Move-Item -LiteralPath $journal -Destination (Join-Path $workspace ('runtime/native-client-cycle-completed-'+$e.Run+'.json'))
$proof
