[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
# This test installs a real SYSTEM service with a synthetic filesystem-only
# transport. It must never run on a user's existing installation.
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted'){throw 'Startup service integration requires a disposable GitHub-hosted Windows runner.'}
$root=$PSScriptRoot
$protected=Join-Path $env:ProgramFiles 'RoutePilot\Agent'
$shared=Join-Path $env:ProgramData 'RoutePilot'
$serviceName='RoutePilotRescue'
if((Test-Path -LiteralPath $protected) -or (Test-Path -LiteralPath $shared) -or (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)){throw 'A protected installation already exists. Refusing to replace it.'}
$base=Join-Path $root ('runtime\startup-service-fixture-'+[guid]::NewGuid().ToString('N'))
$client=Join-Path $base 'Клиент с пробелами\client'
$agent=Join-Path $protected 'RoutePilot.Agent.exe'
$bootClient=Join-Path $protected 'startup-client'
$stateFile=Join-Path $shared 'startup-state.json'
$created=$false
function Assert($Value,[string]$Message){if(-not $Value){throw $Message}}
function ReadState{[IO.File]::ReadAllText($stateFile) | ConvertFrom-Json}
function WaitPhase([string]$Phase){
    $wait=[Diagnostics.Stopwatch]::StartNew()
    while($wait.Elapsed.TotalSeconds -lt 35){if((Test-Path -LiteralPath $stateFile) -and (ReadState).Phase -eq $Phase){return};Start-Sleep -Milliseconds 200}
    throw ('Startup phase was not reached: '+$Phase+'; observed '+(ReadState).Phase)
}
function NewBoot{
    $state=ReadState;$state.BootUtc=[datetime]::UtcNow.AddDays(-1).ToString('o')
    [IO.File]::WriteAllText($stateFile,($state|ConvertTo-Json))
}
function StopFixtureService{
    $service=Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if($service){Stop-Service -Name $serviceName -ErrorAction Stop;$service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20))}
}
try{
    foreach($directory in @($protected,$shared,$client,(Join-Path $client 'runtime'),(Join-Path $client 'data'),(Join-Path $client 'private'),(Join-Path $client 'logs'),(Join-Path $client 'rules'))){New-Item -ItemType Directory -Path $directory -Force|Out-Null}
    # Match production protection, without altering any existing folder.
    foreach($directory in @($protected,$shared)){
        $acl=New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true,$false)
        foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl','ContainerInherit,ObjectInherit','None','Allow')))}
        Set-Acl -LiteralPath $directory -AclObject $acl
    }
    Copy-Item -LiteralPath (Join-Path $root 'runtime\agent-build\RoutePilot.Agent.exe') -Destination $agent
    Copy-Item -LiteralPath (Join-Path $root 'runtime\smart-router.exe') -Destination (Join-Path $protected 'smart-router.exe')
    & dotnet publish (Join-Path $root 'tools\startup-cli-fixture\Cita.StartupCliFixture.csproj') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o (Join-Path $protected 'fixture-cli')
    if($LASTEXITCODE -ne 0){throw 'Startup CLI fixture did not build.'}
    $cli=Join-Path $protected 'fixture-cli\wiresock-connect-cli.exe'
    $ready=Join-Path $protected 'fixture-cli\fixture-ready.txt'
    [IO.File]::WriteAllText($ready,'ready')
    foreach($script in @('SmartRouting.ps1','FileStorage.ps1','ConnectionEnvironment.ps1','RuleFeeds.ps1','WireSockCli.ps1','WireSockProfiles.ps1','WireSockRecovery.ps1','AgentLease.ps1','SessionRecovery.ps1')){Copy-Item -LiteralPath (Join-Path $root $script) -Destination (Join-Path $client $script)}
    foreach($directory in @('rules','runtime\rules')){
        $destination=Join-Path $client $directory;New-Item -ItemType Directory -Path $destination -Force|Out-Null
        foreach($file in Get-ChildItem -LiteralPath (Join-Path $root $directory) -File | Where-Object Extension -in '.json','.srs'){Copy-Item -LiteralPath $file.FullName -Destination $destination}
    }
    Copy-Item -LiteralPath (Join-Path $root 'rules\telegram-cidr.txt') -Destination (Join-Path $client 'rules\telegram-cidr.txt')
    Copy-Item -LiteralPath (Join-Path $root 'product.json') -Destination (Join-Path $client 'product.json')
    [IO.File]::WriteAllText((Join-Path $client 'force-direct.txt'),'')
    [IO.File]::WriteAllText((Join-Path $client 'force-vpn.txt'),'')
    [IO.File]::WriteAllText((Join-Path $client 'private\controller-token.txt'),('0'*64))
    [IO.File]::WriteAllText((Join-Path $client 'runtime\session.json'),'{"running":false}')
    [IO.File]::WriteAllText((Join-Path $client 'data\routing-policy.json'),'{"Mode":"Auto","AllowListedDpi":false}')
    # This replacement cannot create a TUN, socket, route or WireSock connection.
    [IO.File]::WriteAllText((Join-Path $client 'SmartRouting.ps1'),@'
param([string]$Action)
$ErrorActionPreference='Stop'
$runtime=Join-Path $PSScriptRoot 'runtime'
$proofFile=Join-Path $PSScriptRoot 'logs\startup-proof.json'
$proof=@{Starts=0;Stops=0}
if(Test-Path -LiteralPath $proofFile){$value=Get-Content -LiteralPath $proofFile -Raw|ConvertFrom-Json;$proof.Starts=$value.Starts;$proof.Stops=$value.Stops}
$proof.System=([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -eq 'S-1-5-18')
$proof.SessionZero=([Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0)
if($Action -eq 'Start'){
    $proof.Starts++
    $id=[guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText((Join-Path $runtime 'session.json'),(@{id=$id;running=$true;bridgeReady=$true;bridgeGeneration=1}|ConvertTo-Json))
    [IO.File]::WriteAllText((Join-Path $runtime 'session-health.json'),(@{sessionId=$id;bridgeGeneration=1;healthy=$true;updatedUtc=[datetime]::UtcNow.ToString('o')}|ConvertTo-Json))
}elseif($Action -eq 'Stop'){$proof.Stops++;[IO.File]::WriteAllText((Join-Path $runtime 'session.json'),'{"running":false}')}else{exit 2}
[IO.File]::WriteAllText($proofFile,($proof|ConvertTo-Json))
'@,(New-Object Text.UTF8Encoding($true)))
    $config=@{UserSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;UserProfile=$env:USERPROFILE;DataDirectory=$shared;AppRoot=$client;WireSockSourceProfile='fixture';WireSockCliPath=$cli;SourceEndpoint='example.invalid:1';ExpectedEgressIp='203.0.113.1';EndpointIp='203.0.113.1';EndpointPort=1}
    [IO.File]::WriteAllText((Join-Path $protected 'agent-config.json'),($config|ConvertTo-Json))
    & $agent --configure-boot $client on
    Assert ($LASTEXITCODE -eq 0) 'Protected startup snapshot could not be configured.'
    Assert ((ReadState).Phase -eq 'ReadyForBoot') 'Enabling startup must defer until the next boot.'
    Assert (-not(Test-Path -LiteralPath (Join-Path $bootClient 'private\fixture.conf'))) 'Startup snapshot copied a VPN configuration.'
    New-Service -Name $serviceName -BinaryPathName ('"'+$agent+'" --service') -StartupType Automatic|Out-Null;$created=$true
    Start-Service -Name $serviceName
    Start-Sleep -Seconds 2
    Assert ((ReadState).Phase -eq 'ReadyForBoot') 'Enabling startup changed the current connection.'
    StopFixtureService;NewBoot;Start-Service -Name $serviceName;WaitPhase 'Connected'
    $proofFile=Join-Path $bootClient 'logs\startup-proof.json'
    $proof=Get-Content -LiteralPath $proofFile -Raw|ConvertFrom-Json
    Assert ($proof.System -and $proof.SessionZero -and $proof.Starts -eq 1) 'Startup did not execute as SYSTEM in session 0, exactly once.'
    & $agent --configure-boot $client on
    Assert ($LASTEXITCODE -ne 0) 'Active startup snapshot was overwritten.'
    & $agent boot-stop $client|Out-Null
    Assert ($LASTEXITCODE -eq 0) 'Startup stop request failed.'
    WaitPhase 'StoppedForBoot'
    StopFixtureService;Start-Service -Name $serviceName;Start-Sleep -Seconds 2
    Assert ((ReadState).Phase -eq 'StoppedForBoot') 'Service restart undid intentional manual Stop.'
    # A cancel while waiting must not invoke disconnect or disturb any VPN.
    StopFixtureService;Remove-Item -LiteralPath $ready;NewBoot;Start-Service -Name $serviceName;WaitPhase 'WaitingForNetwork'
    & $agent boot-stop $client|Out-Null;WaitPhase 'StoppedForBoot'
    $proof=Get-Content -LiteralPath $proofFile -Raw|ConvertFrom-Json
    Assert ($proof.Starts -eq 1 -and $proof.Stops -eq 1) 'Cancelling network readiness touched the transport.'
    # A client update invalidates the old protected startup snapshot.
    StopFixtureService;[IO.File]::WriteAllText((Join-Path $client 'product.json'),'{"version":"99.0.0"}');NewBoot
    Start-Service -Name $serviceName;WaitPhase 'NeedsAttention'
    & $agent boot-stop $client|Out-Null;WaitPhase 'StoppedForBoot'
    & $agent boot-disable $client|Out-Null
    Assert ($LASTEXITCODE -eq 0 -and -not (ReadState).Enabled) 'Uninstall could not disable the inactive startup setting.'
    StopFixtureService;NewBoot;Start-Service -Name $serviceName;Start-Sleep -Seconds 2
    Assert ((ReadState).Phase -eq 'Disabled') 'Disabled startup connected after a new boot.'
    Write-Output 'PASS: real SYSTEM/session-0 service, protected snapshot, delayed activation, one startup, disconnect, no same-boot reconnect, waiting cancellation, update invalidation and disable. Synthetic transport only; no VPN/driver/network mutation.'
}finally{
    if($created){StopFixtureService;& sc.exe delete $serviceName|Out-Null}
    foreach($target in @($protected,$shared,$base)){
        $absolute=[IO.Path]::GetFullPath($target)
        if($absolute -ne [IO.Path]::GetFullPath($protected) -and $absolute -ne [IO.Path]::GetFullPath($shared) -and -not $absolute.StartsWith([IO.Path]::GetFullPath((Join-Path $root 'runtime\startup-service-fixture-')),[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe startup fixture cleanup.'}
        if(Test-Path -LiteralPath $absolute){Remove-Item -LiteralPath $absolute -Recurse -Force}
    }
}
