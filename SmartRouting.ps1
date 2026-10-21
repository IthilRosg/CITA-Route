[CmdletBinding()]
param(
    [ValidateSet('Prepare','ProxyOnly','Start','Stop','Status','Watch','UpdateRules','SyncProfile','Reconnect','RepairVpn')]
    [string]$Action = 'Start',
    [string]$ExpectedSessionId = '',
    [ValidatePattern('^([a-fA-F0-9]{32})?$')][string]$OperationId = '',
    [switch]$Setup
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$runtime = Join-Path $root 'runtime'
$private = Join-Path $root 'private'
$cli = ''
$sourceProfile = ''
$smartProfile = 'SmartRouting-AWG2'
$routerExe = Join-Path $runtime 'smart-router.exe'
$bridgeExe = Join-Path $env:ProgramFiles 'RoutePilot\Agent\vpn-bridge.exe'
$utf8 = New-Object System.Text.UTF8Encoding($false)

$setupProbe=$null
try {
. (Join-Path $root 'FileStorage.ps1')
if($Action -in @('Start','ProxyOnly')){
    $updateGate=$null
    try{$updateGate=[IO.File]::Open((Join-Path $runtime 'update-preparation.lock'),'OpenOrCreate','ReadWrite','None')}
    catch{throw 'Update preparation is in progress.'}
    finally{if($updateGate){$updateGate.Dispose()}}
}
. (Join-Path $root 'ConnectionEnvironment.ps1')
$startupRoute=Get-StartupRouteState $root
if($startupRoute -and $Action -notin @('Status','Watch')){
    if($Action -ne 'Stop'){throw 'Disconnect the startup route before configuring or starting another route.'}
    $startupAgent=Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe'
    & $startupAgent boot-stop $root | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Startup route stop was refused.'}
    $startupWait=[Diagnostics.Stopwatch]::StartNew()
    while($startupRoute){
        if($startupWait.Elapsed.TotalSeconds -gt 145 -or $startupRoute.Phase -eq 'StopFailed'){throw 'Startup disconnect was not confirmed. Check CITA Route and WireSock.'}
        Start-Sleep -Milliseconds 200
        $startupRoute=Get-StartupRouteState $root
    }
    exit
}
$connectionSettings=Get-ConnectionSettings $root
$cli=Find-WireSockCli $connectionSettings.CliPath
$sourceProfile=$connectionSettings.SourceProfile
if($Setup -and $Action -ne 'Prepare'){throw 'Setup is valid only for offline preparation.'}
if(-not $Setup -and $Action -notin @('Status','Stop','Watch')){
    $setupProbe=$null
    try{$setupProbe=[IO.File]::Open((Join-Path $runtime 'setup.lock'),'OpenOrCreate','ReadWrite','None')}
    catch{throw 'Connection setup is in progress. Wait for it to finish.'}
    # Retain this handle for the whole action: a check followed by an immediate
    # release lets Apply/import/agent replacement race the startup or repair.
}
. (Join-Path $root 'RuleFeeds.ps1')
. (Join-Path $root 'WireSockCli.ps1')
. (Join-Path $root 'WireSockProfiles.ps1')
. (Join-Path $root 'WireSockRecovery.ps1')
. (Join-Path $root 'AgentLease.ps1')
. (Join-Path $root 'SessionRecovery.ps1')
if ($Action -notin @('Status','Watch')) { Assert-NoAgentRun }

function Get-OurProcesses {
    @(Get-Process -Name 'smart-router','vpn-bridge' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $routerExe -or $_.Path -eq $bridgeExe })
}

function Test-WireSockConnected {
    return (Get-WireSockObservation).state -eq 'Connected'
}

function Connect-WireSock {
    $connectLog = Join-Path $root 'logs\wiresock-connect.log'
    $connectErrorLog = Join-Path $root 'logs\wiresock-connect-error.log'
    $connector = Start-Process -FilePath $cli -WindowStyle Hidden -PassThru `
        -ArgumentList @('connect',$smartProfile,'-network-lock','disabled','-log-level','warning','-exit') `
        -RedirectStandardOutput $connectLog -RedirectStandardError $connectErrorLog
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(35)
        while ([DateTime]::UtcNow -lt $deadline) {
            Assert-ConnectionOperation
            if(Test-Path -LiteralPath (Join-Path $runtime 'stop.request')){throw 'Connection cancelled by Stop.'}
            $observation=Get-WireSockObservation
            if($observation.state -eq 'Connected'){return}
            if($observation.state -eq 'Foreign'){throw 'Another WireSock profile is in use; connection paused.'}
            Start-Sleep -Seconds 1
        }
        throw 'WireSock did not complete the handshake within 35 seconds. Disconnect the temporary VPN and check the original AWG2 connection.'
    } finally {
        if (-not $connector.HasExited) { Stop-Process -Id $connector.Id -Force -ErrorAction SilentlyContinue }
    }
}

function Stop-OurProcesses([switch]$KeepWatch) {
    $policyPath = Join-Path $runtime 'policy-process.json'
    if (Test-Path -LiteralPath $policyPath) {
        $policyIdentity = [IO.File]::ReadAllText($policyPath) | ConvertFrom-Json
        $policyProcess = Get-Process -Id $policyIdentity.Pid -ErrorAction SilentlyContinue
        if ($policyProcess -and $policyProcess.Path -eq $policyIdentity.Path -and
            [Math]::Abs(($policyProcess.StartTime.ToUniversalTime() - [DateTime]::Parse($policyIdentity.StartedUtc).ToUniversalTime()).TotalMilliseconds) -lt 100) {
            $policyProcess | Stop-Process -Force
        }
    }
    $watchPath = Join-Path $runtime 'watch.pid'
    if (-not $KeepWatch -and (Test-Path -LiteralPath $watchPath)) {
        $watchId = [int]([IO.File]::ReadAllText($watchPath).Trim())
        $watchProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $watchId" -ErrorAction SilentlyContinue
        if ($watchId -ne $PID -and $watchProcess.CommandLine -like ('*' + $root + '*') -and
            $watchProcess.CommandLine -match '-Action\s+Watch') {
            Stop-Process -Id $watchId -Force -ErrorAction SilentlyContinue
        }
        Remove-Item -LiteralPath $watchPath -Force
    }
    $helpers=@(Get-OurProcesses)
    $helpers | Stop-Process -Force
    foreach($helper in $helpers){if(-not $helper.WaitForExit(5000)){throw 'A routing helper has not finished stopping.'}}
    # Closing Wintun removes the temporary routes and interface DNS settings.
    Start-Sleep -Milliseconds 500
    Clear-DnsClientCache -ErrorAction SilentlyContinue
}

