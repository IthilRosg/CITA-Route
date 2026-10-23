$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'FileStorage.ps1')
. (Join-Path $PSScriptRoot 'RuleFeeds.ps1')
$testBase = Join-Path $PSScriptRoot 'runtime'
$testFolder = Join-Path $testBase ('feed-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testFolder | Out-Null
function Assert-Feed($Condition,$Message) { if (-not $Condition) { throw $Message } }
try {
    $telegram = Join-Path $testFolder 'telegram.json'
    Write-TelegramRules ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'rules\telegram-cidr.txt'))) $telegram
    $parsed = [IO.File]::ReadAllText($telegram) | ConvertFrom-Json
    Assert-Feed ($parsed.rules[0].ip_cidr -contains '149.154.160.0/20') 'Telegram server network was omitted.'
    Assert-Feed ($parsed.rules[1].domain_suffix -contains 'telegram.org') 'Telegram mini-app domain must route even before IP resolution.'
    Write-JsonFile $telegram @{version=3;rules=@(@{ip_cidr=@('149.154.160.0/20')})}
    Ensure-TelegramDomainRules $telegram
    $migrated=[IO.File]::ReadAllText($telegram)
    Ensure-TelegramDomainRules $telegram
    Assert-Feed ($migrated -eq [IO.File]::ReadAllText($telegram)) 'Repeated domain migration must not rewrite or duplicate rules.'
    $parsed=$migrated|ConvertFrom-Json
    Assert-Feed ($parsed.rules[0].ip_cidr -contains '149.154.160.0/20') 'Domain migration must preserve the existing IP networks.'
    Assert-Feed ($parsed.rules[1].domain_suffix.Count -eq 4) 'All pinned Telegram domains need a static route counterpart.'
    foreach ($invalid in @('', 'not-an-address', '0.0.0.0/0', '149.154.160.0/33', '2001:db8::/129')) {
        $rejected = $false
        try { Write-TelegramRules $invalid $telegram } catch { $rejected = $true }
        Assert-Feed $rejected ('Invalid feed accepted: ' + $invalid)
    }
    $files = @()
    foreach ($name in @('first','second')) {
        $target = Join-Path $testFolder ($name + '.json')
        $staged = $target + '.new'
        [IO.File]::WriteAllText($target,'old-' + $name)
        [IO.File]::WriteAllText($staged,'new-' + $name)
        $files += @{Target=$target;Staged=$staged}
    }
    $locked = [IO.File]::Open($files[1].Target,'Open','Read','Read')
    $failed = $false
    try { Publish-RuleFiles $files } catch { $failed = $true }
    finally { $locked.Dispose() }
    Assert-Feed $failed 'A locked second rule did not fail publication.'
    Assert-Feed ([IO.File]::ReadAllText($files[0].Target) -eq 'old-first') 'Partial publication was not rolled back.'
    Assert-Feed ([IO.File]::ReadAllText($files[1].Target) -eq 'old-second') 'Locked rule was modified.'
    [IO.File]::WriteAllText($files[0].Staged,'new-first')
    Publish-RuleFiles $files
    Assert-Feed ([IO.File]::ReadAllText($files[0].Target) -eq 'new-first' -and [IO.File]::ReadAllText($files[1].Target) -eq 'new-second') 'Successful publication did not install both rules.'
    Assert-Feed (@(Get-ChildItem -LiteralPath $testFolder -Filter '*.bak').Count -eq 0) 'Successful update leaked backup files.'
    Write-Output 'PASS: Telegram IPv4/IPv6 list, invalid/empty feed rejection, rollback after partial publication, successful batch publication.'
} finally {
    $resolvedTestFolder = [IO.Path]::GetFullPath($testFolder)
    $allowedPrefix = [IO.Path]::GetFullPath($testBase).TrimEnd('\') + '\feed-tests-'
    if (-not $resolvedTestFolder.StartsWith($allowedPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolvedTestFolder -Recurse -Force
}
