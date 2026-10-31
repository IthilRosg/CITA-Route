$ErrorActionPreference='Stop';$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
$e=[IO.File]::ReadAllText((Join-Path $workspace 'runtime/native-client-cycle-active.json'))|ConvertFrom-Json
if($e.Run -cnotmatch '^[a-f0-9]{32}$' -or $e.Root -cne ('C:\Program Files\RoutePilot\Agent\native-cycle-'+$e.Run)){throw 'Cycle journal scope differs.'}
$code=@'
$ErrorActionPreference='Stop';$r='__ROOT__';$id='__ID__';$out=@{}
foreach($name in @('state.json','connected-result.json','controller-failure.json','rescue-result-primary.json','rescue-result-backup.json','rescue-armed-primary.json','rescue-armed-backup.json','gui-process.json','gui-connection.json','gui-startup-observation.json','fault-checkpoint.json')){if([IO.File]::Exists((Join-Path $r $name))){$out[$name]=[IO.File]::ReadAllText((Join-Path $r $name))|ConvertFrom-Json}}
$actors=@(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'"|Where-Object {$_.ProcessId -ne $PID -and $_.CommandLine -like ('*'+$r+'*')}|Select-Object ProcessId,Name)
$intent=[IO.File]::ReadAllText('C:\Program Files\RoutePilot\Agent\native\intent.json')|ConvertFrom-Json
[pscustomobject]@{Run=$id;Records=$out;Intent=@{Desired=$intent.Desired;Generation=$intent.Generation;ConnectionId=$intent.ConnectionId};Processes=$actors}|ConvertTo-Json -Depth 7 -Compress
'@
$value=Invoke-BoundedRm $code.Replace('__ROOT__',$e.Root).Replace('__ID__',$e.Run) 30;$value|Set-Content -LiteralPath (Join-Path $workspace ('runtime/native-client-cycle-inspection-'+$e.Run+'.json'));$value
