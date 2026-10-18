$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WireSockProfiles.ps1')
$testBase = Join-Path $PSScriptRoot 'runtime'
$testFolder = Join-Path $testBase ('profile-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testFolder -Force | Out-Null
$desiredPath = Join-Path $testFolder 'SmartRouting-AWG2.conf'
$backupDirectory = Join-Path $testFolder 'backups'
$testEncoding = New-Object System.Text.UTF8Encoding($false)
$testConfiguration = @'
[Interface]
PrivateKey = CaseSensitiveKey
Address = 10.10.10.2/32
[Peer]
PublicKey = PublicKeyForTest
AllowedIPs = 0.0.0.0/0, ::/0
Endpoint = 192.0.2.1:51820
#@ws:AllowedApps = C:\RoutePilot\vpn-bridge.exe
#@ws:DisallowedApps = C:\RoutePilot\smart-router.exe
'@
$script:mockProfiles = @{Original='DO NOT CHANGE'}
$script:mockCalls = @()
$script:mockConnected = $false
$script:mockFailImports = 0
function Assert-ProfileTest($Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Invoke-WireSock([string[]]$CliArguments) {
    $command = $CliArguments[0]
    $script:mockCalls += $command
    switch ($command) {
        'list' { 'Available profiles:'; $script:mockProfiles.Keys | ForEach-Object { ' - ' + $_ }; break }
        'export' { [IO.File]::WriteAllText($CliArguments[2],$script:mockProfiles[$CliArguments[1]],$testEncoding); break }
        'status' { if ($script:mockConnected) { 'Connected to SmartRouting-AWG2' } else { 'Not connected' }; break }
        'disconnect' { $script:mockConnected = $false; break }
        'delete' { $script:mockProfiles.Remove($CliArguments[1]); break }
        'import' {
            if ($script:mockFailImports -gt 0) { $script:mockFailImports--; throw 'Simulated import failure' }
            $profileName = [IO.Path]::GetFileNameWithoutExtension($CliArguments[1])
            if ($script:mockProfiles.ContainsKey($profileName)) { throw 'Profile already exists' }
            $script:mockProfiles[$profileName] = [IO.File]::ReadAllText($CliArguments[1])
            break
        }
        default { throw "Unexpected CLI command: $command" }
    }
}
try {
    [IO.File]::WriteAllText($desiredPath,$testConfiguration,$testEncoding)
    Sync-WireSockProfile 'SmartRouting-AWG2' $desiredPath $backupDirectory
    Assert-ProfileTest ($script:mockProfiles.ContainsKey('SmartRouting-AWG2')) 'Missing profile was not imported.'
    $script:mockProfiles['SmartRouting-AWG2'] = ($testConfiguration -replace '#@ws:','' -replace 'AllowedIPs = 0.0.0.0/0, ::/0','AllowedIPs=0.0.0.0/0,::/0')
    $importCount = @($script:mockCalls | Where-Object { $_ -eq 'import' }).Count
    Sync-WireSockProfile 'SmartRouting-AWG2' $desiredPath $backupDirectory
    Sync-WireSockProfile 'SmartRouting-AWG2' $desiredPath $backupDirectory
    Assert-ProfileTest (@($script:mockCalls | Where-Object { $_ -eq 'import' }).Count -eq $importCount) 'Repeat launch attempted a duplicate import.'
    Assert-ProfileTest (@($script:mockCalls | Where-Object { $_ -eq 'delete' }).Count -eq 0) 'Unchanged profile was deleted.'

    $newConfiguration = $testConfiguration.Replace('vpn-bridge.exe','new-bridge.exe')
    [IO.File]::WriteAllText($desiredPath,$newConfiguration,$testEncoding)
    $script:mockConnected = $true
    Sync-WireSockProfile 'SmartRouting-AWG2' $desiredPath $backupDirectory
    Assert-ProfileTest (-not $script:mockConnected) 'Changed managed profile was not disconnected before replacement.'
    Assert-ProfileTest ($script:mockProfiles['SmartRouting-AWG2'] -ceq $newConfiguration) 'Changed configuration was not applied.'
    Assert-ProfileTest (@(Get-ChildItem -LiteralPath $backupDirectory -Recurse -Filter 'SmartRouting-AWG2.conf').Count -eq 1) 'Previous profile backup is missing.'

    [IO.File]::WriteAllText($desiredPath,$testConfiguration,$testEncoding)
    $script:mockFailImports = 1
    $caughtImportFailure = $false
    try { Sync-WireSockProfile 'SmartRouting-AWG2' $desiredPath $backupDirectory }
    catch { $caughtImportFailure = $_.Exception.Message -match 'Simulated import failure' }
    Assert-ProfileTest $caughtImportFailure 'Import failure was not reported.'
    Assert-ProfileTest ($script:mockProfiles['SmartRouting-AWG2'] -ceq $newConfiguration) 'Automatic profile restore failed.'
    Assert-ProfileTest ($script:mockProfiles['Original'] -ceq 'DO NOT CHANGE') 'Original user profile was changed.'
    Write-Output 'PASS: first import, repeated starts, export formatting, managed-profile update, backup, rollback, original profile preserved.'
} finally {
    $resolvedTestFolder = [IO.Path]::GetFullPath($testFolder)
    $allowedTestPrefix = [IO.Path]::GetFullPath($testBase).TrimEnd('\') + '\profile-tests-'
    if (-not $resolvedTestFolder.StartsWith($allowedTestPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolvedTestFolder -Recurse -Force
}
