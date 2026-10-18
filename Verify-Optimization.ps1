param([switch]$Live)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$agent=Join-Path $root 'runtime\agent-build\RoutePilot.Agent.exe'
. (Join-Path $root 'FileStorage.ps1')
$folder=Join-Path $root ('runtime\optimization-check-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder | Out-Null
$report=@{success=$false;startedUtc=[DateTime]::UtcNow.ToString('o');stages=@();folder=$folder}
try{
    foreach($check in @('self-test','optimization-integration')){
        $timer=[Diagnostics.Stopwatch]::StartNew()
        if($check -eq 'self-test'){& $agent --self-test *> (Join-Path $folder ($check+'.log'))}
        else{& $agent --optimization-integration $root *> (Join-Path $folder ($check+'.log'))}
        if($LASTEXITCODE -ne 0){throw ('Optimization check failed: '+$check+'; inspect '+$folder)}
        $report.stages+=@{name=$check;milliseconds=$timer.ElapsedMilliseconds}
    }
    if($Live){
        $state=Read-JsonFile (Join-Path $root 'runtime\session.json')
        if(-not $state.running -or $state.recovering){throw 'The optional live check requires an already running ordinary session.'}
        & $agent --optimization-live $root *> (Join-Path $folder 'live.log')
        if($LASTEXITCODE -ne 0){throw ('Live diagnostic failed; inspect '+$folder)}
        $report.stages+=@{name='live-HEAD-and-DNS';success=$true}
    }
    $report.success=$true
}catch{$report.error=$_.Exception.Message}
finally{$report.finishedUtc=[DateTime]::UtcNow.ToString('o');Write-JsonFile (Join-Path $folder 'report.json') $report;Write-JsonFile (Join-Path $root 'logs\optimization-check-last.json') $report}
if(-not $report.success){exit 1}
Write-Output 'PASS: optimization checks. No system routes, VPNUS or services were changed.'