function Get-Domains([string]$FileName) {
    $path = Join-Path $root $FileName
    $domains = @()
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        $domain = ($line -split '#',2)[0].Trim().TrimEnd('.').ToLowerInvariant()
        if (-not $domain) { continue }
        $domain = (New-Object Globalization.IdnMapping).GetAscii($domain)
        if ($domain -notmatch '^(?=.{1,253}$)([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9-]{2,63}$') {
            throw "Invalid domain in ${FileName}: $domain (enter a hostname, without https:// or a path)."
        }
        $domains += $domain
    }
    return @($domains | Sort-Object -Unique)
}

function Update-RuleFeeds {
    $lockPath = Join-Path $runtime 'rules-update.lock'
    try { $feedLock = [IO.File]::Open($lockPath,'OpenOrCreate','ReadWrite','None') }
    catch { throw 'The rule lists are already being updated.' }
    $staged = @()
    $changes = @()
    try {
        foreach ($feedName in @('russia_inside','discord')) {
            $stagedPath = Join-Path $runtime ('rules\' + $feedName + '.new.srs')
            $stagedJson = Join-Path $runtime ('rules\' + $feedName + '.new.json')
            $staged += @{name=$feedName;binary=$stagedPath;json=$stagedJson}
            $changes += @{Staged=$stagedPath;Target=(Join-Path $runtime ('rules\' + $feedName + '.srs'))}
            $changes += @{Staged=$stagedJson;Target=(Join-Path $runtime ('rules\' + $feedName + '.json'))}
            Invoke-WebRequest -UseBasicParsing -Uri (
                'https://github.com/itdoginfo/allow-domains/releases/latest/download/' + $feedName + '.srs') `
                -Proxy 'http://127.0.0.1:20808' -TimeoutSec 20 -OutFile $stagedPath
            & $routerExe rule-set decompile $stagedPath -o $stagedJson
            if ($LASTEXITCODE -ne 0) { throw "Invalid downloaded rule list: $feedName" }
        }
        $telegramStaged = Join-Path $runtime 'rules\telegram.new.json'
        $changes += @{Staged=$telegramStaged;Target=(Join-Path $runtime 'rules\telegram.json')}
        $telegramResponse = Invoke-WebRequest -UseBasicParsing -Uri 'https://core.telegram.org/resources/cidr.txt' `
            -Proxy 'http://127.0.0.1:20808' -TimeoutSec 20
        Write-TelegramRules ([string]$telegramResponse.Content) $telegramStaged
        Publish-RuleFiles $changes
        Write-JsonFile (Join-Path $runtime 'feeds-state.json') @{
            success=$true;updatedUtc=[DateTime]::UtcNow.ToString('o');message='Rule lists updated.'
        }
        Write-Host 'Rule lists updated and applied to new connections.'
        Remove-Item -LiteralPath (Join-Path $runtime 'feeds-error.json') -Force -ErrorAction SilentlyContinue
    } catch {
        Write-JsonFile (Join-Path $runtime 'feeds-error.json') @{
            failedUtc=[DateTime]::UtcNow.ToString('o');message=$_.Exception.Message
        }
        throw
    } finally {
        foreach ($item in $staged) {
            Remove-Item -LiteralPath $item.binary,$item.json -Force -ErrorAction SilentlyContinue
        }
        if ($telegramStaged) { Remove-Item -LiteralPath $telegramStaged -Force -ErrorAction SilentlyContinue }
        $feedLock.Dispose()
    }
}

function Prepare-Configuration {
    New-Item -ItemType Directory -Path $runtime,$private,(Join-Path $root 'logs') -Force | Out-Null
    if (-not $sourceProfile -or -not $cli) { throw 'Select a WireSock profile in RoutePilot Settings first.' }
    if (-not (Test-Path -LiteralPath $routerExe) -or (-not (Test-Path -LiteralPath $bridgeExe) -and -not $Setup)) {
        throw 'Install or update the RoutePilot rescue service first: the protected VPN bridge is missing.'
    }
    Protect-PrivateDirectory $private $connectionSettings.UserSid
    $physical = @(Get-NetIPConfiguration | Where-Object {
        $_.NetAdapter.HardwareInterface -and $_.NetAdapter.Status -eq 'Up' -and $_.IPv4DefaultGateway
    } | Sort-Object { $_.NetIPv4Interface.InterfaceMetric }) | Select-Object -First 1
    if (-not $physical) { throw 'No active physical network adapter with an IPv4 gateway was found.' }
    $wireSockSettingsPath = Join-Path $env:ProgramData 'WireSock Foundation\WireSock Secure Connect\wiresock.config'
    if (Test-Path -LiteralPath $wireSockSettingsPath) {
        $globalSettings = [xml][IO.File]::ReadAllText($wireSockSettingsPath)
        $overrideSplit = $globalSettings.SelectSingleNode('//OverrideSplitTunnelingSettings')
        if ($overrideSplit -and $overrideSplit.InnerText -eq 'True') {
            throw 'Disable WireSock setting Override profile split-tunneling settings with global ones first.'
        }
        $virtualMode = $globalSettings.SelectSingleNode('//VirtualAdapterMode')
        if ($virtualMode -and $virtualMode.InnerText -eq 'True') {
            throw 'This setup requires WireSock transparent mode. Disable Virtual Adapter Mode first.'
        }
    }
    $interface = $physical.InterfaceAlias
    $tailInterface='Tailscale'
    $tailAdapter=@(Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object InterfaceDescription -match '^Tailscale(?: Tunnel(?: #\d+)?)?$' | Select-Object -First 1)
    if($tailAdapter.Count){$tailInterface=[string]$tailAdapter[0].Name}
    $hasDirectIPv6 = [bool]$physical.IPv6DefaultGateway
    $directDnsStrategy = if ($hasDirectIPv6) { 'prefer_ipv4' } else { 'ipv4_only' }
    $originalPath = Join-Path $private 'awg2.original.conf'
    if (-not (Test-Path -LiteralPath $originalPath)) {
        Invoke-WireSock @('export',$sourceProfile,$originalPath) | Out-Null
    }
    $currentSourcePath = Join-Path $private ('source-check-' + [guid]::NewGuid().ToString('N') + '.conf')
    try {
        Invoke-WireSock @('export',$sourceProfile,$currentSourcePath) | Out-Null
        $original = [IO.File]::ReadAllText($currentSourcePath)
    } finally { Remove-Item -LiteralPath $currentSourcePath -Force -ErrorAction SilentlyContinue }
    if ([regex]::Matches($original,'(?im)^\s*\[Peer\]\s*$').Count -ne 1) {
        throw 'Expected exactly one WireSock peer.'
    }
    $protectedConfigPath=Join-Path $env:ProgramFiles 'RoutePilot\Agent\agent-config.json'
    $protectedConfig=$null
    if(-not $Setup -and (Test-Path -LiteralPath $protectedConfigPath)){$protectedConfig=Read-JsonFile $protectedConfigPath}
    $endpoint=Get-ProfileEndpoint $original $(if($protectedConfig){$protectedConfig.EndpointIp}else{''})
    if(-not $Setup -and (Test-Path -LiteralPath $protectedConfigPath)){
        $protectedSource=if($protectedConfig.SourceEndpoint){$protectedConfig.SourceEndpoint}else{$protectedConfig.EndpointIp+':'+$protectedConfig.EndpointPort}
        if($protectedConfig.WireSockSourceProfile -ne $sourceProfile -or $protectedSource -ine $endpoint.SourceEndpoint){throw 'Source profile changed. Apply the connection in RoutePilot Settings before connecting.'}
        # Keep the installed IPv4 pin until an explicit setup refreshes both route and service.
        $endpoint.Ip=$protectedConfig.EndpointIp
    }
    $endpoint | Add-Member -NotePropertyName SourceProfile -NotePropertyValue $sourceProfile -Force
    Write-JsonFile (Join-Path $runtime 'connection-endpoint.json') $endpoint
    $profileLines = New-Object 'System.Collections.Generic.List[string]'
    foreach ($line in ($original -split '\r?\n')) {
        # DNS must stay under the router's control; only the bridge uses WireSock.
        if ($line -match '^\s*(?:#@ws:\s*)?(DNS|AllowedIPs|AllowedApps|DisallowedApps|DisallowedIPs)\s*=') { continue }
        if ($line -match '^\s*Endpoint\s*=') { $profileLines.Add('Endpoint = '+$endpoint.Ip+':'+$endpoint.Port); continue }
        $profileLines.Add($line)
        if ($line -match '^\s*\[Peer\]\s*$') {
            $profileLines.Add('AllowedIPs = 0.0.0.0/0, ::/0')
            $profileLines.Add('#@ws:AllowedApps = ' + $bridgeExe)
            $profileLines.Add('#@ws:DisallowedApps = ' + $routerExe)
        }
    }
    [IO.File]::WriteAllText((Join-Path $private ($smartProfile + '.conf')), ($profileLines -join "`r`n"), $utf8)

    $directDomains = @(Get-Domains 'force-direct.txt')
    $vpnDomains = @(Get-Domains 'force-vpn.txt')
    foreach ($directDomain in $directDomains) {
        foreach ($vpnDomain in $vpnDomains) {
            if ($directDomain -eq $vpnDomain -or $directDomain.EndsWith('.'+$vpnDomain) -or $vpnDomain.EndsWith('.'+$directDomain)) {
                throw "Conflicting explicit rules: $directDomain / $vpnDomain"
            }
        }
    }
    foreach ($personalRule in @(@{tag='personal-direct';domains=$directDomains},@{tag='personal-vpn';domains=$vpnDomains})) {
        $personalRules = @()
        if ($personalRule.domains.Count) { $personalRules = @(@{domain_suffix=@($personalRule.domains)}) }
        Write-JsonFile (Join-Path $runtime ('rules\' + $personalRule.tag + '.json')) @{version=3;rules=$personalRules}
    }
    $telegramRulePath = Join-Path $runtime 'rules\telegram.json'
    if (-not (Test-Path -LiteralPath $telegramRulePath)) {
        Write-TelegramRules ([IO.File]::ReadAllText((Join-Path $root 'rules\telegram-cidr.txt'))) $telegramRulePath
    }
    $controllerTokenPath = Join-Path $private 'controller-token.txt'
    if (-not (Test-Path -LiteralPath $controllerTokenPath)) {
        [IO.File]::WriteAllText($controllerTokenPath,([guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')),$utf8)
    }
    $controllerToken = [IO.File]::ReadAllText($controllerTokenPath).Trim()
    $routeRules = @(
        # Preserve the overlay's own control/DERP transport before sniffing and
        # adaptive routing. Tailnet destinations keep their OS-specific routes.
        @{ process_name=@('tailscaled.exe','tailscale.exe','tailscale-ipn.exe'); action='route'; outbound='direct' },
        @{ action='sniff'; timeout='300ms' },
        @{ protocol='dns'; action='hijack-dns' },
        @{ process_name=@('wiresock-connect-service.exe','wiresock-connect-app-service.exe'); action='route'; outbound='direct' }
    )
    # Existing third-party hosts redirects can point OpenAI at a regional proxy.
    # Restore only the exact destination named by TLS/HTTP; leave the hosts file intact.
    $hostsPath = Join-Path $env:windir 'System32\drivers\etc\hosts'
    foreach ($line in [IO.File]::ReadAllLines($hostsPath)) {
        $parts = (($line -split '#',2)[0].Trim() -split '\s+')
        if ($parts.Count -lt 2) { continue }
        foreach ($hostName in $parts[1..($parts.Count-1)]) {
            if ($hostName -match '^(?:[a-z0-9-]+\.)*(?:openai\.com|chatgpt\.com|oaistatic\.com|oaiusercontent\.com|sora\.com)$') {
                $routeRules += @{domain=@($hostName);action='route-options';override_address=$hostName}
            }
        }
    }
    $routeRules += @{inbound=@('direct-probe');action='route';outbound='direct'}
    $routeRules += @{inbound=@('dpi-probe');network='tcp';action='route';outbound='dpi';tls_record_fragment=$true}
    # Explicit proxy names and overlay addresses must not enter FakeIP,
    # physical-interface binding or adaptive DPI/VPN classification.
    $routeRules += @{domain_suffix=@('ts.net');action='resolve';server='dns-tailnet';strategy='prefer_ipv4'}
    $routeRules += @{ip_cidr=@('100.64.0.0/10','fd7a:115c:a1e0::/48');action='route';outbound='tailnet'}
    $routeRules += @{rule_set=@('personal-direct');action='route';outbound='direct'}
    $routeRules += @{rule_set=@('personal-vpn');action='route';outbound='vpn'}
    $routeRules += @{domain_suffix=@('openai.com','chatgpt.com','oaistatic.com','oaiusercontent.com','sora.com');action='route';outbound='vpn'}
    $policyOptionsPath = Join-Path $root 'data\routing-policy.json'
    if (-not (Test-Path -LiteralPath $policyOptionsPath)) { Write-JsonFile $policyOptionsPath @{Mode='Auto';AllowListedDpi=$false} }
    $policyOptions = [IO.File]::ReadAllText($policyOptionsPath) | ConvertFrom-Json
    if ($policyOptions.Mode -notin @('Lists','Auto')) { throw 'Unknown routing policy mode.' }
    foreach ($autoRoute in @('direct','dpi','vpn','blocked')) {
        Write-JsonFile (Join-Path $runtime ('rules\auto-' + $autoRoute + '.json')) @{version=3;rules=@()}
    }
    # Generation canaries use ONLY this dedicated loopback inbound/outbound.
    # A missing canary is rejected; it can never fall through to a WAN route.
    $ackRules = @()
    foreach ($autoRoute in @('direct','dpi','vpn','blocked')) {
        $ackRules += @{type='logical';mode='and';rules=@(@{inbound=@('policy-ack')},@{rule_set=@('auto-'+$autoRoute)});action='route';outbound='policy-proof';override_address='127.0.0.1';override_port=20831}
    }
    $ackRules += @{inbound=@('policy-ack');action='reject'}
    $routeRules = $ackRules + $routeRules
    $dnsRules = @(
        @{domain_suffix=@('ts.net');action='route';server='dns-tailnet'},
        @{rule_set=@('personal-direct');action='route';server='dns-direct'},
        @{rule_set=@('personal-vpn');query_type=@('A','AAAA');action='route';server='fakeip'},
        @{rule_set=@('personal-vpn');action='route';server='dns-vpn'}
    )
    if ($policyOptions.Mode -eq 'Lists' -or -not $policyOptions.AllowListedDpi) {
        $routeRules += @{ rule_set=@('russia-inside','discord','telegram'); action='route'; outbound='vpn' }
    } else {
        $routeRules += @{ rule_set=@('telegram');action='route';outbound='vpn' }
        $routeRules += @{ rule_set=@('russia-inside','discord');network='udp';action='route';outbound='vpn' }
    }
    $routeRules += @{type='logical';mode='and';rules=@(@{rule_set=@('auto-blocked')},@{ip_version=6;invert=$true});action='reject'}
    foreach($autoRoute in @('direct','vpn','dpi')){
        # A CONNECT/SOCKS hostname initially has IP version 0. Resolve ONLY
        # these unknown-family flows to A before applying the IPv4 decision.
        # Original IPv6 flows keep their independent routing policy.
        $unknownFamily=@{type='logical';mode='and';rules=@(@{ip_version=4;invert=$true},@{ip_version=6;invert=$true})}
        $routeRules += @{type='logical';mode='and';rules=@(@{rule_set=@('auto-'+$autoRoute)},$unknownFamily);action='resolve';server=$(if($autoRoute -eq 'vpn'){'dns-vpn'}else{'dns-direct'});strategy='ipv4_only'}
        $adaptiveRule=@{type='logical';mode='and';rules=@(@{rule_set=@('auto-'+$autoRoute)},@{ip_version=6;invert=$true});action='route';outbound=$autoRoute}
        if($autoRoute -eq 'dpi'){$adaptiveRule.tls_record_fragment=$true}
        $routeRules += $adaptiveRule
    }
    if (-not $hasDirectIPv6) { $routeRules += @{ip_version=6;action='reject'} }
    $routeRules += @{ ip_cidr=@('198.18.0.0/15','fc00::/18'); action='reject' }
    $routeRules += @{ ip_is_private=$true; action='route'; outbound='direct' }
    $dnsRules += @{ rule_set=@('russia-inside','discord'); query_type=@('A','AAAA'); action='route'; server='fakeip' }
    $dnsRules += @{ rule_set=@('russia-inside','discord'); action='route'; server='dns-vpn' }
    if ($policyOptions.Mode -eq 'Auto') {
        $dnsRules += @{domain_suffix=@('local','lan','home.arpa');action='route';server='dns-direct'}
        $dnsRules += @{domain_regex=@('^[^.]+$');action='route';server='dns-direct'}
        $dnsRules += @{query_type=@('A','AAAA');action='route';server='fakeip'}
    }

    $localRules = @()
    foreach ($rule in @(@{tag='russia-inside';file='russia_inside'},@{tag='discord';file='discord'})) {
        $localRules += @{type='local';tag=$rule.tag;format='binary';path=(Join-Path $runtime ('rules\' + $rule.file + '.srs'))}
    }
    foreach ($personalTag in @('personal-direct','personal-vpn')) {
        $localRules += @{type='local';tag=$personalTag;format='source';path=(Join-Path $runtime ('rules\' + $personalTag + '.json'))}
    }
    $localRules += @{type='local';tag='telegram';format='source';path=$telegramRulePath}
    foreach ($autoRoute in @('direct','dpi','vpn','blocked')) {
        $localRules += @{type='local';tag=('auto-'+$autoRoute);format='source';path=(Join-Path $runtime ('rules\auto-'+$autoRoute+'.json'))}
    }
    $router = @{
        log=@{level='warn';timestamp=$true;output=(Join-Path $root 'logs\router.log')}
        dns=@{
            servers=@(
                @{type='https';tag='dns-direct';server='1.1.1.1';detour='direct'},
                @{type='https';tag='dns-vpn';server='1.1.1.1';detour='vpn'},
                @{type='udp';tag='dns-tailnet';server='100.100.100.100';server_port=53;detour='tailnet'},
                @{type='fakeip';tag='fakeip';inet4_range='198.18.0.0/15';inet6_range='fc00::/18'}
            )
            rules=$dnsRules; final='dns-direct'; strategy=$directDnsStrategy; reverse_mapping=$true
        }
        inbounds=@(@{type='mixed';tag='local-test';listen='127.0.0.1';listen_port=20809},
            @{type='mixed';tag='dpi-probe';listen='127.0.0.1';listen_port=20828},
            @{type='mixed';tag='direct-probe';listen='127.0.0.1';listen_port=20829},
            @{type='mixed';tag='policy-ack';listen='127.0.0.1';listen_port=20830})
        outbounds=@(
            @{type='direct';tag='direct';bind_interface=$interface},
            @{type='direct';tag='dpi';bind_interface=$interface},
            @{type='direct';tag='tailnet';bind_interface=$tailInterface;connect_timeout='3s'},
            @{type='socks';tag='vpn';server='127.0.0.1';server_port=20808;version='5'},
            @{type='direct';tag='policy-proof'}
        )
        route=@{
            rules=$routeRules;rule_set=$localRules;final='direct'
            default_domain_resolver='dns-direct';auto_detect_interface=$true
        }
        experimental=@{
            cache_file=@{enabled=$true;path=(Join-Path $runtime 'router-cache.db');store_fakeip=$true}
            clash_api=@{external_controller='127.0.0.1:20810';secret=$controllerToken;access_control_allow_origin=@('http://localhost.invalid')}
        }
    }
    if ($policyOptions.Mode -eq 'Auto') {
        $router.log.level = 'info'
        foreach ($outbound in $router.outbounds) {
            if ($outbound.tag -in @('direct','dpi')) { $outbound.connect_timeout = '3s' }
        }
    }
    Write-JsonFile (Join-Path $runtime 'router-proxy.json') $router
    # Do not attract global IPv6 onto an IPv4-only underlay. Keep an internal
    # IPv6 address: sing-tun StrictRoute blocks ALL IPv6 if that family is absent.
    # An explicit, nonempty internal IPv6 route prevents its automatic ::/0.
    $tunRoutes=@('0.0.0.0/0',$(if($hasDirectIPv6){'::/0'}else{'fd7a:125::/126'}))
    $router.inbounds += @{
        type='tun';tag='system-tun';interface_name='SmartRouting';address=@('172.30.125.1/30','fd7a:125::1/126');route_address=$tunRoutes
        mtu=1400;auto_route=$true;strict_route=$true;stack='mixed';dns_mode='hijack'
        route_exclude_address=@('10.0.0.0/8','172.16.0.0/12','192.168.0.0/16','100.64.0.0/10','fd7a:115c:a1e0::/48','fe80::/10','224.0.0.0/4','ff00::/8')
    }
    $router.inbounds[-1].route_exclude_address += ($endpoint.Ip + '/32')
    Write-JsonFile (Join-Path $runtime 'router.json') $router
    $bridge = @{
        log=@{level='warn';timestamp=$true;output=(Join-Path $root 'logs\bridge.log')}
        dns=@{servers=@(@{type='https';tag='dns';server='1.1.1.1';detour='egress'});final='dns';strategy='ipv4_only'}
        inbounds=@(@{type='mixed';tag='bridge';listen='127.0.0.1';listen_port=20808})
        outbounds=@(@{type='direct';tag='egress';bind_interface=$interface})
        route=@{final='egress';default_domain_resolver='dns'}
    }
    Write-JsonFile (Join-Path $runtime 'bridge.json') $bridge
    foreach ($configName in @('bridge.json','router-proxy.json','router.json')) {
        & $routerExe check -c (Join-Path $runtime $configName)
        if ($LASTEXITCODE -ne 0) { throw "Configuration check failed: $configName" }
    }
    Write-Host "Prepared. Direct adapter: $interface. DNS strategy: $directDnsStrategy. WireSock profile: $smartProfile."
}

function Test-HelperAdapterCollision([string]$Text) {
    return $Text -match 'configure tun interface:' -and $Text -match 'create adapter:.*already exists' -and $Text -match 'open existing adapter:.*Element not found'
}
function Test-HelperTunReady($Settings) {
    foreach($tun in @($Settings.inbounds | Where-Object type -eq 'tun')){
        $adapter=@(Get-NetAdapter -Name $tun.interface_name -IncludeHidden -ErrorAction SilentlyContinue)
        if($adapter.Count -ne 1 -or $adapter[0].Status -ne 'Up'){return $false}
        $expected=@($tun.address | Where-Object {$_ -notmatch ':'} | ForEach-Object {($_ -split '/')[0]})
        if(-not $expected.Count){return $false}
        $assigned=@(Get-NetIPAddress -InterfaceIndex $adapter[0].ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object AddressState -eq 'Preferred' | ForEach-Object IPAddress)
        if(@($expected | Where-Object {$_ -notin $assigned}).Count){return $false}
    }
    return $true
}
function Start-Helper([string]$Exe,[string]$Config) {
    Assert-ConnectionOperation
    if(Test-Path -LiteralPath (Join-Path $runtime 'stop.request')){throw 'Helper start cancelled by Stop.'}
    $startupLog = Join-Path $root ('logs\' + [IO.Path]::GetFileNameWithoutExtension($Exe) + '-startup.log')
    $settings=Read-JsonFile $Config
    $tuns=@($settings.inbounds | Where-Object type -eq 'tun')
    $canRetry=$Exe -eq $routerExe -and $tuns.Count -eq 1 -and $tuns[0].interface_name -eq 'SmartRouting'
    $required=@($settings.inbounds | Where-Object {$_.listen -eq '127.0.0.1' -and $_.listen_port} | ForEach-Object {[int]$_.listen_port})
    foreach($attempt in 1..2){
        Assert-ConnectionOperation
        if(Test-Path -LiteralPath (Join-Path $runtime 'stop.request')){throw 'Helper start cancelled by Stop.'}
        foreach($tun in $tuns){
            $released=[Diagnostics.Stopwatch]::StartNew()
            while(@(Get-NetAdapter -Name $tun.interface_name -IncludeHidden -ErrorAction SilentlyContinue).Count){
                Write-ConnectionProgress 'WaitingAdapter'
                Assert-ConnectionOperation
                if(Test-Path -LiteralPath (Join-Path $runtime 'stop.request')){throw 'Helper start cancelled by Stop.'}
                if($released.Elapsed.TotalSeconds -ge 8){throw 'The virtual routing adapter is still in use or closing. Wait before reconnecting.'}
                Start-Sleep -Milliseconds 200
            }
        }
        $process = Start-Process -FilePath $Exe -ArgumentList @('run','-c',('"' + $Config + '"'),'--disable-color') `
            -WorkingDirectory $runtime -WindowStyle Hidden -PassThru -RedirectStandardError $startupLog -RedirectStandardOutput ($startupLog+'.out')
        $deadline=[DateTime]::UtcNow.AddSeconds($(if($tuns.Count){20}else{8}))
        $ready=$false
        try{
            do {
                Assert-ConnectionOperation
                if(Test-Path -LiteralPath (Join-Path $runtime 'stop.request')){throw 'Helper start cancelled by Stop.'}
                $process.Refresh();if($process.HasExited){break}
                $listeners=@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object OwningProcess -eq $process.Id | ForEach-Object LocalPort)
                $ready=@($required | Where-Object {$_ -notin $listeners}).Count -eq 0 -and (Test-HelperTunReady $settings)
                if(-not $ready){Start-Sleep -Milliseconds 100}
            }while(-not $ready -and [DateTime]::UtcNow -lt $deadline)
            $process.Refresh()
            if($ready -and -not $process.HasExited){
                if($canRetry){Write-JsonFile (Join-Path $root 'logs\helper-start-last.json') @{schema=1;success=$true;attempts=$attempt;code=$(if($attempt -gt 1){'RecoveredTunAdapterCollision'}else{'Ready'});updatedUtc=[DateTime]::UtcNow.ToString('o')}}
                return $process
            }
            $collision=$canRetry -and $process.HasExited -and (Test-Path -LiteralPath $startupLog) -and (Test-HelperAdapterCollision ([IO.File]::ReadAllText($startupLog)))
            if($canRetry){Write-JsonFile (Join-Path $root 'logs\helper-start-last.json') @{schema=1;success=$false;attempts=$attempt;code=$(if($collision){'TunAdapterCollision'}elseif($process.HasExited){'Exited'}else{'NotReady'});updatedUtc=[DateTime]::UtcNow.ToString('o')}}
            if($collision -and $attempt -eq 1){Write-ConnectionProgress 'RetryingRouter';Start-Sleep -Milliseconds 2000;continue}
            if($process.HasExited){throw "Helper exited before its network interface was ready: $Exe. See the logs folder."}
            throw 'The helper did not finish network and listener setup before the deadline.'
        }finally{
            if(-not $ready -and -not $process.HasExited){$process | Stop-Process -Force;[void]$process.WaitForExit(3000)}
        }
    }
}

if ($Action -eq 'Watch') {
    Watch-Session $PSCommandPath
    exit
}
if ($Action -eq 'UpdateRules') {
    Write-ConnectionProgress 'UpdatingRules'
    Invoke-AgentCoordinator 'begin-operation'
    try { Update-RuleFeeds } finally { Invoke-AgentCoordinator 'end-operation' }
    exit
}
if ($Action -eq 'Status') {
    Invoke-WireSock @('status')
    Get-OurProcesses | Select-Object Id,ProcessName,Path
    exit
}
if($Action -eq 'Prepare'){
    # First-run preparation must work before the rescue service exists. It never
    # connects WireSock and must not enter the live-start rollback/disconnect path.
    if((Get-OurProcesses).Count){throw 'Stop the current RoutePilot engine before preparing a connection.'}
    $coordinated=$false;$prepareLock=$null
    try{
        if(Get-Service -Name RoutePilotRescue -ErrorAction SilentlyContinue){Invoke-AgentCoordinator 'begin-operation';$coordinated=$true}
        $prepareLock=[IO.File]::Open((Join-Path $runtime 'network-control.lock'),'OpenOrCreate','ReadWrite','None')
        Prepare-Configuration
    }finally{if($prepareLock){$prepareLock.Dispose()};if($coordinated){Invoke-AgentCoordinator 'end-operation'}}
    exit
}
if ($Action -in @('Start','Stop','ProxyOnly','Reconnect','RepairVpn')) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        if($Action -in @('Reconnect','RepairVpn')){throw 'Automatic recovery requires the original elevated session.'}
        # The elevated child must acquire its own setup gate. Do not race that
        # child while an unelevated parent still holds the same exclusive file.
        if($setupProbe){$setupProbe.Dispose();$setupProbe=$null}
        $escapedScript = $PSCommandPath.Replace("'","''")
        $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes(
            "& '$escapedScript' -Action $Action *>&1 | Out-File -LiteralPath '$($root.Replace("'","''"))\logs\last-action.log' -Encoding utf8"))
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -WindowStyle Hidden `
            -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encodedCommand) | Out-Null
        Write-Host 'Windows administrator confirmation requested. Result: logs\last-action.log'
        exit
    }
}

if($Action -eq 'Stop'){
    [IO.File]::WriteAllText((Join-Path $runtime 'stop.request'),[guid]::NewGuid().ToString('N'))
    $stopWait=[Diagnostics.Stopwatch]::StartNew()
    while($true){
        try{Invoke-AgentCoordinator 'begin-operation';break}
        catch{if($stopWait.Elapsed.TotalSeconds -ge 60){throw};Start-Sleep -Milliseconds 400}
    }
}else{
    try{Invoke-AgentCoordinator 'begin-operation'}
    catch{if($Action -in @('Reconnect','RepairVpn')){exit 75};throw}
}
try { $controlLock = [IO.File]::Open((Join-Path $runtime 'network-control.lock'),'OpenOrCreate','ReadWrite','None') }
catch { Invoke-AgentCoordinator 'end-operation';if($Action -in @('Reconnect','RepairVpn')){exit 75};throw 'Another network operation is in progress. Wait for it to finish.' }
try {
if($Action -eq 'RepairVpn'){
    Repair-SessionVpn $ExpectedSessionId
    exit
}
if ($Action -eq 'Reconnect') {
    if(Test-Path -LiteralPath (Join-Path $runtime 'stop.request')){Write-Host 'Recovery cancelled by Stop.';exit}
    $previous=Read-JsonFile (Join-Path $runtime 'session.json')
    if(-not $ExpectedSessionId -or $previous.id -ne $ExpectedSessionId -or -not $previous.running){Write-Host 'Stale recovery ignored.';exit}
    $wireState=(Get-WireSockObservation).state
    if($wireState -notin @('Connected','Disconnected')){throw 'Managed WireSock state is not confirmed; automatic recovery is paused.'}
    if($previous.mode -eq 'Start' -and (Get-NetAdapter -Name 'VPNUS' -ErrorAction SilentlyContinue).Status -eq 'Up'){throw 'VPNUS is active; automatic recovery is paused.'}
    [void](Set-SessionRecovering $ExpectedSessionId 'Automatic reconnect')
    Write-ConnectionEvidence $root 'ReconnectRequested' $previous
    Stop-OurProcesses -KeepWatch
    if($wireState -eq 'Connected'){Invoke-WireSock @('disconnect') | Out-Null}
}
if ($Action -eq 'Stop') {
    Write-ConnectionProgress 'Stopping'
    $stopSession=$null;try{$stopSession=Read-JsonFile (Join-Path $runtime 'session.json')}catch{}
    Write-ConnectionEvidence $root 'StopRequested' $stopSession
    Stop-OurProcesses
    $stopped=@{id=$(if($stopSession){$stopSession.id}else{''});running=$false;bridgeReady=$false;bridgeGeneration=[int]$stopSession.bridgeGeneration;stopReason='RequestedStop';stoppedUtc=[DateTime]::UtcNow.ToString('o')}
    Write-JsonFile (Join-Path $runtime 'session.json') $stopped
    Write-ConnectionEvidence $root 'Stopped' $stopped
    Disconnect-ManagedWireSock
    Write-Host 'Smart routing stopped. The original WireSock profile is unchanged.'
    exit
}

if ($Action -eq 'Start') {
    $temporaryVpn = Get-NetAdapter -Name 'VPNUS' -ErrorAction SilentlyContinue
    if ($temporaryVpn -and $temporaryVpn.Status -eq 'Up') {
        throw 'Disconnect the temporary VPNUS connection before starting system smart routing.'
    }
}
if ((Get-OurProcesses).Count) { throw 'Smart routing is already running. Stop it before restarting.' }
if($Action -in @('Start','ProxyOnly')){
    $startupObservation=Get-WireSockObservation
    if($startupObservation.state -eq 'Foreign'){
        throw 'Another WireSock profile is in use. Disconnect it in WireSock before starting RoutePilot.'
    }
    if($startupObservation.state -notin @('Connected','Disconnected')){throw 'WireSock state could not be verified. Wait and check the WireSock service.'}
}
if($Action -in @('Start','ProxyOnly')){Stop-OurProcesses}
if($Action -in @('Start','ProxyOnly')){Remove-Item -LiteralPath (Join-Path $runtime 'stop.request') -Force -ErrorAction SilentlyContinue}
try {
Write-ConnectionProgress 'Preparing'
    Write-ConnectionEvidence $root 'StartRequested'
    Prepare-Configuration
if ($Action -eq 'Prepare') { exit }
if ($Action -eq 'SyncProfile') {
    Sync-WireSockProfile $smartProfile (Join-Path $private ($smartProfile + '.conf')) (Join-Path $private 'profile-backups')
    exit
}
$routingPorts=@(20808,20809,20810,20828,20829,20830,20831)
$occupied=@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object {$_.LocalPort -in $routingPorts})
if($occupied.Count){throw ('Local routing ports are already in use: '+(($occupied.LocalPort | Sort-Object -Unique) -join ', '))}

    Sync-WireSockProfile $smartProfile (Join-Path $private ($smartProfile + '.conf')) (Join-Path $private 'profile-backups')
    Write-ConnectionProgress 'Connecting'
    Connect-WireSock
    if(Test-Path -LiteralPath (Join-Path $runtime 'stop.request')){throw 'Connection cancelled by Stop.'}
    Invoke-WireSock @('status')
    if(Test-PolicyV2){
        & (Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe') --prepare-policy $root
        if($LASTEXITCODE -ne 0){throw 'The checked policy snapshot could not be prepared.'}
    }
    Write-ConnectionProgress 'StartingBridge'
    $bridgeProcess = Start-Helper $bridgeExe (Join-Path $runtime 'bridge.json')
    $sessionMode=if($Action -eq 'Reconnect'){$previous.mode}else{$Action}
    $configFile = if ($sessionMode -eq 'ProxyOnly') { 'router-proxy.json' } else { 'router.json' }
    Write-ConnectionProgress 'StartingRouter'
    $routerProcess = Start-Helper $routerExe (Join-Path $runtime $configFile)
    # A deliberate new session retires the previous session's independent hold.
    Remove-Item -LiteralPath (Join-Path $runtime 'bridge-safety.json') -Force -ErrorAction SilentlyContinue
    Write-JsonFile (Join-Path $runtime 'session.json') @{
        running=$true;id=[guid]::NewGuid().ToString('N');startedUtc=[DateTime]::UtcNow.ToString('o')
        operationId=$(if($Action -eq 'Reconnect'){$previous.operationId}else{$OperationId})
        routerPid=$routerProcess.Id;routerStartedUtc=$routerProcess.StartTime.ToUniversalTime().ToString('o')
        bridgePid=$bridgeProcess.Id;bridgeStartedUtc=$bridgeProcess.StartTime.ToUniversalTime().ToString('o');bridgeGeneration=1;bridgeReady=$false;mode=$sessionMode;sourceProfile=$sourceProfile
        recovering=$true;recoveryParentId=$(if($Action -eq 'Reconnect'){$ExpectedSessionId}else{''});networkSignature=(Get-SessionNetworkSignature)
    }
    Write-ConnectionProgress 'ApplyingPolicy'
    Start-PolicyController
    $startedSession=Read-JsonFile (Join-Path $runtime 'session.json')
    $policyDeadline=[DateTime]::UtcNow.AddSeconds(30);$policyReady=$false
    do {
        Assert-ConnectionOperation
        if(Test-Path -LiteralPath (Join-Path $runtime 'stop.request')){throw 'Connection cancelled by Stop.'}
        $routerProcess.Refresh();if($routerProcess.HasExited){throw 'The routing engine exited during policy setup. See logs/smart-router-startup.log.'}
        try {$policyState=Read-JsonFile (Join-Path $runtime 'policy-state.json');$policyReady=$policyState.epoch.StartsWith($startedSession.id+':') -and $policyState.phase -in @('Auto','Lists')}catch{}
        if(-not $policyReady){Start-Sleep -Milliseconds 150}
    }while(-not $policyReady -and [DateTime]::UtcNow -lt $policyDeadline)
    if(-not $policyReady){throw 'The routing controller did not become ready.'}
    Write-ConnectionProgress 'Verifying'
    $healthAttempts=0
    do{
        $healthAttempts++
        $health=& (Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe') --session-health | ConvertFrom-Json
        Assert-ConnectionOperation
        if(Test-Path -LiteralPath (Join-Path $runtime 'stop.request')){throw 'Connection cancelled by Stop.'}
        $current=Read-JsonFile (Join-Path $runtime 'session.json')
        if($current.id -ne $startedSession.id -or $current.bridgeGeneration -ne $startedSession.bridgeGeneration -or -not $current.running){throw 'Session changed during HTTPS verification.'}
        if($health.healthy -or $healthAttempts -ge 2 -or $health.code -notin @('ControlHttpsUnavailable','StatusUnavailable','MissingExpectedEgress')){break}
        Start-Sleep -Milliseconds 500
    }while($true)
    $healthCode=[string]$health.code
    if($healthCode -notmatch '^[A-Za-z][A-Za-z0-9]{0,63}$'){$healthCode='UnknownHealthFailure'}
    # Cleanup retires session-health.json. Keep only the bounded reason and
    # session identity for diagnosis; no addresses, keys or raw CLI output.
    try{Write-JsonFile (Join-Path $root 'logs\connection-health-last.json') @{schema=1;sessionId=$startedSession.id;bridgeGeneration=$startedSession.bridgeGeneration;healthy=[bool]$health.healthy;code=$healthCode;attempts=$healthAttempts;updatedUtc=[DateTime]::UtcNow.ToString('o')}}catch{}
    if(-not $health.healthy){throw ('The AWG2 data path did not pass HTTPS verification. ['+$healthCode+']')}
    Write-JsonFile (Join-Path $runtime 'session-health.json') @{sessionId=$startedSession.id;bridgeGeneration=$startedSession.bridgeGeneration;healthy=$true;code='Verified';message=$health.message;updatedUtc=[DateTime]::UtcNow.ToString('o')}
    $startedSession.recovering=$false;$startedSession.bridgeReady=$true
    Write-JsonFile (Join-Path $runtime 'session.json') $startedSession
    Write-ConnectionEvidence $root $(if($Action -eq 'Reconnect'){'ReconnectVerified'}else{'StartVerified'}) $startedSession
    Remove-Item -LiteralPath (Join-Path $root 'logs\connection-lost.txt'),(Join-Path $root 'logs\recovery-error.log') -Force -ErrorAction SilentlyContinue
    $watchScriptPath = '"' + $PSCommandPath + '"'
    if($Action -ne 'Reconnect'){
        Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -WorkingDirectory $root `
            -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$watchScriptPath,'-Action','Watch') `
            -RedirectStandardOutput (Join-Path $root 'logs\watch-output.log') -RedirectStandardError (Join-Path $root 'logs\watch-error.log') | Out-Null
    }
    if ($Action -eq 'Start') { Clear-DnsClientCache -ErrorAction SilentlyContinue }
    Write-Host "Running: $Action. Router 127.0.0.1:20809; VPN bridge 127.0.0.1:20808."
} catch {
    $startFailure = $_
    try {
        Stop-OurProcesses -KeepWatch:($Action -eq 'Reconnect')
        if($Action -eq 'Reconnect' -and -not(Test-Path -LiteralPath (Join-Path $runtime 'stop.request'))){
            $previous | Add-Member -NotePropertyName recovering -NotePropertyValue $true -Force
            $previous | Add-Member -NotePropertyName recoveryReason -NotePropertyValue $startFailure.Exception.Message -Force
            $previous | Add-Member -NotePropertyName bridgeReady -NotePropertyValue $false -Force
            Write-JsonFile (Join-Path $runtime 'session.json') $previous
        }else{Write-JsonFile (Join-Path $runtime 'session.json') @{running=$false;bridgeReady=$false;stopReason='StartFailed';stoppedUtc=[DateTime]::UtcNow.ToString('o')}}
        Write-ConnectionEvidence $root $(if($Action -eq 'Reconnect'){'ReconnectFailed'}else{'StartFailed'})
        Disconnect-ManagedWireSock
    } catch { [IO.File]::WriteAllText((Join-Path $root 'logs\cleanup-error.log'),$_.Exception.Message) }
    throw $startFailure
}
} finally { $controlLock.Dispose(); Invoke-AgentCoordinator 'end-operation' }
} finally { if($setupProbe){$setupProbe.Dispose()} }
