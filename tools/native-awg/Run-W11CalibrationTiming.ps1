param([ValidateRange(1,5)][int]$Runs=3)
$ErrorActionPreference='Stop';$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$samples=@()
foreach($round in 1..$Runs){
 $raw=& (Join-Path $root 'tools/native-guard-lab/Run-W11Direct.ps1') -Automatic
 $result=($raw|Where-Object {$_ -match '^\{'}|Select-Object -Last 1)|ConvertFrom-Json
 if(-not $result.success -or -not $result.hashesMatched -or -not $result.independentCleanupConfirmed -or -not $result.targetCalibrationTiming.matchedTarget -or -not $result.targetCalibrationTiming.sameCore){throw 'Incomplete target calibration timing'}
 $samples+=@{round=$round;timing=$result.targetCalibrationTiming;controlProbeMs=$result.firstControlProbeMs;controllerSha256=$result.controllerSha256}
}
$report=@{success=$true;scope='Exact observed domain/address/application tuple within one core lifetime; monotonic in-process stamps; no broad P95 or page-load claim';samples=$samples}
$report|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root 'runtime/native-calibration-timing.json') -Encoding UTF8
$report|ConvertTo-Json -Depth 6 -Compress
