# Local preparation only. Does not connect to the station or control a service.
param([string]$OutputRoot=(Join-Path $PSScriptRoot '../../runtime/physical-service-cycle-candidate'))
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=[IO.Path]::GetFullPath($OutputRoot);$allowed=[IO.Path]::GetFullPath((Join-Path $workspace 'runtime'))
if(!$out.StartsWith($allowed+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Candidate output must remain under workspace runtime.'}
if(Test-Path -LiteralPath $out){throw 'Candidate output already exists.'}
$fixture=Join-Path $workspace 'runtime/physical-original-protected-agent.exe'
if((Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash -cne '4F1C0AF3A7FFF0DE5AEDD4645F590867EA24BDD43B3A51B8C8075EFF4F0A741E'){throw 'Original service fixture differs.'}
$contract=Join-Path $workspace 'runtime/installed-service-control-contract.json'
$c=Get-Content -LiteralPath $contract -Raw|ConvertFrom-Json
if($c.imageSha256 -cne '4F1C0AF3A7FFF0DE5AEDD4645F590867EA24BDD43B3A51B8C8075EFF4F0A741E'){throw 'Stop contract differs.'}
$dotnet=Join-Path $workspace 'runtime/dotnet10/dotnet.exe';$project=Join-Path $PSScriptRoot 'Cita.PhysicalServiceCycle.csproj'
& $dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $out
if($LASTEXITCODE -ne 0){throw 'Service cycle candidate build failed.'}
$helper=Join-Path $out 'Cita.PhysicalServiceCycle.exe';$digest=(Get-FileHash -LiteralPath $helper -Algorithm SHA256).Hash
[pscustomobject]@{helper=$helper;helperSha256=$digest;originalAgentSha256=$c.imageSha256;clientManifestSha256='8F8C2074F234C456A1C844B659AD5EB14A83B56371C8DB05E3B59FC08A9C6E97';scope='Local candidate only; no service stop or remote execution';readyForRemote=$false}|ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $out 'candidate.json') -Encoding UTF8
Get-Content -LiteralPath (Join-Path $out 'candidate.json')
