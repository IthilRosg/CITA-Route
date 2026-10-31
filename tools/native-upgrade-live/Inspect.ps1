$ErrorActionPreference='Stop';$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
$e=[IO.File]::ReadAllText((Join-Path $workspace 'runtime/native-upgrade-live-active.json'))|ConvertFrom-Json
if($e.Run -cnotmatch '^[a-f0-9]{32}$' -or $e.Root -cne ('C:\Program Files\RoutePilot\Agent\native-update-'+$e.Run)){throw 'Live run journal scope differs.'}
$script=@'
$ErrorActionPreference='Stop';$root='__ROOT__';$id='__ID__';$agent='C:\Program Files\RoutePilot\Agent'
$records=@{}
foreach($name in @('actor-failure.json','actor-complete.json','failure-controller.json','failure-primary.json','failure-backup.json','worker-failure-primary.json','worker-failure-backup.json','verified-primary.json','verified-backup.json','recovered-primary.json','recovered-backup.json')){if([IO.File]::Exists((Join-Path $root $name))){$records[$name]=[IO.File]::ReadAllText((Join-Path $root $name))|ConvertFrom-Json}}
$s=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'"
$tx=$null;if([IO.File]::Exists((Join-Path $agent 'native-upgrade.json'))){$j=[IO.File]::ReadAllText((Join-Path $agent 'native-upgrade.json'))|ConvertFrom-Json;$tx=@{Id=$j.id;Phase=$j.phase}}
$processes=@(Get-CimInstance Win32_Process -Filter "Name='Cita.NativeUpdater.exe' OR Name='powershell.exe'"|Where-Object {$_.ExecutablePath -ieq (Join-Path $root 'Cita.NativeUpdater.exe') -or ($_.Name -ieq 'powershell.exe' -and $_.CommandLine -like ('*'+$root+'*'))}|Select-Object ProcessId,Name)
$intent=$null;if([IO.File]::Exists((Join-Path $agent 'native\intent.json'))){$i=[IO.File]::ReadAllText((Join-Path $agent 'native\intent.json'))|ConvertFrom-Json;$intent=@{Desired=$i.Desired;Generation=$i.Generation;Operation=$i.Operation}}
[pscustomobject]@{Run=$id;Records=$records;Service=@{State=$s.State;Pid=$s.ProcessId;StartMode=$s.StartMode};Transaction=$tx;Marker=Test-Path (Join-Path $agent 'native-upgrade.pending');AgentSha=(Get-FileHash -LiteralPath (Join-Path $agent 'RoutePilot.Agent.exe')).Hash;Intent=$intent;Processes=$processes}|ConvertTo-Json -Depth 8 -Compress
'@
$result=Invoke-BoundedRm $script.Replace('__ROOT__',$e.Root).Replace('__ID__',$e.Run) 30
$result|Set-Content -LiteralPath (Join-Path $workspace ('runtime/native-upgrade-live-inspection-'+$e.Run+'.json'))
$result
