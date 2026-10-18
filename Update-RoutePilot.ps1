param([ValidateSet('Check','Apply')][string]$Action='Check')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$version=([IO.File]::ReadAllText((Join-Path $root 'product.json')) | ConvertFrom-Json).version
. (Join-Path $root 'FileStorage.ps1')
. (Join-Path $root 'AgentLease.ps1')
. (Join-Path $root 'AgentPackage.ps1')
Assert-NoAgentRun
$agentBuild=Join-Path $root 'runtime\agent-build\RoutePilot.Agent.exe'
$appBuild=Join-Path $root 'runtime\app-build\RoutePilot.exe'
$install=Join-Path $env:ProgramFiles 'RoutePilot\Agent'
foreach($path in @($agentBuild,$appBuild)){
    if(-not(Test-Path -LiteralPath $path) -or [version][Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion -ne [version]($version+'.0')){throw 'Build both current-version packages before updating.'}
}
$before=Read-JsonFile (Join-Path $root 'runtime\session.json')
if($before.recovering){throw 'Wait for the current recovery before updating.'}
$journal=Read-JsonFile (Join-Path $env:ProgramData 'RoutePilot\run.json')
if($journal.Phase -in @('Armed','Testing','Restoring','RecoveryFailed')){throw 'An autonomous run owns the network.'}
if($Action -eq 'Check'){
    @{ready=$true;version=$version;running=$before.running;mode=$before.mode;willResume=[bool]$before.running;vpnusChanged=$false;rollback='Protected previous package plus previous GUI'} | ConvertTo-Json -Compress
    exit
}
$admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if(-not $admin){
    $command="& '"+$PSCommandPath.Replace("'","''")+"' -Action Apply"
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $child=Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded)
    $child.WaitForExit();exit $child.ExitCode
}
$id=[guid]::NewGuid().ToString('N')
$folder=Join-Path $root ('runtime\upgrade-'+$id)
New-Item -ItemType Directory -Path $folder | Out-Null
$lock=[IO.File]::Open((Join-Path $root 'runtime\upgrade.lock'),'OpenOrCreate','ReadWrite','None')
$backup=Join-Path (Split-Path $install) ('rollback-'+$id)
$allowed=[IO.Path]::GetFullPath((Split-Path $install)).TrimEnd('\')+'\rollback-'
if(-not [IO.Path]::GetFullPath($backup).StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid protected backup path.'}
$report=@{id=$id;startedUtc=[DateTime]::UtcNow.ToString('o');success=$false;restored=$false;oldSessionId=$before.id;wasRunning=[bool]$before.running;mode=$before.mode;stages=@();backup=$backup}
$stopped=$false;$installed=$false;$packageTouched=$false;$stopToken=$null
function Save-Report { Write-JsonFile (Join-Path $folder 'report.json') $script:report;Write-JsonFile (Join-Path $root 'logs\upgrade-last.json') $script:report }
function Step([string]$Name,[scriptblock]$Work){
    $script:report.currentStage=$Name;Save-Report
    $timer=[Diagnostics.Stopwatch]::StartNew();& $Work
    $script:report.stages+=@{name=$Name;milliseconds=$timer.ElapsedMilliseconds};Save-Report
}
function Run-Script([string]$Name,[string]$Verb=''){
    $log=Join-Path $folder ($Name+'-'+$Verb+'-'+[guid]::NewGuid().ToString('N')+'.log')
    $command="& '"+(Join-Path $root $Name).Replace("'","''")+"'"
    if($Verb){$command+=" -Action '"+$Verb.Replace("'","''")+"'"}
    $command+=" *> '"+$log.Replace("'","''")+"'"
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $child=Start-Process powershell.exe -WindowStyle Hidden -PassThru -WorkingDirectory $root -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded)
    try{if(-not $child.WaitForExit(100000)){$child.Kill();throw ('Update child timed out: '+$Name)};if($child.ExitCode -ne 0){throw ('Update stage failed: '+$Name+' '+$Verb+'; inspect '+$log)}}finally{$child.Dispose()}
}
function Stop-OwnEngines {
    # Used only if the normal Stop cannot reach a damaged coordinator.
    [IO.File]::WriteAllText((Join-Path $root 'runtime\stop.request'),[guid]::NewGuid().ToString('N'))
    $watchFile=Join-Path $root 'runtime\watch.pid'
    if(Test-Path -LiteralPath $watchFile){
        $watchId=[int][IO.File]::ReadAllText($watchFile).Trim()
        $watch=Get-CimInstance Win32_Process -Filter ('ProcessId='+$watchId) -ErrorAction SilentlyContinue
        if($watch -and $watchId -ne $PID -and $watch.CommandLine -like ('*'+$root+'*') -and $watch.CommandLine -match '-Action\s+Watch'){Stop-Process -Id $watchId -Force -ErrorAction SilentlyContinue}
    }
    foreach($process in @(Get-Process smart-router,vpn-bridge,RoutePilot.Agent -ErrorAction SilentlyContinue)){
        if($process.Path -eq (Join-Path $root 'runtime\smart-router.exe') -or $process.Path -eq (Join-Path $install 'vpn-bridge.exe')){$process | Stop-Process -Force}
        elseif($process.Path -eq (Join-Path $install 'RoutePilot.Agent.exe')){
            $identity=Read-JsonFile (Join-Path $root 'runtime\policy-process.json')
            if($process.Id -eq $identity.Pid -and [Math]::Abs(($process.StartTime.ToUniversalTime()-[DateTime]::Parse($identity.StartedUtc).ToUniversalTime()).TotalMilliseconds) -lt 100){$process | Stop-Process -Force}
        }
    }
}
try{
    Step 'protected-backup' {
        New-Item -ItemType Directory -Path $backup | Out-Null
        Copy-Item -LiteralPath $install -Destination (Join-Path $backup 'Agent') -Recurse
        Copy-Item -LiteralPath (Join-Path $root 'RoutePilot.exe') -Destination (Join-Path $backup 'RoutePilot.exe')
        $script:oldAppHash=(Get-FileHash -LiteralPath (Join-Path $backup 'RoutePilot.exe')).Hash
    }
    Step 'close-owned-interface' {
        @(Get-Process RoutePilot -ErrorAction SilentlyContinue | Where-Object Path -eq (Join-Path $root 'RoutePilot.exe')) | Stop-Process -Force
    }
    Step 'stop-owned-session' {
        $script:stopped=$true;Run-Script 'SmartRouting.ps1' 'Stop'
        $script:stopToken=[IO.File]::ReadAllText((Join-Path $root 'runtime\stop.request'))
    }
    Step 'prepare-configurations' { Run-Script 'SmartRouting.ps1' 'Prepare' }
    Step 'install-protected-package' { $script:packageTouched=$true;Run-Script 'Install-Agent.ps1';$script:installed=$true }
    Step 'publish-interface' {
        Copy-Item -LiteralPath $appBuild -Destination (Join-Path $root 'RoutePilot.exe') -Force
        if((Get-FileHash -LiteralPath $appBuild).Hash -ne (Get-FileHash -LiteralPath (Join-Path $root 'RoutePilot.exe')).Hash){throw 'Published interface hash differs.'}
    }
    Step 'resume-previous-mode' {
        if($before.running -and [IO.File]::ReadAllText((Join-Path $root 'runtime\stop.request')) -eq $stopToken){
            Run-Script 'SmartRouting.ps1' $before.mode
        }else{$script:report.awaitingManualConnection=$true}
    }
    Step 'verify-session' {
        if($report.awaitingManualConnection){return}
        if((Test-Path -LiteralPath (Join-Path $root 'runtime\stop.request')) -and [IO.File]::ReadAllText((Join-Path $root 'runtime\stop.request')) -ne $stopToken){$script:report.awaitingManualConnection=$true;return}
        $agent=Join-Path $install 'RoutePilot.Agent.exe'
        $health=& $agent --session-health | ConvertFrom-Json
        $current=Read-JsonFile (Join-Path $root 'runtime\session.json')
        $policy=Read-JsonFile (Join-Path $root 'runtime\policy-state.json')
        if(-not $health.healthy -or -not $current.running -or $current.recovering -or -not $policy.epoch.StartsWith($current.id+':') -or $policy.version -ne $version -or $policy.publication.phase -ne 'Applied'){throw 'The upgraded session did not pass independent HTTPS and publication checks.'}
        if((Get-Service RoutePilotRescue).Status -ne 'Running'){throw 'The rescue service is not running.'}
        $script:report.sessionId=$current.id;$script:report.transportHealthy=$true;$script:report.policyVersion=$policy.version
    }
    $report.success=$true
}catch{
    $report.error=$_.Exception.Message
    if($stopped){
        try{
            $userCancelled=$false
            if($stopToken -and (Test-Path -LiteralPath (Join-Path $root 'runtime\stop.request'))){$userCancelled=[IO.File]::ReadAllText((Join-Path $root 'runtime\stop.request')) -ne $stopToken}
            try{Run-Script 'SmartRouting.ps1' 'Stop'}catch{Stop-OwnEngines}
            if($packageTouched){
                $savedRoot=Join-Path $backup 'Agent'
                $files=@(Get-ChildItem -LiteralPath $savedRoot -File -Recurse | ForEach-Object {@{Source=$_.FullName;Name=$_.FullName.Substring($savedRoot.Length+1)}})
                Publish-AgentPackage $files $install {Stop-Service RoutePilotRescue -ErrorAction SilentlyContinue} {Start-Service RoutePilotRescue;(Get-Service RoutePilotRescue).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))}
            }
            if((Get-FileHash -LiteralPath (Join-Path $backup 'RoutePilot.exe')).Hash -ne $oldAppHash){throw 'Protected interface backup changed.'}
            Copy-Item -LiteralPath (Join-Path $backup 'RoutePilot.exe') -Destination (Join-Path $root 'RoutePilot.exe') -Force
            if($before.running -and -not $userCancelled){Run-Script 'SmartRouting.ps1' $before.mode;$health=& (Join-Path $install 'RoutePilot.Agent.exe') --session-health | ConvertFrom-Json;if(-not $health.healthy){throw 'Previous package did not restore HTTPS.'}}
            elseif($userCancelled){$report.awaitingManualConnection=$true}
            $report.restored=$true
        }catch{$report.rollbackError=$_.Exception.Message}
    }
}finally{
    $report.completedUtc=[DateTime]::UtcNow.ToString('o');Save-Report;$lock.Dispose()
}
if(-not $report.success){exit 1}
