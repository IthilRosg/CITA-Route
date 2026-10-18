$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$runtime = Join-Path $root 'runtime'
$routerExe = Join-Path $runtime 'smart-router.exe'
if (Get-Process -Name 'smart-router','vpn-bridge' -ErrorAction SilentlyContinue) {
    throw 'Stop smart routing before running the isolated policy check.'
}
foreach ($configName in @('bridge.json','router-proxy.json','router.json')) {
    & $routerExe check -c (Join-Path $runtime $configName)
    if ($LASTEXITCODE -ne 0) { throw "Invalid config: $configName" }
}
$testConfig = Get-Content -LiteralPath (Join-Path $runtime 'router-proxy.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$testConfig.inbounds += [pscustomobject]@{type='direct';tag='dns-test';listen='127.0.0.1';listen_port=20811;network='udp'}
$testConfig.log.level = 'info'
$testConfig.log.output = Join-Path $root 'logs\policy-test.log'
[IO.File]::WriteAllText($testConfig.log.output,'')
$testPath = Join-Path $runtime 'router-policy-test.json'
$testFolder = Join-Path $runtime ('policy-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testFolder | Out-Null
foreach ($rule in $testConfig.route.rule_set) {
    if ($rule.tag -notin @('personal-vpn','personal-direct','auto-vpn','auto-direct','auto-dpi','auto-blocked')) { continue }
    $copyPath = Join-Path $testFolder ([IO.Path]::GetFileName($rule.path))
    Copy-Item -LiteralPath $rule.path -Destination $copyPath
    $rule.path = $copyPath
}
$personalVpnPath = Join-Path $testFolder 'personal-vpn.json'
$testConfig.experimental.cache_file.path = Join-Path $testFolder 'router-cache.db'
[IO.File]::WriteAllText($testPath, ($testConfig | ConvertTo-Json -Depth 30),
    (New-Object System.Text.UTF8Encoding($false)))
$testProcess = Start-Process -FilePath $routerExe -WindowStyle Hidden -PassThru `
    -ArgumentList @('run','-c',('"' + $testPath + '"'),'--disable-color') `
    -RedirectStandardError (Join-Path $root 'logs\policy-test-startup.log')
try {
    Start-Sleep -Seconds 1
    if ($testProcess.HasExited) {
        Get-Content -LiteralPath (Join-Path $root 'logs\policy-test-startup.log') -Encoding UTF8
        throw 'The test router did not start.'
    }
    function Invoke-TestDns([string]$Domain,[int]$RecordType) {
        $packet = New-Object 'System.Collections.Generic.List[byte]'
        $packet.AddRange([byte[]]@(82,80,1,0,0,1,0,0,0,0,0,0))
        foreach ($label in $Domain.Split('.')) {
            $bytes = [Text.Encoding]::ASCII.GetBytes($label)
            $packet.Add([byte]$bytes.Length); $packet.AddRange($bytes)
        }
        $packet.AddRange([byte[]]@(0,0,$RecordType,0,1))
        $client = New-Object Net.Sockets.UdpClient
        try {
            $client.Client.ReceiveTimeout = 6000
            $client.Connect('127.0.0.1',20811)
            [void]$client.Send($packet.ToArray(),$packet.Count)
            $remote = New-Object Net.IPEndPoint([Net.IPAddress]::Any,0)
            $response = $client.Receive([ref]$remote)
            if ($response.Length -lt 12 -or $response[0] -ne 82 -or $response[1] -ne 80) { throw 'Invalid local DNS response.' }
            if (($response[3] -band 15) -ne 0) { throw 'Local DNS request failed.' }
            return ([int]$response[6] * 256 + [int]$response[7])
        } finally { $client.Dispose() }
    }
    if ((Invoke-TestDns 'yandex.ru' 1) -eq 0) { throw 'Local DNS did not return an IPv4 address.' }
    if ($testConfig.dns.strategy -eq 'ipv4_only' -and (Invoke-TestDns 'yandex.ru' 28) -ne 0) {
        throw 'IPv6 answers were returned without a physical IPv6 gateway.'
    }
    $directHttp = & curl.exe --silent --show-error --max-time 15 --proxy 'http://127.0.0.1:20809' `
        'https://yandex.ru' -o NUL --write-out '%{http_code}'
    $directExit = $LASTEXITCODE
    $vpnHttp = & curl.exe --silent --max-time 10 --proxy 'http://127.0.0.1:20809' `
        'https://youtube.com' -o NUL --write-out '%{http_code}'
    $vpnExit = $LASTEXITCODE
    & curl.exe --silent --max-time 5 --proxy 'http://127.0.0.1:20809' 'https://149.154.167.50' -o NUL
    $telegramExit = $LASTEXITCODE
    [pscustomobject]@{
        DirectHttp=$directHttp
        DirectCurlExit=$directExit
        MissingVpnBridgeCurlExit=$vpnExit
        DoesNotFallbackToDirect=($vpnExit -ne 0)
    } | ConvertTo-Json -Compress
    if ($directExit -ne 0 -or $vpnExit -eq 0) { throw 'Routing policy check failed. Inspect logs\policy-test.log.' }
    $logStream = New-Object IO.FileStream($testConfig.log.output,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    $logReader = New-Object IO.StreamReader($logStream)
    try { $policyLog = $logReader.ReadToEnd() } finally { $logReader.Dispose() }
    if ($telegramExit -eq 0 -or $policyLog -notmatch 'outbound/socks\[vpn\]: outbound connection to 149\.154\.167\.50:443') {
        throw 'Telegram raw-IP traffic did not select the VPN route.'
    }
    $controllerSecret = [IO.File]::ReadAllText((Join-Path $root 'private\controller-token.txt')).Trim()
    $controllerHeaders = @{Authorization=('Bearer ' + $controllerSecret)}
    $authenticationEnforced = $false
    try { Invoke-RestMethod -Uri 'http://127.0.0.1:20810/connections' -TimeoutSec 3 | Out-Null }
    catch { if ([int]$_.Exception.Response.StatusCode -eq 401) { $authenticationEnforced = $true } else { throw } }
    if (-not $authenticationEnforced) { throw 'The statistics API accepted an unauthenticated request.' }
    $trafficSnapshot = Invoke-RestMethod -Uri 'http://127.0.0.1:20810/connections' -Headers $controllerHeaders -TimeoutSec 3
    if ($trafficSnapshot.uploadTotal -le 0 -or $trafficSnapshot.downloadTotal -le 0) { throw 'Traffic counters did not record the real request.' }
    $hotRule = @{version=3;rules=@(@{domain_suffix=@('yandex.ru')})} | ConvertTo-Json -Depth 8
    $hotRuleTemporary = $personalVpnPath + '.test.tmp'
    [IO.File]::WriteAllText($hotRuleTemporary,$hotRule,(New-Object System.Text.UTF8Encoding($false)))
    [IO.File]::Replace($hotRuleTemporary,$personalVpnPath,[NullString]::Value)
    Start-Sleep -Milliseconds 1000
    & curl.exe --silent --max-time 10 --proxy 'http://127.0.0.1:20809' 'https://yandex.ru' -o NUL
    $hotRuleExit = $LASTEXITCODE
    if ($hotRuleExit -eq 0) { throw 'The new personal VPN rule was not applied without a restart.' }
    Write-Output 'PASS: DNS A/AAAA policy, protected statistics API, real traffic counters, isolated personal rule reload, Telegram raw-IP VPN routing.'
    Get-Content -LiteralPath $testConfig.log.output -Tail 20 |
        Where-Object { $_ -match 'outbound/(direct|socks)|connection refused|connectex|started' }
} finally {
    Stop-Process -Id $testProcess.Id -Force -ErrorAction SilentlyContinue
    $resolvedTestFolder = [IO.Path]::GetFullPath($testFolder)
    $allowedPrefix = [IO.Path]::GetFullPath($runtime).TrimEnd('\') + '\policy-test-'
    if (-not $resolvedTestFolder.StartsWith($allowedPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolvedTestFolder -Recurse -Force
}
