param([switch]$Apply)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot;$runtime=Join-Path $root 'runtime'
. (Join-Path $root 'FileStorage.ps1')
. (Join-Path $root 'AgentLease.ps1')
. (Join-Path $root 'SessionRecovery.ps1')
Assert-NoAgentRun
$session=Read-JsonFile (Join-Path $runtime 'session.json')
$policy=Read-JsonFile (Join-Path $runtime 'policy-state.json')
if(-not $session.running -or $session.recovering -or -not $policy.epoch.StartsWith($session.id+':') -or $policy.phase -notin @('Auto','Lists')){throw 'An active, stable ordinary session is required.'}
$source=Join-Path $root 'runtime\agent-build\RoutePilot.Agent.exe'
$target=Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe'
if(-not(Test-Path -LiteralPath $source)){throw 'Build the controller package first.'}
$preferences=Read-JsonFile (Join-Path $root 'data\routing-policy.json')
if(($policy.phase -eq 'Auto') -ne ($preferences.Mode -eq 'Auto')){throw 'Pending mode changes require a normal reconnect, not a controller reload.'}
if(-not $Apply){@{ready=$true;sessionId=$session.id;networkRestart=$false;vpnusChanged=$false}|ConvertTo-Json -Compress;exit}
$admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if(-not $admin){
    $command="& '"+$PSCommandPath.Replace("'","''")+"' -Apply"
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $child=Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded)
    $child.WaitForExit();exit $child.ExitCode
}
$lock=[IO.File]::Open((Join-Path $runtime 'upgrade.lock'),'OpenOrCreate','ReadWrite','None')
$controlLock=[IO.File]::Open((Join-Path $runtime 'network-control.lock'),'OpenOrCreate','ReadWrite','None')
$id=[guid]::NewGuid().ToString('N');$agentFolder=Split-Path $target
$backup=Join-Path $agentFolder ('policy-before-'+$id+'.exe')
$staged=Join-Path $agentFolder ('policy-stage-'+$id+'.exe')
$report=@{success=$false;restored=$false;sessionId=$session.id;routerPid=$session.routerPid;bridgePid=$session.bridgePid;startedUtc=[DateTime]::UtcNow.ToString('o');networkRestart=$false;backup=$backup}
$replaced=$false;$quiesced=$false;$watchRestored=$false;$requestedStop=$false
function Stop-Watch {
    $pidFile=Join-Path $runtime 'watch.pid'
    if(Test-Path -LiteralPath $pidFile){
        $watchId=[int][IO.File]::ReadAllText($pidFile).Trim()
        $watch=Get-CimInstance Win32_Process -Filter ('ProcessId='+$watchId) -ErrorAction SilentlyContinue
        if($watch -and $watchId -ne $PID -and $watch.CommandLine -like ('*'+$root+'*') -and $watch.CommandLine -match '-Action\s+Watch'){Stop-Process -Id $watchId -Force -ErrorAction SilentlyContinue}
    }
}
function Stop-Worker {
    $identity=Read-JsonFile (Join-Path $runtime 'policy-process.json')
    $worker=Get-Process -Id $identity.Pid -ErrorAction SilentlyContinue
    if($worker -and $worker.Path -eq $target -and [Math]::Abs(($worker.StartTime.ToUniversalTime()-([DateTime]$identity.StartedUtc).ToUniversalTime()).TotalMilliseconds) -lt 100){$worker | Stop-Process -Force;$worker.WaitForExit(5000) | Out-Null}
}
function Start-Watch {
    Start-Process powershell.exe -WindowStyle Hidden -WorkingDirectory $root -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+(Join-Path $root 'SmartRouting.ps1')+'"'),'-Action','Watch') -RedirectStandardOutput (Join-Path $root 'logs\watch-output.log') -RedirectStandardError (Join-Path $root 'logs\watch-error.log') | Out-Null
    $script:watchRestored=$true
}
function Start-And-Check {
    $started=[DateTime]::UtcNow
    Start-Service RoutePilotRescue
    (Get-Service RoutePilotRescue).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
    if(Test-Path -LiteralPath (Join-Path $runtime 'stop.request')){throw 'Reload superseded by Stop.'}
    Start-PolicyController
    $ready=$false;$deadline=[DateTime]::UtcNow.AddSeconds(25)
    do{
        Start-Sleep -Milliseconds 150
        if(Test-Path -LiteralPath (Join-Path $runtime 'stop.request')){throw 'Reload superseded by Stop.'}
        $state=Read-JsonFile (Join-Path $runtime 'policy-state.json')
        $ready=$state.epoch -eq $policy.epoch -and $state.phase -in @('Auto','Lists') -and ([DateTime]$state.updatedUtc).ToUniversalTime() -ge $started
    }while(-not $ready -and [DateTime]::UtcNow -lt $deadline)
    if(-not $ready){throw 'The reloaded controller did not confirm its rules.'}
    $after=Read-JsonFile (Join-Path $runtime 'session.json')
    if($after.id -ne $session.id -or $after.routerPid -ne $session.routerPid -or $after.bridgePid -ne $session.bridgePid){throw 'The network session unexpectedly changed.'}
    $health=& $target --session-health | ConvertFrom-Json
    if(-not $health.healthy){throw 'HTTPS data path did not pass after controller reload.'}
    Start-Watch
}
try{
    Copy-Item -LiteralPath $source -Destination $staged
    if((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $staged).Hash){throw 'Staged controller hash mismatch.'}
    Copy-Item -LiteralPath $target -Destination $backup
    @(Get-Process RoutePilot -ErrorAction SilentlyContinue | Where-Object Path -eq (Join-Path $root 'RoutePilot.exe')) | Stop-Process -Force
    $quiesced=$true
    Stop-Watch
    Stop-Worker
    $latest=Read-JsonFile (Join-Path $runtime 'session.json')
    if($latest.id -ne $session.id -or -not $latest.running -or (Test-Path -LiteralPath (Join-Path $runtime 'stop.request'))){throw 'Stop or another session superseded this reload.'}
    $latestPolicy=Read-JsonFile (Join-Path $runtime 'policy-state.json')
    $options=Read-JsonFile (Join-Path $root 'data\routing-policy.json')
    $clear='';if(Test-Path -LiteralPath (Join-Path $runtime 'clear-policy.request')){$clear=[IO.File]::ReadAllText((Join-Path $runtime 'clear-policy.request')).Trim()}
    $seed=@($latestPolicy.decisions)
    $cacheFile=Join-Path $root 'data\policy-cache.json'
    if(Test-Path -LiteralPath $cacheFile){$cache=Read-JsonFile $cacheFile;if($cache.ClearRequest -ne $clear){$seed=@()}}
    $clearFile=Join-Path $runtime 'clear-policy.request'
    if((Test-Path -LiteralPath $clearFile) -and (Get-Item -LiteralPath $clearFile).LastWriteTimeUtc -gt ([DateTime]$policy.updatedUtc).ToUniversalTime()){$seed=@()}
    Write-JsonFile (Join-Path $runtime 'policy-active.json') @{Epoch=$policy.epoch;Mode=$options.Mode;AllowListedDpi=[bool]$options.AllowListedDpi;ClearRequest=$clear;Decisions=$seed}
    Stop-Service RoutePilotRescue
    (Get-Service RoutePilotRescue).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))
    $deadline=[DateTime]::UtcNow.AddSeconds(12)
    do{$busy=@(Get-Process RoutePilot.Agent -ErrorAction SilentlyContinue | Where-Object Path -eq $target);if($busy.Count){Start-Sleep -Milliseconds 100}}while($busy.Count -and [DateTime]::UtcNow -lt $deadline)
    if($busy.Count){throw 'An independent agent check still holds the image; controller replacement was cancelled.'}
    [IO.File]::Replace($staged,$target,[NullString]::Value);$replaced=$true
    if((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $source).Hash){throw 'Installed controller hash mismatch.'}
    Start-And-Check
    Copy-Item -LiteralPath (Join-Path $root 'runtime\app-build\RoutePilot.exe') -Destination (Join-Path $root 'RoutePilot.exe') -Force
    $report.success=$true;$report.healthy=$true
}catch{
    $report.error=$_.Exception.Message
    $requestedStop=Test-Path -LiteralPath (Join-Path $runtime 'stop.request')
    if($requestedStop){Start-Service RoutePilotRescue -ErrorAction SilentlyContinue;$report.cancelledByStop=$true}
    elseif($quiesced){try{
        Stop-Watch;Stop-Worker
        if($replaced){Stop-Service RoutePilotRescue -ErrorAction SilentlyContinue;Copy-Item -LiteralPath $backup -Destination $staged;[IO.File]::Replace($staged,$target,[NullString]::Value)}
        Start-And-Check;$report.restored=$true
    }catch{$report.rollbackError=$_.Exception.Message}}
}finally{
    if(Test-Path -LiteralPath $staged){Remove-Item -LiteralPath $staged -Force}
    $controlLock.Dispose();$lock.Dispose()
    if($requestedStop){& (Join-Path $root 'SmartRouting.ps1') -Action Stop *> (Join-Path $root 'logs\policy-upgrade-stop.log')}
    elseif($quiesced -and -not $watchRestored){Start-Watch}
    $report.finishedUtc=[DateTime]::UtcNow.ToString('o');Write-JsonFile (Join-Path $root 'logs\policy-upgrade-last.json') $report
}
if(-not $report.success){exit 1}
