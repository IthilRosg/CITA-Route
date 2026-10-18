$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Build-Agent.ps1')
& (Join-Path $PSScriptRoot 'Build-RoutePilot.ps1')
