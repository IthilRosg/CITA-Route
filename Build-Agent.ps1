$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Build-Dotnet.ps1')
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot 'runtime\.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
$testScratch = Join-Path $PSScriptRoot 'runtime\agent-selftest-scratch'
New-Item -ItemType Directory -Path $testScratch -Force | Out-Null
# Keep all self-test fixtures inside the checkout. This makes the suite
# reproducible under restricted runners where atomic replacement in the system
# temp directory is denied, and prevents test residue from touching user temp.
$env:CITA_TEST_SCRATCH_ROOT = $testScratch
$env:TEMP = $testScratch
$env:TMP = $testScratch
$output = Join-Path $PSScriptRoot 'runtime\agent-build'
& (Get-BuildDotnet) publish (Join-Path $PSScriptRoot 'agent\RoutePilot.Agent.csproj') -c Release -r win-x64 -o $output --self-contained false `
    -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:RestoreIgnoreFailedSources=true -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Agent build failed.' }
& (Join-Path $output 'RoutePilot.Agent.exe') --self-test
if ($LASTEXITCODE -ne 0) { throw 'Agent self-tests failed.' }
