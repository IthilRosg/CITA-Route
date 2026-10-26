param([ValidateRange(2,5)][int]$Pairs=5)
$ErrorActionPreference='Stop';$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$samples=@()
foreach($round in 1..$Pairs){foreach($variant in @('Before','After')){
 $folder=if($variant -eq 'Before'){'runtime/native-observation-before'}else{'runtime/native-agent-backend-lab'}
 $raw=& (Join-Path $root 'tools/native-guard-lab/Run-W11Direct.ps1') -Automatic -AgentFolder $folder
 $result=($raw|Where-Object {$_ -match '^\{'}|Select-Object -Last 1)|ConvertFrom-Json
 if(-not $result.success -or -not $result.hashesMatched -or -not $result.independentCleanupConfirmed){throw 'Incomplete observation latency sample'}
 $samples+=@{round=$round;variant=$variant;decisionWaitMs=$result.firstAutomaticDecisionWaitMs;readyFromProbeStartUpperMs=($result.firstControlProbeMs+$result.firstAutomaticDecisionWaitMs);initializeMs=$result.initializeMs;connectMs=$result.connectMs;controllerSha256=$result.controllerSha256}
 }}
$before=@($samples|Where-Object variant -eq 'Before'|ForEach-Object readyFromProbeStartUpperMs|Sort-Object)
$after=@($samples|Where-Object variant -eq 'After'|ForEach-Object readyFromProbeStartUpperMs|Sort-Object)
function Median($values){if($values.Count%2){return $values[[int][Math]::Floor($values.Count/2)]};return ($values[$values.Count/2-1]+$values[$values.Count/2])/2.0}
$report=@{success=$true;pairs=$Pairs;beforeMedianUpperMs=(Median $before);afterMedianUpperMs=(Median $after);scope='Paired single-domain VM control probes; uncontrolled OS cache, not page-load latency or a broad P95';samples=$samples}
$report|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root 'runtime/native-observation-speed.json') -Encoding UTF8;$report|ConvertTo-Json -Depth 5 -Compress
