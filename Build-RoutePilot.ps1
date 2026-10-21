$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Build-Dotnet.ps1')
$root = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $root 'runtime\.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
& (Join-Path $root 'Create-AppIcon.ps1')
$output=Join-Path $root 'runtime\app-build'
& (Get-BuildDotnet) publish (Join-Path $root 'app\RoutePilot.csproj') -c Release -o $output --self-contained false `
    -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false `
    -p:RestoreIgnoreFailedSources=true -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'RoutePilot build failed.' }
if(-not (Test-Path -LiteralPath (Join-Path $root 'RoutePilot.exe'))){Copy-Item -LiteralPath (Join-Path $output 'RoutePilot.exe') -Destination (Join-Path $root 'RoutePilot.exe')}
Write-Host 'Built runtime\app-build\RoutePilot.exe'
