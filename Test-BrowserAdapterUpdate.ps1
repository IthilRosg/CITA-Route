#requires -Version 5.1
# Run only in the clean second VMware guest after staging the browser fixture
# and this repository's AgentPackage.ps1 at C:\CitaLab\AgentPackage.ps1.
$ErrorActionPreference='Stop'
$report=@{success=$false;startedUtc=[datetime]::UtcNow.ToString('o')};$chrome=$null;$source=$null;$powerState=0
function Assert-LabChrome {
    if(-not $chrome){throw 'Missing owned Chrome identity'}
    $chrome.Refresh()
    if($chrome.HasExited -or $chrome.Path -ne $identity.path -or [Math]::Abs(($chrome.StartTime.ToUniversalTime()-([datetime]$identity.startedUtc).ToUniversalTime()).TotalMilliseconds) -ge 100){throw 'Owned Chrome identity changed'}
}
function Assert-NoNative {
    if(@(Get-Process RoutePilot.Agent -ErrorAction SilentlyContinue).Count){throw 'An agent/native executable is still running'}
}
function Start-LabAgent {
    $script:activations++
    if($script:injectFailure -and $script:activations -eq 1){throw 'Injected package activation failure'}
    $p=Start-Process 'C:\CitaLab\Adaptive\browser\RoutePilot.Agent.exe' -ArgumentList '--self-test' -WindowStyle Hidden -PassThru -RedirectStandardOutput 'C:\CitaLab\adapter-update-self.out' -RedirectStandardError 'C:\CitaLab\adapter-update-self.err';$null=$p.Handle
    try{if(-not $p.WaitForExit(90000)){$p.Kill();throw 'Updated agent self-test deadline'};$p.Refresh();if($p.ExitCode -ne 0){throw 'Updated agent self-test failed'}}finally{$p.Dispose()}
    $script:groups=@(Get-Content 'C:\CitaLab\adapter-update-self.out'|Where-Object {$_ -like 'PASS:*'}).Count
    if($script:groups -lt 27){throw 'Not all agent self-test groups ran'}
}
try {
    if(([string](Get-ItemProperty 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS').SystemManufacturer) -notmatch 'VMware'){throw 'VMware guest required'}
    if(-not ('CitaAdapterUpdateAwake' -as [type])){Add-Type 'using System.Runtime.InteropServices; public static class CitaAdapterUpdateAwake { [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint state); }'}
    $powerState=[CitaAdapterUpdateAwake]::SetThreadExecutionState(2147483649);if($powerState -eq 0){throw 'Could not hold guest standby during update acceptance'}
    if(Test-Path 'HKCU:\Software\CITA\Route\BrowserFeedback'){throw 'A prior native registration remains'}
    $env:DOTNET_ROOT='C:\CitaLab\Dotnet';$env:DOTNET_ROOT_X64='C:\CitaLab\Dotnet'
    & 'C:\CitaLab\Test-BrowserTun.ps1' -KeepBrowser
    $browser=Get-Content 'C:\CitaLab\browser-tun-result.json' -Raw|ConvertFrom-Json
    $identity=$browser.result.retainedBrowser
    if(-not $identity -or $identity.path -ne 'C:\CitaLab\Browser\chrome-win64\chrome.exe'){throw 'The owned browser identity is missing'}
    $chrome=Get-Process -Id $identity.pid -ErrorAction Stop;Assert-LabChrome
    if(-not $browser.success){throw 'The complete owned browser cycle failed'}
    Assert-NoNative
    if(Test-Path 'HKCU:\Software\CITA\Route\BrowserFeedback'){throw 'The old adapter registration was not removed'}
    . 'C:\CitaLab\AgentPackage.ps1'
    $target='C:\CitaLab\Adaptive\browser';$source='C:\CitaLab\NativeUpdate\'+[guid]::NewGuid().ToString('N');[void][IO.Directory]::CreateDirectory($source)
    $names=@('RoutePilot.Agent.exe','RoutePilot.Agent.dll','RoutePilot.Agent.deps.json','RoutePilot.Agent.runtimeconfig.json','Cita.BrowserLab.exe','stage.json')
    $files=@();$hashes=@{}
    foreach($name in $names){$from=Join-Path $target $name;Copy-Item -LiteralPath $from -Destination (Join-Path $source $name);$hashes[$name]=(Get-FileHash -LiteralPath $from).Hash;$files+=@{Source=(Join-Path $source $name);Name=$name}}
    $script:activations=0;$script:injectFailure=$true;$failed=$false
    try{Publish-AgentPackage $files $target {Assert-NoNative;Assert-LabChrome} {Start-LabAgent}}catch{$failed=$_.Exception.Message -like '*Injected package activation failure*'}
    if(-not $failed -or $script:activations -ne 2){throw 'The injected update failure did not restore and restart the old package'}
    foreach($name in $names){if((Get-FileHash -LiteralPath (Join-Path $target $name)).Hash -ne $hashes[$name]){throw 'Rollback did not restore the complete agent package'}}
    Assert-LabChrome;$report.rollbackWithBrowserOpen=$true
    $script:activations=0;$script:injectFailure=$false
    Publish-AgentPackage $files $target {Assert-NoNative;Assert-LabChrome} {Start-LabAgent}
    foreach($name in $names){if((Get-FileHash -LiteralPath (Join-Path $target $name)).Hash -ne $hashes[$name]){throw 'The new package differs after promotion'}}
    Assert-LabChrome;Assert-NoNative
    $report.successfulReplacementWithBrowserOpen=$true;$report.agentSelfTestGroups=$script:groups
    $report.nativeProcessesRemaining=@(Get-Process RoutePilot.Agent -ErrorAction SilentlyContinue).Count
    $report.temporaryTasksRemaining=@(Get-ScheduledTask -TaskName 'Cita-BrowserLab-*' -ErrorAction SilentlyContinue).Count
    if($report.temporaryTasksRemaining -ne 0){throw 'Temporary browser launcher tasks remain'}
    $report.scope='Real package directory promotion/rollback with the same candidate binary; owned Chrome stays open. No MSI or protected rescue-service upgrade.'
    $report.success=$true
} catch {$report.error=$_.Exception.Message}
finally {
    if($powerState){[void][CitaAdapterUpdateAwake]::SetThreadExecutionState($powerState)}
    if($chrome){try{Assert-LabChrome;Stop-Process -Id $chrome.Id -Force -ErrorAction Stop;[void]$chrome.WaitForExit(5000)}catch{$report.browserCleanupError=$_.Exception.Message;$report.success=$false}finally{$chrome.Dispose()}}
    if($source){$resolved=[IO.Path]::GetFullPath($source);$sourceGuid=[guid]::Empty;if($resolved.StartsWith('C:\CitaLab\NativeUpdate\',[StringComparison]::OrdinalIgnoreCase) -and [guid]::TryParseExact((Split-Path $resolved -Leaf),'N',[ref]$sourceGuid)){Remove-Item -LiteralPath $resolved -Recurse -Force}}
    $report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 8|Set-Content 'C:\CitaLab\adapter-update-result.json' -Encoding UTF8
}
