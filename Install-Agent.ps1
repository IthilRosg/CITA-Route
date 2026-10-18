param([string]$UserSid,[string]$UserProfile)
$ErrorActionPreference = 'Stop'
if (-not $UserSid) { $UserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
if (-not $UserProfile) { $UserProfile = $env:USERPROFILE }
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $scriptLiteral = $PSCommandPath.Replace("'","''")
    $sidLiteral = $UserSid.Replace("'","''")
    $profileLiteral = $UserProfile.Replace("'","''")
    $command = "& '$scriptLiteral' -UserSid '$sidLiteral' -UserProfile '$profileLiteral'"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $child = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -PassThru -Wait -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded)
    exit $child.ExitCode
}
$logPath = Join-Path $PSScriptRoot 'logs\agent-install.log'
. (Join-Path $PSScriptRoot 'AgentPackage.ps1')
. (Join-Path $PSScriptRoot 'FileStorage.ps1')
. (Join-Path $PSScriptRoot 'ConnectionEnvironment.ps1')
try {
    if (Get-Process -Name 'smart-router','vpn-bridge' -ErrorAction SilentlyContinue) { throw 'Stop the current RoutePilot engine before updating its protected files.' }
    $build = Join-Path $PSScriptRoot 'runtime\agent-build\RoutePilot.Agent.exe'
    if (-not (Test-Path -LiteralPath $build)) { throw 'Build the rescue agent first.' }
    $installRoot = Join-Path $env:ProgramFiles 'RoutePilot\Agent'
    $dataRoot = Join-Path $env:ProgramData 'RoutePilot'
    foreach ($target in @((Split-Path $installRoot),$installRoot,$dataRoot)) {
        if (Test-Path -LiteralPath $target) {
            if ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'A protected installation path is a reparse point.' }
        }
    }
    $service = Get-Service -Name RoutePilotRescue -ErrorAction SilentlyContinue
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
    $config = @{UserSid=$UserSid;UserProfile=$UserProfile;DataDirectory=$dataRoot;AppRoot=$PSScriptRoot;WireSockSourceProfile=$connection.SourceProfile;WireSockCliPath=$wireSockCli;EndpointIp=$endpoint.Ip;EndpointPort=$endpoint.Port;SourceEndpoint=$endpoint.SourceEndpoint;ExpectedEgressIp=$connection.ExpectedEgressIp}
    $configSource = Join-Path $PSScriptRoot 'runtime\agent-install-config.json'
    [IO.File]::WriteAllText($configSource,($config | ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
    $files += @{Source=$configSource;Name='agent-config.json'},@{Source=(Join-Path $PSScriptRoot 'runtime\router.json');Name='production-router.json'}
    Publish-AgentPackage $files $installRoot {
        $current=Get-Service -Name RoutePilotRescue -ErrorAction SilentlyContinue
        if($current){Stop-Service -Name RoutePilotRescue; $current.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60))}
    } {
        if (-not (Get-Service -Name RoutePilotRescue -ErrorAction SilentlyContinue)) {
            New-Service -Name RoutePilotRescue -BinaryPathName ('"' + (Join-Path $installRoot 'RoutePilot.Agent.exe') + '" --service') -DisplayName 'RoutePilot Rescue' -StartupType Automatic | Out-Null
        }
        $configureResult = & (Join-Path $installRoot 'RoutePilot.Agent.exe') --configure-service-recovery 2>&1
        if ($LASTEXITCODE -ne 0) { throw ('Could not configure service restart recovery: ' + ($configureResult -join ' ')) }
        Start-Service -Name RoutePilotRescue
        (Get-Service -Name RoutePilotRescue).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
    }
    Remove-Item -LiteralPath (Join-Path $dataRoot 'guard-proof.json') -Force -ErrorAction SilentlyContinue
    'Installed protected rescue service. No VPN was disconnected.' | Set-Content -LiteralPath $logPath -Encoding UTF8
} catch {
    $_.Exception.ToString() | Set-Content -LiteralPath $logPath -Encoding UTF8
    exit 1
}
