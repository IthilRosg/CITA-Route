$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dotnet=Join-Path $root 'runtime/dotnet10/dotnet.exe'
function Inputs {
 $files=@(Get-ChildItem -LiteralPath (Join-Path $root 'agent') -Recurse -File -Filter '*.cs' | Where-Object {$_.FullName -notmatch '[\\/](bin|obj)[\\/]'} | ForEach-Object {$_.FullName})
 $files+=@((Join-Path $root 'agent/RoutePilot.Agent.csproj'),(Join-Path $PSScriptRoot 'Program.cs'),(Join-Path $PSScriptRoot 'NativeControlProbe.csproj'))
 @($files|Sort-Object|ForEach-Object {@{path=$_.Substring($root.Length+1);sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})
}
$before=Inputs
$agentDir=Join-Path $root 'runtime/goal2-preflight-agent-build'
$probeDir=Join-Path $root 'runtime/control-probe-publish'
& $dotnet build (Join-Path $root 'agent/RoutePilot.Agent.csproj') -c Release -o $agentDir *> (Join-Path $root 'runtime/goal2-physical-agent-build.log')
if($LASTEXITCODE){throw 'Agent build failed'}
& $dotnet publish (Join-Path $PSScriptRoot 'NativeControlProbe.csproj') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $probeDir *> (Join-Path $root 'runtime/goal2-physical-probe-build.log')
if($LASTEXITCODE){throw 'Probe build failed'}
$agent=Join-Path $agentDir 'RoutePilot.Agent.dll';$probe=Join-Path $probeDir 'NativeControlProbe.exe'
$agentHash=(Get-FileHash -LiteralPath $agent).Hash;$probeHash=(Get-FileHash -LiteralPath $probe).Hash
$output=@(& $probe $agent);if($LASTEXITCODE){throw 'Local probe failed'}
$expected=@('bad-policy','late-policy','rejected','bad-scope','bad-retirement','retirement-pending','bad-owner','bad-generation','bad-revision','missing-digest','bad-telemetry','concurrent','cancel-after-write','cancel-before-write','ok','calibration','calibration-lock','connector-ownership','zapret-authorization','connector-admission','connector-authorization-race','connector-replace','zapret-policy-gate','zapret-probe','zapret-manifest','policy-staging','atomic-process','core-recovery-failure')
$seen=@($output|ForEach-Object {if($_ -match '^([a-z-]+): PASS$'){$Matches[1]}})
if($seen.Count -ne $expected.Count -or @(Compare-Object ($seen|Sort-Object) ($expected|Sort-Object)).Count){throw 'Local probe coverage differs'}
$after=Inputs
if(($before|ConvertTo-Json -Depth 6 -Compress) -cne ($after|ConvertTo-Json -Depth 6 -Compress) -or (Get-FileHash $agent).Hash -ne $agentHash -or (Get-FileHash $probe).Hash -ne $probeHash){throw 'Build inputs or outputs changed'}
$receipt=@{agentSha256=$agentHash;probeSha256=$probeHash;dotnetSha256=(Get-FileHash $dotnet).Hash;inputs=$after;tests=$seen;builtUtc=[DateTime]::UtcNow.ToString('o');scope='local build and stdio probe; SDK inputs beyond listed files not attested'}
$receipt|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $root 'runtime/goal2-control-build-receipt.json') -Encoding UTF8
"Build and 28 local scenario groups passed"
