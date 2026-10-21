$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$fixture = Join-Path $projectRoot ('runtime\prepare-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture,(Join-Path $fixture 'runtime\rules'),(Join-Path $fixture 'data') -Force | Out-Null
try {
    . (Join-Path $projectRoot 'FileStorage.ps1')
    . (Join-Path $projectRoot 'RuleFeeds.ps1')
    . (Join-Path $projectRoot 'ConnectionEnvironment.ps1')
    $syntax = [Management.Automation.Language.Parser]::ParseFile((Join-Path $projectRoot 'SmartRouting.ps1'),[ref]$null,[ref]$null)
    foreach ($name in @('Get-Domains','Prepare-Configuration')) {
        $definition = $syntax.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$false)
        Invoke-Expression $definition.Extent.Text
    }
    function Get-NetIPConfiguration {
        [pscustomobject]@{InterfaceAlias='Fixture WiFi';NetAdapter=@{HardwareInterface=$true;Status='Up'};IPv4DefaultGateway=@{NextHop='192.168.1.1'};IPv6DefaultGateway=$(if($script:fixtureIpv6){@{NextHop='fe80::1'}}else{$null});NetIPv4Interface=@{InterfaceMetric=10}}
    }
    function Invoke-WireSock($Arguments) {
        if ($Arguments[0] -ne 'export') { throw 'The configuration fixture must never change WireSock.' }
        [IO.File]::WriteAllText($Arguments[2],"[Interface]`nPrivateKey = AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=`nAddress = 10.0.0.2/32`n[Peer]`nPublicKey = AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=`nEndpoint = 203.0.113.1:47193`nAllowedIPs = 0.0.0.0/0`n")
    }
    $root=$fixture; $runtime=Join-Path $root 'runtime'; $private=Join-Path $root 'private'
    $routerExe=Join-Path $projectRoot 'runtime\smart-router.exe'
    $bridgeExe=Join-Path $env:ProgramFiles 'RoutePilot\Agent\vpn-bridge.exe'
    $sourceProfile='Fixture'; $cli='fixture'; $Setup=$true; $connectionSettings=@{}; $smartProfile='SmartRouting-AWG2'; $utf8=New-Object Text.UTF8Encoding($false)
    foreach ($name in @('russia_inside.srs','discord.srs','telegram.json')) { Copy-Item -LiteralPath (Join-Path $projectRoot ('runtime\rules\'+$name)) -Destination (Join-Path $runtime 'rules') }
    [IO.File]::WriteAllText((Join-Path $root 'force-direct.txt'),'example.org')
    [IO.File]::WriteAllText((Join-Path $root 'force-vpn.txt'),'example.net')
    foreach ($ipv6 in @($false,$true)) {
    $script:fixtureIpv6=$ipv6
    foreach ($mode in @('Auto','Lists')) {
        Write-JsonFile (Join-Path $root 'data\routing-policy.json') @{Mode=$mode;AllowListedDpi=$false}
        Prepare-Configuration
        $config=[IO.File]::ReadAllText((Join-Path $runtime 'router.json')) | ConvertFrom-Json
        $expected=if($mode -eq 'Auto'){'info'}else{'warn'}
        if ($config.log.level -ne $expected) { throw "Wrong log level for $mode." }
        if ($config.dns.strategy -ne $(if($ipv6){'prefer_ipv4'}else{'ipv4_only'})) { throw 'DNS families must follow the physical gateway.' }
        $tun=@($config.inbounds|Where-Object type -eq 'tun')[0]
        if(($tun.route_address -contains '::/0') -ne $ipv6){throw 'A TUN IPv6 default must require a physical IPv6 gateway.'}
        if(-not @($tun.address|Where-Object {$_ -match ':'}).Count -or -not $tun.strict_route){throw 'Internal IPv6 and StrictRoute must remain present to avoid a global IPv6 block or loss of DNS protection.'}
        if(-not $ipv6 -and ((@($tun.route_address|Where-Object {$_ -match ':'}) -join ',') -ne 'fd7a:125::/126')){throw 'An IPv4-only underlay may route only the TUN internal IPv6 subnet.'}
        if($tun.route_exclude_address -notcontains '100.64.0.0/10' -or $tun.route_exclude_address -notcontains 'fd7a:115c:a1e0::/48'){throw 'Both Tailscale overlay families must stay excluded.'}
        if($config.dns.rules[0].server -ne 'dns-tailnet' -or $config.dns.rules[0].domain_suffix -notcontains 'ts.net'){throw 'MagicDNS must precede FakeIP and ordinary DNS rules.'}
        $tailnet=@($config.outbounds|Where-Object tag -eq 'tailnet')
        if($tailnet.Count -ne 1 -or -not $tailnet[0].bind_interface -or $tailnet[0].bind_interface -eq 'Fixture WiFi'){throw 'Tailnet DNS cannot use the physical adapter or an unbound WAN fallback.'}
        $tailIndex=-1;$sniffIndex=-1
        for($i=0;$i -lt $config.route.rules.Count;$i++){
            if($config.route.rules[$i].process_name -contains 'tailscaled.exe'){$tailIndex=$i}
            if($config.route.rules[$i].action -eq 'sniff'){$sniffIndex=$i}
        }
        if($tailIndex -lt 0 -or $sniffIndex -lt 0 -or $tailIndex -ge $sniffIndex -or $config.route.rules[$tailIndex].outbound -ne 'direct'){throw 'Tailscale control/DERP transport must avoid sniffing and adaptive rules.'}
        if ($mode -eq 'Auto' -and @($config.outbounds | Where-Object { $_.tag -in @('direct','dpi') -and $_.connect_timeout -eq '3s' }).Count -ne 2) { throw 'Auto probes need bounded connection times.' }
        $manualIndex=-1;$adaptiveIndex=-1
        for($index=0;$index -lt $config.route.rules.Count;$index++){
            $rule=$config.route.rules[$index]
            if($rule.rule_set -contains 'personal-direct' -and $rule.action -eq 'route'){$manualIndex=$index}
            if($rule.action -eq 'route' -and $rule.outbound -eq 'direct' -and ($rule.rule_set -contains 'auto-direct' -or @($rule.rules | ForEach-Object rule_set) -contains 'auto-direct')){$adaptiveIndex=$index}
        }
        if($manualIndex -lt 0 -or $adaptiveIndex -lt 0 -or $manualIndex -ge $adaptiveIndex){throw 'Manual rules must precede adaptive decisions.'}
    }
    }
    Write-Output 'PASS: Auto/Lists with and without physical IPv6, real engine validation, Tailscale exclusions/transport priority, manual priority and bounded Auto sockets. No system network changed.'
} finally {
    $expectedPrefix=[IO.Path]::GetFullPath((Join-Path $projectRoot 'runtime\prepare-test-'))
    if (-not [IO.Path]::GetFullPath($fixture).StartsWith($expectedPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup.' }
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
