$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'AgentPackage.ps1')
$fixture=Join-Path $PSScriptRoot ('runtime\package-test-'+[guid]::NewGuid().ToString('N'))
$installed=Join-Path $fixture 'Agent'
New-Item -ItemType Directory -Path $installed -Force | Out-Null
try {
    $lockPath=Join-Path $fixture 'setup.lock'
    $original=[IO.File]::Open($lockPath,'OpenOrCreate','ReadWrite','None');$retained=$null
    try{
        $retained=Receive-SetupReservation $lockPath $PID $original.SafeFileHandle.DangerousGetHandle().ToInt64()
        $original.Dispose()
        $blocked=$false;try{$competing=[IO.File]::Open($lockPath,'Open','ReadWrite','None');$competing.Dispose()}catch [IO.IOException]{$blocked=$true}
        if(-not $blocked){throw 'The installer lost its setup reservation when the source owner closed its handle.'}
    }finally{$original.Dispose();if($retained){$retained.Dispose()}}
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
    # Snapshot preparation and shared state belong to the package transaction.
    $state=Join-Path $fixture 'startup-state.json';[IO.File]::WriteAllText($state,'manual-stop')
    $script:restores=0;$script:starts=0
    $failed=$false
    try{Publish-AgentPackage @(@{Source=$source;Name='agent.txt'}) $installed $stop { $script:starts++ } -Prepare {
        [IO.File]::WriteAllText($state,'new-snapshot');throw 'Injected snapshot preparation failure.'
    } -Restore { $script:restores++;[IO.File]::WriteAllText($state,'manual-stop') }}catch{$failed=$true}
    if(-not $failed -or $script:restores -ne 1 -or $script:starts -ne 1 -or [IO.File]::ReadAllText($state) -ne 'manual-stop'){throw 'Failed snapshot preparation did not restore shared state before restarting the prior package.'}
    $failed=$false;$script:starts=0
    try{Publish-AgentPackage @(@{Source=$source;Name='agent.txt'}) $installed $stop {
        $script:starts++;if($script:starts -eq 1){throw 'Injected service failure after snapshot commit.'}
        if([IO.File]::ReadAllText($state) -ne 'manual-stop'){throw 'Old service restarted before state rollback.'}
    } -Prepare {[IO.File]::WriteAllText($state,'new-snapshot')} -Restore {[IO.File]::WriteAllText($state,'manual-stop')}}catch{$failed=$true}
    if(-not $failed -or $script:starts -ne 2 -or [IO.File]::ReadAllText($state) -ne 'manual-stop'){throw 'Failed activation did not roll back the committed snapshot state.'}
    $empty=Join-Path $fixture 'first-install';New-Item -ItemType Directory -Path $empty|Out-Null
    $script:starts=0;$script:restores=0;$failed=$false
    try{Publish-AgentPackage @(@{Source=$source;Name='agent.txt'}) $empty $stop {$script:starts++;throw 'Injected first-install activation failure.'} -Restore {$script:restores++}}catch{$failed=$true}
    if(-not $failed -or $script:starts -ne 1 -or $script:restores -ne 1 -or (Test-Path -LiteralPath (Join-Path $empty 'agent.txt'))){throw 'A failed first installation attempted to restart a nonexistent previous executable.'}
    Write-Output 'PASS: missing source preserves the running service; failed startup rolls back and restarts old files; successful promotion installs verified files.'
} finally {
    $prefix=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runtime\package-test-'))
    if (-not [IO.Path]::GetFullPath($fixture).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path.' }
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
