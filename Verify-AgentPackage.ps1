$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'AgentPackage.ps1')
$fixture=Join-Path $PSScriptRoot ('runtime\package-test-'+[guid]::NewGuid().ToString('N'))
$installed=Join-Path $fixture 'Agent'
New-Item -ItemType Directory -Path $installed -Force | Out-Null
try {
    [IO.File]::WriteAllText((Join-Path $installed 'agent.txt'),'old')
    $source=Join-Path $fixture 'new.txt'; [IO.File]::WriteAllText($source,'new')
    $script:stops=0; $script:starts=0
    $stop={ $script:stops++ }
    $start={ $script:starts++; if($script:starts -eq 1){throw 'Injected service startup failure.'} }
    $failed=$false
    try { Publish-AgentPackage @(@{Source=$source;Name='agent.txt'},@{Source=(Join-Path $fixture 'missing');Name='missing'}) $installed $stop $start }
    catch { $failed=$true }
    if (-not $failed -or $script:stops -ne 0) { throw 'Missing package sources must not stop the rescue service.' }
    $failed=$false
    try { Publish-AgentPackage @(@{Source=$source;Name='agent.txt'}) $installed $stop $start }
    catch { $failed=$true }
    if (-not $failed -or $script:starts -ne 2 -or [IO.File]::ReadAllText((Join-Path $installed 'agent.txt')) -ne 'old') { throw 'A failed startup must restore and restart the old package.' }
    Publish-AgentPackage @(@{Source=$source;Name='agent.txt'}) $installed $stop { $script:starts++ }
    if ([IO.File]::ReadAllText((Join-Path $installed 'agent.txt')) -ne 'new') { throw 'Valid package did not replace the old version.' }
    Write-Output 'PASS: missing source preserves the running service; failed startup rolls back and restarts old files; successful promotion installs verified files.'
} finally {
    $prefix=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runtime\package-test-'))
    if (-not [IO.Path]::GetFullPath($fixture).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path.' }
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
