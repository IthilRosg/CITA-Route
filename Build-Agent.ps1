$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot 'runtime\.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
$output = Join-Path $PSScriptRoot 'runtime\agent-build'
& (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') publish (Join-Path $PSScriptRoot 'agent\RoutePilot.Agent.csproj') -c Release -r win-x64 -o $output --self-contained false `
    -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:RestoreIgnoreFailedSources=true -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Agent build failed.' }
& (Join-Path $output 'RoutePilot.Agent.exe') --self-test
if ($LASTEXITCODE -ne 0) { throw 'Agent self-tests failed.' }
