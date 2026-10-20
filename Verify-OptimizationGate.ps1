$ErrorActionPreference='Stop'
$fixture=Join-Path $PSScriptRoot ('runtime\optimization-gate-'+[guid]::NewGuid().ToString('N'))
try{
    New-Item -ItemType Directory -Path (Join-Path $fixture 'logs'),(Join-Path $fixture 'runtime') -Force|Out-Null
    foreach($name in @('Verify-Optimization.ps1','FileStorage.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $fixture $name)}
    # A missing fixture executable must be visible to a calling PowerShell
    # workflow, not just set LASTEXITCODE before a later successful command.
    $caught=$false
    try{& (Join-Path $fixture 'Verify-Optimization.ps1')}catch{$caught=$true}
    $path=Join-Path $fixture 'logs\optimization-check-last.json'
    if(-not $caught -or -not(Test-Path -LiteralPath $path)){throw 'An optimization failure did not stop its caller or preserve its report.'}
    $report=[IO.File]::ReadAllText($path)|ConvertFrom-Json
    if($report.success -or -not $report.error){throw 'A failed optimization check was recorded as successful.'}
    Write-Output 'PASS: failed optimization validation stops the parent workflow and keeps a diagnostic report.'
}finally{
    $prefix=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runtime\optimization-gate-'))
    $absolute=[IO.Path]::GetFullPath($fixture)
    if(-not $absolute.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe gate fixture cleanup.'}
    if(Test-Path -LiteralPath $absolute){Remove-Item -LiteralPath $absolute -Recurse -Force}
}
