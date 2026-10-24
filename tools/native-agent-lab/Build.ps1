$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$env:DOTNET_CLI_HOME=Join-Path $root 'runtime/dotnet-cli-home';$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1';$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
. (Join-Path $root 'Build-Dotnet.ps1');$dotnet=Get-BuildDotnet
& $dotnet build (Join-Path $PSScriptRoot 'NativeAgentLab.csproj') -c Release -o (Join-Path $root 'runtime/native-agent-backend-lab') --nologo
if($LASTEXITCODE -ne 0){throw 'Native agent candidate build failed'}
& $dotnet (Join-Path $root 'runtime/native-agent-backend-lab/Cita.NativeAgentLab.dll') --self-test
if($LASTEXITCODE -ne 0){throw 'Native agent candidate scope checks failed'}
& $dotnet build (Join-Path $root 'agent/RoutePilot.Agent.csproj') -c Release -o (Join-Path $root 'runtime/native-agent-backend-compile') --nologo
if($LASTEXITCODE -ne 0){throw 'Shared backend agent compilation failed'}
