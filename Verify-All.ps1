$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if(-not $admin){
    $command="& '"+$PSCommandPath.Replace("'","''")+"'"
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $child=Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded)
    $child.WaitForExit();exit $child.ExitCode
}
. (Join-Path $root 'FileStorage.ps1')
. (Join-Path $root 'AgentLease.ps1')
Assert-NoAgentRun
if(Get-Process smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'Stop the current RoutePilot engine before the full check.'}
$lock=[IO.File]::Open((Join-Path $root 'runtime\full-check.lock'),'OpenOrCreate','ReadWrite','None')
$folder=Join-Path $root ('runtime\full-check-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder | Out-Null
$report=@{startedUtc=[DateTime]::UtcNow.ToString('o');success=$false;stages=@();folder=$folder}
function Step([string]$Name,[scriptblock]$Check){
    $script:report.currentStage=$Name;Write-JsonFile (Join-Path $folder 'report.json') $script:report
    $timer=[Diagnostics.Stopwatch]::StartNew();& $Check
    $script:report.stages+=@{name=$Name;success=$true;milliseconds=$timer.ElapsedMilliseconds}
    Write-JsonFile (Join-Path $folder 'report.json') $script:report
}
function Run-Script([string]$Name){
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root $Name) *> (Join-Path $folder ($Name+'.log'))
    if($LASTEXITCODE -ne 0){throw ('Failed '+$Name+'; inspect '+$folder)}
}
function Service-Scenario([string]$Command){
    $agent=Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe'
    $reply=& $agent $Command | ConvertFrom-Json
    if(-not $reply.Success){throw $reply.Message}
    $limit=[DateTime]::Parse($reply.State.ReturnDeadlineUtc).ToUniversalTime().AddSeconds(25)
    do {
        Start-Sleep -Seconds 1
        $state=Read-JsonFile (Join-Path $env:ProgramData 'RoutePilot\run.json')
        if($state.Id -ne $reply.State.Id){throw 'The test generation changed unexpectedly.'}
    }while($state.Phase -in @('Armed','Testing','Restoring') -and [DateTime]::UtcNow -lt $limit)
    Write-JsonFile (Join-Path $folder ($Command+'-'+$state.Generation+'.json')) $state
    if($state.Phase -ne 'Restored' -or @($state.Results | Where-Object {-not $_.Success -and -not $_.Informational}).Count){throw ('Scenario failed: '+$Command+' / '+$state.LastMessage)}
    $script:report.runs+=@{command=$Command;id=$state.Id;generation=$state.Generation;phase=$state.Phase}
}
try {
    foreach($scriptName in @('Verify-Prepare.ps1','Verify-Storage.ps1','Verify-RuleFeeds.ps1','Verify-ProfileSync.ps1','Verify-WireSockRecovery.ps1','Verify-AgentPackage.ps1','Verify-SessionRecovery.ps1')){
        Step $scriptName {Run-Script $scriptName}
    }
    Step 'agent-self-tests' {
        & (Join-Path $root 'runtime\agent-build\RoutePilot.Agent.exe') --self-test *> (Join-Path $folder 'agent-self-test.log')
        if($LASTEXITCODE -ne 0){throw 'Agent self-tests failed.'}
    }
    Step 'WPF-self-tests' {
        $app=Start-Process (Join-Path $root 'RoutePilot.exe') -ArgumentList '--self-test' -WindowStyle Hidden -PassThru
        if(-not $app.WaitForExit(45000) -or $app.ExitCode -ne 0){throw 'WPF self-tests failed.'}
    }
    Step 'optimization-stress' {Run-Script 'Verify-Optimization.ps1'}
    Step 'prepare-current-mode' {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'SmartRouting.ps1') -Action Prepare *> (Join-Path $folder 'prepare-before-tests.log')
        if($LASTEXITCODE -ne 0){throw 'Could not prepare current mode.'}
    }
    Step 'routing-policy-and-DNS' {Run-Script 'Verify-Routing.ps1'}
    Step 'production-fault-injection' {Run-Script 'Verify-Automation.ps1'}
    Step 'refresh-protected-snapshot' {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'SmartRouting.ps1') -Action Prepare *> (Join-Path $folder 'prepare.log')
        if($LASTEXITCODE -ne 0){throw 'Production configuration preparation failed.'}
        Run-Script 'Install-Agent.ps1'
    }
    $report.runs=@()
    Step 'guard-restart-1' {Service-Scenario 'test-guard-restart'}
    Step 'guard-restart-2' {Service-Scenario 'test-guard-restart'}
    Step 'VPNUS-restart' {Service-Scenario 'test-vpnus'}
    Step 'AWG2-proxy' {Service-Scenario 'test-wiresock'}
    Step 'system-TUN-1' {Service-Scenario 'test-system'}
    Step 'system-TUN-2' {Service-Scenario 'test-system'}
    Step 'cleanup-postconditions' {
        if(Get-Process smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'An engine process remains.'}
        $protectedConfig=Read-JsonFile (Join-Path $env:ProgramFiles 'RoutePilot\Agent\agent-config.json')
        if(Get-NetRoute -DestinationPrefix ($protectedConfig.EndpointIp+'/32') -ErrorAction SilentlyContinue){throw 'A temporary endpoint route remains.'}
        if((Get-NetAdapter -Name VPNUS -ErrorAction SilentlyContinue).Status -ne 'Up'){throw 'VPNUS was not restored.'}
    }
    $report.success=$true
}catch{$report.error=$_.Exception.Message}
finally {
    $report.completedUtc=[DateTime]::UtcNow.ToString('o')
    Write-JsonFile (Join-Path $folder 'report.json') $report
    Write-JsonFile (Join-Path $root 'logs\full-check-last.json') $report
    $lock.Dispose()
}
if(-not $report.success){exit 1}
