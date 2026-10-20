param([switch]$Native)
$ErrorActionPreference='Stop'
$env:DOTNET_CLI_HOME=Join-Path $PSScriptRoot 'runtime\.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$output=Join-Path $PSScriptRoot 'runtime\transport-lab-build'
& (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') publish (Join-Path $PSScriptRoot 'tools\transport-lab\Cita.TransportLab.csproj') -c Release -r win-x64 -o $output --self-contained false -p:RestoreIgnoreFailedSources=true -p:NuGetAudit=false
if($LASTEXITCODE -ne 0){throw 'Transport lab build failed.'}
$lab=Join-Path $output 'Cita.TransportLab.exe'
& $lab --self-test
if($LASTEXITCODE -ne 0){throw 'Transport lab self-test failed.'}
if($Native){
    & $lab --integration
    if($LASTEXITCODE -ne 0){throw 'Native transport boundary fixture failed. See the fixture receipt and cleanup status.'}
}
