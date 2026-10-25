param([string]$UserSid,[string]$UserProfile,[ValidateSet('Unchanged','Enable')][string]$BootStartup='Unchanged',[int]$SetupOwner=0,[long]$SetupHandle=0,[switch]$TransferStartup,[int]$TransferOwner=0,[string]$TransferHandles='')
$ErrorActionPreference = 'Stop'
if (-not $UserSid) { $UserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
if (-not $UserProfile) { $UserProfile = $env:USERPROFILE }
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $scriptLiteral = $PSCommandPath.Replace("'","''")
    $sidLiteral = $UserSid.Replace("'","''")
    $profileLiteral = $UserProfile.Replace("'","''")
    $command = "& '$scriptLiteral' -UserSid '$sidLiteral' -UserProfile '$profileLiteral' -BootStartup '$BootStartup'"
    if($TransferStartup){$command+=' -TransferStartup'}
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $child = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -PassThru -Wait -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded)
    exit $child.ExitCode
}
$logPath = Join-Path $PSScriptRoot 'logs\agent-install.log'
. (Join-Path $PSScriptRoot 'AgentPackage.ps1')
. (Join-Path $PSScriptRoot 'FileStorage.ps1')
. (Join-Path $PSScriptRoot 'ConnectionEnvironment.ps1')
. (Join-Path $PSScriptRoot 'AgentLease.ps1')
. (Join-Path $PSScriptRoot 'NativeMaintenance.ps1')
$nativeCoordinated=$false
$setupReservation=$null
$startupTransfer=$null
try {
    # Resolve prerequisites before acquiring a lease, stopping the existing
    # service or replacing protected files. A user SDK cannot run SYSTEM.
    Assert-AgentRuntimeReady $PSScriptRoot
    $setupPath=Join-Path $PSScriptRoot 'runtime\setup.lock'
    if($SetupOwner -gt 0){$setupReservation=Receive-SetupReservation $setupPath $SetupOwner $SetupHandle}
    else{$setupReservation=[IO.File]::Open($setupPath,'OpenOrCreate','ReadWrite','None')}
    $SetupOwner=$PID;$SetupHandle=$setupReservation.SafeFileHandle.DangerousGetHandle().ToInt64()
    if(Get-StartupRouteState $PSScriptRoot){throw 'Disconnect the startup route before updating protected files.'}
    if (Get-Process -Name 'smart-router','vpn-bridge' -ErrorAction SilentlyContinue) { throw 'Stop the current RoutePilot engine before updating its protected files.' }
    $build = Join-Path $PSScriptRoot 'runtime\agent-build\RoutePilot.Agent.exe'
    if (-not (Test-Path -LiteralPath $build)) { throw 'Build the rescue agent first.' }
    Stop-NativeForMaintenance
    if((Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'RoutePilot\Agent\native\service.json')) -and (Get-Service RoutePilotRescue -ErrorAction SilentlyContinue)){Invoke-AgentCoordinator 'begin-operation';$nativeCoordinated=$true}
    $installRoot = Join-Path $env:ProgramFiles 'RoutePilot\Agent'
    $dataRoot = Join-Path $env:ProgramData 'RoutePilot'
    foreach ($target in @((Split-Path $installRoot),$installRoot,$dataRoot)) {
        if (Test-Path -LiteralPath $target) {
            if ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'A protected installation path is a reparse point.' }
        }
    }
    $service = Get-Service -Name RoutePilotRescue -ErrorAction SilentlyContinue
    $bootSettingsPath=Join-Path $installRoot 'startup-settings.json'
    $refreshBoot=$false
    if(Test-Path -LiteralPath $bootSettingsPath){
        $oldBoot=Read-JsonFile $bootSettingsPath
        if($oldBoot.Enabled){
            if([IO.Path]::GetFullPath($oldBoot.SourceRoot).TrimEnd('\') -ine [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')){
                if(-not $TransferStartup){throw 'Enabled startup belongs to another client installation. Connect from the new client to transfer an inactive installation.'}
                $startupTransfer=Reserve-AgentStartupTransfer $installRoot $dataRoot $PSScriptRoot $UserSid $TransferOwner $TransferHandles
            }
            $refreshBoot=$true
        }
    }
    $bootStatePath=Join-Path $dataRoot 'startup-state.json'
    $oldBootState=if(Test-Path -LiteralPath $bootStatePath){[IO.File]::ReadAllBytes($bootStatePath)}else{$null}
    if ($service) {
        $image = (Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'").PathName
        if ($image -ne ('"' + (Join-Path $installRoot 'RoutePilot.Agent.exe') + '" --service')) { throw 'The service name belongs to a different installation.' }
        $journalPath = Join-Path $dataRoot 'run.json'
        if (Test-Path -LiteralPath $journalPath) {
            $journal = [IO.File]::ReadAllText($journalPath) | ConvertFrom-Json
            if ($journal.Phase -in @('Armed','Testing','Restoring','RecoveryFailed')) { throw 'Recover the unfinished run before upgrading the service.' }
        }
    }
    foreach ($folder in @((Split-Path $installRoot),$installRoot,$dataRoot)) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true,$false)
        $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
        foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
            $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
                (New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
        }
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            (New-Object Security.Principal.SecurityIdentifier($UserSid)), 'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')))
        Set-Acl -LiteralPath $folder -AclObject $acl
    }
    $files = @(@{Source=$build;Name='RoutePilot.Agent.exe'},@{Source=(Join-Path $PSScriptRoot 'runtime\vpn-bridge.exe');Name='vpn-bridge.exe'},@{Source=(Join-Path $PSScriptRoot 'runtime\smart-router.exe');Name='smart-router.exe'})
    foreach ($name in @('russia_inside.srs','discord.srs','telegram.json','personal-direct.json','personal-vpn.json','auto-direct.json','auto-dpi.json','auto-vpn.json','auto-blocked.json')) {
        $files += @{Source=(Join-Path $PSScriptRoot ('runtime\rules\'+$name));Name=('rules\'+$name)}
    }
    $connection=Get-ConnectionSettings $PSScriptRoot
    $wireSockCli=Find-WireSockCli $connection.CliPath
    Assert-ProtectedWireSockCli $wireSockCli
    $endpoint=Read-JsonFile (Join-Path $PSScriptRoot 'runtime\connection-endpoint.json')
    if(-not $connection.SourceProfile){throw 'Select a source WireSock profile and prepare the connection first.'}
    if($endpoint.SourceProfile -ne $connection.SourceProfile){throw 'Prepared profile does not match the selection. Apply the connection again.'}
    $config = @{UserSid=$UserSid;UserProfile=$UserProfile;DataDirectory=$dataRoot;AppRoot=$PSScriptRoot;WireSockSourceProfile=$connection.SourceProfile;WireSockCliPath=$wireSockCli;EndpointIp=$endpoint.Ip;EndpointPort=$endpoint.Port;SourceEndpoint=$endpoint.SourceEndpoint;ExpectedEgressIp=$connection.ExpectedEgressIp;ConnectionRevision=[guid]::NewGuid().ToString('N')}
    $configSource = Join-Path $PSScriptRoot 'runtime\agent-install-config.json'
    [IO.File]::WriteAllText($configSource,($config | ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
    $files += @{Source=$configSource;Name='agent-config.json'},@{Source=(Join-Path $PSScriptRoot 'runtime\router.json');Name='production-router.json'}
    Publish-AgentPackage $files $installRoot {
        $current=Get-Service -Name RoutePilotRescue -ErrorAction SilentlyContinue
        if($current){Stop-Service -Name RoutePilotRescue; $current.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60))}
        if($startupTransfer){
            # Both installations are reserved and the old coordinator stopped.
            if(Get-Process -Name 'smart-router','vpn-bridge' -ErrorAction SilentlyContinue){throw 'Startup transfer requires an inactive route.'}
            $transferState=Read-JsonFile $bootStatePath
            $transferState.Phase='StoppedForBoot';$transferState.UpdatedUtc=[datetime]::UtcNow.ToString('o')
            Write-JsonFile $bootStatePath $transferState
        }
    } {
        if (-not (Get-Service -Name RoutePilotRescue -ErrorAction SilentlyContinue)) {
            New-Service -Name RoutePilotRescue -BinaryPathName ('"' + (Join-Path $installRoot 'RoutePilot.Agent.exe') + '" --service') -DisplayName 'RoutePilot Rescue' -StartupType Automatic | Out-Null
        }
        $configureResult = & (Join-Path $installRoot 'RoutePilot.Agent.exe') --configure-service-recovery 2>&1
        $configureCode=$LASTEXITCODE
        if($BootStartup -eq 'Enable'){Set-Service -Name RoutePilotRescue -StartupType Automatic}
        if ($configureCode -ne 0) { throw ('Could not configure service restart recovery: ' + ($configureResult -join ' ')) }
        Start-Service -Name RoutePilotRescue
        (Get-Service -Name RoutePilotRescue).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
    } -Prepare {
        if($BootStartup -eq 'Enable' -or $refreshBoot){
            $mode=if($BootStartup -eq 'Enable'){'on'}else{'refresh'}
            & (Join-Path $installRoot 'RoutePilot.Agent.exe') --configure-boot $PSScriptRoot $mode $SetupOwner $SetupHandle
            if($LASTEXITCODE -ne 0){throw 'Could not prepare protected startup. The previous protected installation will be restored.'}
        }
    } -Restore {
        # On a failed first install there is no old executable to restart.
        # Remove only the service created by this transaction at this exact path.
        if(-not $service){
            $createdService=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'" -ErrorAction SilentlyContinue
            if($createdService){
                if($createdService.PathName -ne ('"'+(Join-Path $installRoot 'RoutePilot.Agent.exe')+'" --service')){throw 'Rollback found an unrelated service registration.'}
                & sc.exe delete RoutePilotRescue | Out-Null
                if($LASTEXITCODE -ne 0){throw 'Could not remove the failed first-install service registration.'}
            }
        }
        if($null -ne $oldBootState){
            $temporary=$bootStatePath+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
            try{
                [IO.File]::WriteAllBytes($temporary,$oldBootState)
                if(Test-Path -LiteralPath $bootStatePath){[IO.File]::Replace($temporary,$bootStatePath,[NullString]::Value)}else{[IO.File]::Move($temporary,$bootStatePath)}
            }finally{Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue}
        }
        elseif(Test-Path -LiteralPath $bootStatePath){Remove-Item -LiteralPath $bootStatePath -Force}
    }
    Remove-Item -LiteralPath (Join-Path $dataRoot 'guard-proof.json') -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $PSScriptRoot 'data\pending-agent-update.json') -Force -ErrorAction SilentlyContinue
    'Installed protected rescue service. No VPN was disconnected.' | Set-Content -LiteralPath $logPath -Encoding UTF8
} catch {
    $_.Exception.ToString() | Set-Content -LiteralPath $logPath -Encoding UTF8
    exit 1
}finally{if($nativeCoordinated){try{Invoke-AgentCoordinator 'end-operation'}catch{}};if($startupTransfer){foreach($held in $startupTransfer.Locks){$held.Dispose()}};if($setupReservation){$setupReservation.Dispose()}}
