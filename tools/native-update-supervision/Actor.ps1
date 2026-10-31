param([Parameter(Mandatory)][string]$RunRoot,[Parameter(Mandatory)][string]$Role)
$ErrorActionPreference='Stop'
$plan=[IO.File]::ReadAllText((Join-Path $RunRoot 'plan.json'))|ConvertFrom-Json
# No service, route or native-file mutation: exercise only process supervision.
[IO.File]::WriteAllText((Join-Path $RunRoot 'fixture-ready.json'),(@{Run=$plan.Run;Process=$PID}|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
while($true){Start-Sleep -Milliseconds 200}
