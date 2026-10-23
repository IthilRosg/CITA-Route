function Write-TelegramRules([string]$Text,[string]$Path) {
    $networks = @()
    foreach ($line in ($Text -split '\r?\n')) {
        $entry = $line.Trim()
        if (-not $entry) { continue }
        if ($entry -notmatch '^([^/]+)/([0-9]{1,3})$') { throw 'Invalid Telegram IP list.' }
        $address = $null
        $prefix = [int]$Matches[2]
        if (-not [Net.IPAddress]::TryParse($Matches[1],[ref]$address)) { throw 'Invalid Telegram IP address.' }
        $bits = if ($address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) { 32 } else { 128 }
        if ($prefix -lt 1 -or $prefix -gt $bits) { throw 'Invalid Telegram IP prefix.' }
        $networks += $address.ToString() + '/' + $prefix
    }
    if (-not $networks.Count) { throw 'Empty Telegram IP list.' }
    # A FakeIP/CONNECT destination is still a name before the outbound resolves
    # it. IP-only Telegram rules cannot cover its official web mini-apps.
    Write-JsonFile $Path @{version=3;rules=@(@{ip_cidr=@($networks | Sort-Object -Unique)},@{domain_suffix=@('telegram.org','telegram.me','t.me','tdesktop.com')})}
}

function Ensure-TelegramDomainRules([string]$Path) {
    $data=Read-JsonFile $Path
    if($data.version -ne 3 -or -not $data.rules){throw 'Invalid installed Telegram rule set.'}
    $required=@('telegram.org','telegram.me','t.me','tdesktop.com')
    $present=@($data.rules|ForEach-Object {$_.domain_suffix})
    $missing=@($required|Where-Object {$_ -notin $present})
    if($missing.Count){$data.rules=@($data.rules)+@(@{domain_suffix=$missing});Write-JsonFile $Path $data}
}

function Publish-RuleFiles([object[]]$Files) {
    $applied = New-Object 'System.Collections.Generic.List[object]'
    try {
        foreach ($file in $Files) {
            $backup = $file.Target + '.' + [guid]::NewGuid().ToString('N') + '.bak'
            $existed = Test-Path -LiteralPath $file.Target
            if ($existed) { [IO.File]::Replace($file.Staged,$file.Target,$backup) }
            else { [IO.File]::Move($file.Staged,$file.Target) }
            $applied.Add(@{Target=$file.Target;Backup=$backup;Existed=$existed})
        }
    } catch {
        $publishFailure = $_
        $restoreErrors = @()
        for ($index = $applied.Count - 1; $index -ge 0; $index--) {
            $file = $applied[$index]
            try {
                if ($file.Existed) { [IO.File]::Replace($file.Backup,$file.Target,[NullString]::Value) }
                else { Remove-Item -LiteralPath $file.Target -Force }
            } catch { $restoreErrors += $file.Backup }
        }
        if ($restoreErrors.Count) { throw ('Rule update rollback failed. Backups retained: ' + ($restoreErrors -join ', ')) }
        throw $publishFailure
    }
    foreach ($file in $applied) { Remove-Item -LiteralPath $file.Backup -Force -ErrorAction SilentlyContinue }
}
