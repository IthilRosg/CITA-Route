[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Package,[Parameter(Mandatory=$true)][string]$Version,
      [int]$GuiPid=0,[string]$GuiStartedUtc='', [switch]$ValidateOnly,
      [string]$OperationId='', [string]$ManifestSha256='')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
. (Join-Path $root 'FileStorage.ps1')
if($OperationId -and $OperationId -notmatch '^[a-f0-9]{32}$'){throw 'Invalid update operation identity.'}
function Write-UpdateHandoff([string]$Phase,[string]$Message=''){
    if($OperationId){Write-JsonFile (Join-Path $root ('runtime\update-handoff-'+$OperationId+'.json')) @{id=$OperationId;guiPid=$GuiPid;phase=$Phase;message=$Message;updatedUtc=[DateTime]::UtcNow.ToString('o')}}
}
trap{try{Write-UpdateHandoff 'Failed' $_.Exception.Message}catch{};throw}
. (Join-Path $root 'AgentLease.ps1')
$packagePath=[IO.Path]::GetFullPath($Package).TrimEnd('\')
$updatesPrefix=[IO.Path]::GetFullPath((Join-Path $root 'runtime\updates')).TrimEnd('\')+'\'
if(-not $packagePath.StartsWith($updatesPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Update package must be staged inside this installation.'}
$desired=[version]$Version
if($OperationId -and -not $ManifestSha256){throw 'Missing verified manifest identity.'}
if($ManifestSha256 -and ($ManifestSha256 -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath (Join-Path $packagePath 'manifest.json')).Hash -ne $ManifestSha256)){throw 'Release manifest changed after verification.'}
$manifest=Read-JsonFile (Join-Path $packagePath 'manifest.json')
if([version]$manifest.version -ne $desired -or $manifest.architecture -ne 'win-x64'){throw 'Release manifest does not match the requested update.'}
$names=@{}
foreach($file in $manifest.files){
    $source=[IO.Path]::GetFullPath((Join-Path $packagePath $file.path))
    if(-not $source.StartsWith($packagePath+'\',[StringComparison]::OrdinalIgnoreCase) -or $names.ContainsKey($source)){throw 'Unsafe manifest path.'}
    $names[$source]=$file.sha256
    if((Get-Item -LiteralPath $source).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $file.sha256){throw 'Update payload has changed.'}
}
$files=@('RoutePilot.exe','SmartRouting.ps1','FileStorage.ps1','RuleFeeds.ps1','WireSockCli.ps1','WireSockProfiles.ps1','WireSockRecovery.ps1','AgentLease.ps1','SessionRecovery.ps1','ConnectionEnvironment.ps1','Configure-Connection.ps1','AgentPackage.ps1','Install-Agent.ps1','Verify-Routing.ps1','Install-Release.ps1','DesktopRuntime.ps1','product.json','README.md','THIRD-PARTY.md','LICENSE','runtime\smart-router.exe','runtime\vpn-bridge.exe','runtime\agent-build\RoutePilot.Agent.exe','runtime\rules\russia_inside.srs','runtime\rules\discord.srs','rules\telegram-cidr.txt')
foreach($name in $files){if(-not $names.ContainsKey((Join-Path $packagePath $name))){throw ('Required update file missing: '+$name)}}
if(-not $manifest.selfContained){
    # Old clients copy only their historical allowlist. Read the already hashed
    # helper from this verified package, so the first update from 0.5.x can
    # bootstrap the new helper on the next update without losing compatibility.
    . (Join-Path $packagePath 'DesktopRuntime.ps1')
    if($manifest.desktopRuntimeMajor -ne 8 -or -not(Test-MachineDesktopRuntime)){throw 'The update requires machine-wide .NET Desktop Runtime 8 x64. Use the installer to prepare it.'}
}
if([version](Get-Item -LiteralPath (Join-Path $packagePath 'RoutePilot.exe')).VersionInfo.FileVersion -ne [version]($Version+'.0')){throw 'Release executable version differs.'}
Assert-NoAgentRun
function Assert-Disconnected {
    $path=Join-Path $root 'runtime\session.json'
    if((Test-Path -LiteralPath $path) -and (Read-JsonFile $path).running){throw 'Disconnect the route before installing an update.'}
    if(Get-Process -Name 'smart-router','vpn-bridge' -ErrorAction SilentlyContinue){throw 'A routing engine is still running. Update postponed.'}
}
Assert-Disconnected
if($ValidateOnly){Write-Output 'PASS: verified release payload; disconnected installation only; no files changed.';exit 0}
$connectionOperationLock=$null;$setupLock=$null;$upgradeLock=$null;$controlLock=$null;$coordinated=$false;$applied=New-Object 'System.Collections.Generic.List[object]'
$backup=Join-Path $root ('runtime\update-backup-'+[guid]::NewGuid().ToString('N'))
$report=@{version=$Version;operationId=$OperationId;success=$false;restored=$false;startedUtc=[DateTime]::UtcNow.ToString('o');backup=$backup}
try{
    if(Get-Service RoutePilotRescue -ErrorAction SilentlyContinue){Invoke-AgentCoordinator 'begin-operation';$coordinated=$true}
    $connectionOperationLock=[IO.File]::Open((Join-Path $root 'runtime\connection-operation.lock'),'OpenOrCreate','ReadWrite','None')
    $setupLock=[IO.File]::Open((Join-Path $root 'runtime\setup.lock'),'OpenOrCreate','ReadWrite','None')
    $upgradeLock=[IO.File]::Open((Join-Path $root 'runtime\upgrade.lock'),'OpenOrCreate','ReadWrite','None')
    $controlLock=[IO.File]::Open((Join-Path $root 'runtime\network-control.lock'),'OpenOrCreate','ReadWrite','None')
    Assert-NoAgentRun;Assert-Disconnected
    if($GuiPid -le 0 -or -not $GuiStartedUtc){throw 'Missing GUI process identity.'}
    $gui=Get-Process -Id $GuiPid -ErrorAction SilentlyContinue
    if($gui){
        if($gui.Path -ne (Join-Path $root 'RoutePilot.exe') -or [Math]::Abs(($gui.StartTime.ToUniversalTime()-[DateTime]::Parse($GuiStartedUtc).ToUniversalTime()).TotalMilliseconds) -gt 100){throw 'Update caller identity changed.'}
        Write-UpdateHandoff 'Ready'
        if(-not $gui.WaitForExit(25000)){throw 'Close the interface before updating.'}
    }
    elseif($OperationId){throw 'GUI exited before update ownership was acknowledged.'}
    Assert-NoAgentRun;Assert-Disconnected
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    foreach($name in $files){
        $target=Join-Path $root $name;$source=Join-Path $packagePath $name;$saved=Join-Path $backup $name
        New-Item -ItemType Directory -Path (Split-Path $target),(Split-Path $saved) -Force | Out-Null
        $existed=Test-Path -LiteralPath $target
        if($existed){Copy-Item -LiteralPath $target -Destination $saved}
        $applied.Add(@{Target=$target;Backup=$saved;Existed=$existed})
        $temporary=$target+'.'+[guid]::NewGuid().ToString('N')+'.update'
        try{
            Copy-Item -LiteralPath $source -Destination $temporary
            if((Get-FileHash -LiteralPath $temporary).Hash -ne $names[$source]){throw 'Staged update hash differs.'}
            if($existed){[IO.File]::Replace($temporary,$target,[NullString]::Value)}else{[IO.File]::Move($temporary,$target)}
        }finally{Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue}
    }
    # The protected agent is upgraded by Apply Connection, with its normal UAC
    # transaction. Never change a Windows service from this non-elevated updater.
    Write-JsonFile (Join-Path $root 'data\pending-agent-update.json') @{version=$Version;createdUtc=[DateTime]::UtcNow.ToString('o')}
    $report.success=$true
    # Only the current user's registered installation at this exact path may
    # change its Windows Apps version label. Portable copies never register.
    try{
        $registration='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CITA.Route.Client_is1'
        $installed=Get-ItemProperty -LiteralPath $registration -ErrorAction SilentlyContinue
        if($installed -and $installed.InstallLocation -and [IO.Path]::GetFullPath((Join-Path $installed.InstallLocation 'client')).TrimEnd('\') -eq [IO.Path]::GetFullPath($root).TrimEnd('\')){
            Set-ItemProperty -LiteralPath $registration -Name DisplayVersion -Value $Version
        }
    }catch{$report.registrationWarning='Windows Apps version label could not be refreshed.'}
}catch{
    $report.error=$_.Exception.Message;$restoreErrors=@()
    Write-UpdateHandoff 'Failed' $report.error
    for($index=$applied.Count-1;$index -ge 0;$index--){
        $entry=$applied[$index]
        try{if($entry.Existed){Copy-Item -LiteralPath $entry.Backup -Destination $entry.Target -Force}else{Remove-Item -LiteralPath $entry.Target -Force -ErrorAction SilentlyContinue}}
        catch{$restoreErrors+=$_.Exception.Message}
    }
    $report.restored=($restoreErrors.Count -eq 0);if($restoreErrors.Count){$report.restoreErrors=$restoreErrors}
}finally{
    foreach($held in @($controlLock,$upgradeLock,$setupLock,$connectionOperationLock)){if($held){$held.Dispose()}}
    if($coordinated){Invoke-AgentCoordinator 'end-operation'}
    $report.finishedUtc=[DateTime]::UtcNow.ToString('o')
    Write-JsonFile (Join-Path $root 'logs\release-update-last.json') $report
}
Start-Process -FilePath (Join-Path $root 'RoutePilot.exe') -WindowStyle Normal | Out-Null
if(-not $report.success){exit 1}
