param([Parameter(Mandatory)][string]$RunRoot,[Parameter(Mandatory)][string]$Role)
$ErrorActionPreference='Stop'
$plan=[IO.File]::ReadAllText((Join-Path $RunRoot 'plan.json'))|ConvertFrom-Json
$before=[IO.File]::ReadAllText((Join-Path $RunRoot 'baseline.json'))|ConvertFrom-Json
$agent=Join-Path $env:ProgramFiles 'RoutePilot\Agent'
$s=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'"
if($s.State -cne 'Running' -or $s.ProcessId -ne $before.ServicePid -or $s.StartMode -cne $before.StartMode){throw 'The unmodified service changed.'}
foreach($row in @(@('RoutePilot.Agent.exe',$before.AgentSha),@('native\intent.json',$before.IntentSha))){if((Get-FileHash -LiteralPath (Join-Path $agent $row[0])).Hash -cne $row[1]){throw 'The unmodified protected input changed.'}}
if(Test-Path -LiteralPath (Join-Path $agent 'native-upgrade.pending')){throw 'Unexpected production maintenance marker.'}
$value=@{Run=$plan.Run;Success=$true}
[IO.File]::WriteAllText((Join-Path $RunRoot ('recovered-'+$Role+'.json')),($value|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
