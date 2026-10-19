[CmdletBinding()]
param([ValidateSet('Discover','Apply','Import','ReleaseConflicts','CompleteHandoff','RestoreHandoff')][string]$Action='Discover',
    [string]$SourceProfile='', [string]$CliPath='', [string]$ImportPath='', [string]$ExpectedEgressIp='', [string]$UserSid='', [string]$UserProfile='',
    [ValidatePattern('^([a-fA-F0-9]{32})?$')][string]$OperationId='')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$runtime=Join-Path $root 'runtime'
if(-not $UserSid){$UserSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value}
if(-not $UserProfile){$UserProfile=$env:USERPROFILE}
. (Join-Path $root 'FileStorage.ps1')
. (Join-Path $root 'ConnectionEnvironment.ps1')
. (Join-Path $root 'WireSockCli.ps1')
# Completion only retires this operation's local journal. Do not make a healthy
# new connection depend on another CLI/signature/network check at this point.
if($Action -eq 'CompleteHandoff' -or ($Action -eq 'RestoreHandoff' -and -not(Test-Path -LiteralPath (Join-Path $runtime 'connection-handoff.json')))){
    if(-not $OperationId){throw 'A connection handoff requires an operation identity.'}
    if($Action -eq 'CompleteHandoff'){Complete-ConnectionHandoff $root}
    exit 0
}
$settings=Get-ConnectionSettings $root
$cli=Find-WireSockCli $(if($CliPath){$CliPath}else{$settings.CliPath})
if($Action -eq 'Discover'){
    $profiles=@();$message='';$version=''
    if($cli){
        $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($cli).FileVersion
        try{$profiles=@(Get-WireSockProfileNames)}catch{$message='Не удалось прочитать профили. Проверь, что служба WireSock запущена.'}
    }else{$message='WireSock Secure Connect не найден. Установи его или укажи CLI вручную.'}
    @{CliPath=$cli;Version=$version;Profiles=$profiles;SelectedProfile=$settings.SourceProfile;ExpectedEgressIp=$settings.ExpectedEgressIp;Message=$message} | ConvertTo-Json -Compress
    exit 0
}
if(-not $cli){throw 'WireSock Secure Connect CLI was not found.'}
$isAdmin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if(-not $isAdmin){throw 'Connection setup requires elevation.'}
Assert-ProtectedWireSockCli $cli
if($Action -in @('ReleaseConflicts','CompleteHandoff','RestoreHandoff')){
    if(-not $OperationId){throw 'A connection handoff requires an operation identity.'}
    . (Join-Path $root 'AgentLease.ps1')
    switch($Action){
        'ReleaseConflicts' {Release-ConnectionConflicts $root}
        'CompleteHandoff' {Complete-ConnectionHandoff $root}
        'RestoreHandoff' {Restore-ConnectionHandoff $root}
    }
    exit 0
}
New-Item -ItemType Directory -Path (Join-Path $root 'data'),(Join-Path $root 'runtime'),(Join-Path $root 'logs') -Force | Out-Null
$setupLock=$null
try{
    $setupLock=[IO.File]::Open((Join-Path $root 'runtime\setup.lock'),'OpenOrCreate','ReadWrite','None')
    Write-ConnectionProgress 'Preparing'
    $sessionPath=Join-Path $root 'runtime\session.json'
    if((Test-Path -LiteralPath $sessionPath) -and (Read-JsonFile $sessionPath).running){throw 'Disconnect RoutePilot before selecting another connection.'}
    if(Get-Process -Name 'smart-router','vpn-bridge' -ErrorAction SilentlyContinue){throw 'Stop the current RoutePilot engine before setup.'}
    . (Join-Path $root 'AgentLease.ps1')
    Assert-NoAgentRun
    $profiles=@(Get-WireSockProfileNames)
    if($Action -eq 'Import'){
        if([IO.Path]::GetExtension($ImportPath) -ine '.conf' -or -not(Test-Path -LiteralPath $ImportPath -PathType Leaf)){throw 'Select a .conf file.'}
        $name=[IO.Path]::GetFileNameWithoutExtension($ImportPath)
        if($name -eq 'SmartRouting-AWG2' -or $name -like 'RoutePilot-Probe-*' -or $name -notmatch '^[\p{L}\p{N} _.-]{1,100}$'){throw 'Use a simple, non-reserved profile filename.'}
        if($profiles -contains $name){throw 'A profile with this name already exists. Select it in the profile list instead.'}
        $info=Get-Item -LiteralPath $ImportPath
        if($info.Length -gt 131072){throw 'Configuration file is too large.'}
        $null=Get-ProfileEndpoint ([IO.File]::ReadAllText($ImportPath))
        Invoke-WireSock @('import',[IO.Path]::GetFullPath($ImportPath)) | Out-Null
        exit 0
    }
    if(-not $SourceProfile -or $profiles -notcontains $SourceProfile){throw 'Select an existing WireSock source profile.'}
    if($ExpectedEgressIp){
        $egress=$null
        if(-not [Net.IPAddress]::TryParse($ExpectedEgressIp,[ref]$egress) -or $egress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork){throw 'Expected egress must be an IPv4 address, or leave it empty for profile verification.'}
        $ExpectedEgressIp=$egress.ToString()
    }
    $settingsPath=Join-Path $root 'data\connection.json'
    $old=if(Test-Path -LiteralPath $settingsPath){[IO.File]::ReadAllText($settingsPath)}else{$null}
    try{
        Assert-ConnectionOperation
        Write-JsonFile $settingsPath @{Version=1;SourceProfile=$SourceProfile;CliPath=$cli;ExpectedEgressIp=$ExpectedEgressIp;UserSid=$UserSid}
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'SmartRouting.ps1') -Action Prepare -Setup
        if($LASTEXITCODE -ne 0){throw 'Connection preparation failed. Check WireSock transparent mode and the selected profile.'}
        Write-ConnectionProgress 'Installing'
        # Run separately: a failed installer must not exit this transaction before rollback.
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'Install-Agent.ps1') -UserSid $UserSid -UserProfile $UserProfile
        if($LASTEXITCODE -ne 0){throw 'Protected service installation failed. See logs/agent-install.log.'}
    }catch{
        if($null -ne $old){[IO.File]::WriteAllText($settingsPath,$old,(New-Object Text.UTF8Encoding($false)))}else{Remove-Item -LiteralPath $settingsPath -Force -ErrorAction SilentlyContinue}
        throw
    }
    Write-Output 'Connection saved. The source WireSock profile was not modified. Connect when ready.'
}finally{if($setupLock){$setupLock.Dispose()}}
