function Invoke-WireSock([string[]]$CliArguments) {
    $result = & $cli @CliArguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and $CliArguments[0] -eq 'disconnect') {
        # Some WireSock versions return 1 even after a completed disconnect.
        # Accept it only when an independent status query confirms the postcondition.
        $statusResult = & $cli status 2>&1
        $statusExitCode = $LASTEXITCODE
        $statusText = ($statusResult -join ' ').Trim()
        $disconnectedPattern = '(?i)^(Not connected|NotConnected|No connection|Disconnected|\u041e\u0442\u043a\u043b\u044e\u0447\u0435\u043d\u043e|\u041d\u0435\u0442 \u043f\u043e\u0434\u043a\u043b\u044e\u0447\u0435\u043d\u0438\u044f)$'
        if ($statusExitCode -eq 0 -and $statusText -match $disconnectedPattern) { return $result }
    }
    if ($exitCode -ne 0) { throw "WireSock CLI failed: $($result -join ' ')" }
    return $result
}

function Read-HelperOutput([string]$Image,[string]$Arguments) {
    # Status must not hold the watchdog indefinitely. Do not log raw CLI output.
    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName=$Image;$info.Arguments=$Arguments;$info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $process=New-Object Diagnostics.Process
    $process.StartInfo=$info
    $started=$false
    try {
        if(-not $process.Start()){throw 'WireSock status did not start.'}
        $started=$true
        $output=New-Object IO.MemoryStream;$errorOutput=New-Object IO.MemoryStream
        try {
            $stdout=$process.StandardOutput.BaseStream.CopyToAsync($output)
            $stderr=$process.StandardError.BaseStream.CopyToAsync($errorOutput)
            if(-not $process.WaitForExit(4000)){throw 'WireSock status timed out.'}
            if(-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout,$stderr),1000)){throw 'WireSock status output timed out.'}
            if($process.ExitCode -ne 0 -or $output.Length -gt 65536){throw 'WireSock status is unavailable.'}
            try{return (New-Object Text.UTF8Encoding($false,$true)).GetString($output.ToArray())}
            catch [Text.DecoderFallbackException]{return [Text.Encoding]::GetEncoding(866).GetString($output.ToArray())}
        } finally {$output.Dispose();$errorOutput.Dispose()}
    } finally {
        if($started -and -not $process.HasExited){try{$process.Kill();[void]$process.WaitForExit(1000)}catch{}}
        $process.Dispose()
    }
}

function Read-WireSockStatus { return Read-HelperOutput $cli 'status' }

function Test-WireSockManagedProfile([string]$Status,[string]$Profile) {
    # CLI stdout uses CRLF on Windows; multiline $ leaves the CR in the line.
    $names=[regex]::Matches($Status,'(?im)^.*?\b(?:profile|\u043f\u0440\u043e\u0444\u0438\u043b\u044f)[ \t]+([^\r\n]+?)[ \t]*\r?$')
    return $names.Count -eq 1 -and [string]::Equals($names[0].Groups[1].Value.Trim(),$Profile,[StringComparison]::OrdinalIgnoreCase)
}

function Get-WireSockState([string]$Status,[string]$Profile) {
    $text=$Status.Trim()
    if($text -match '(?i)^(Not connected|NotConnected|No connection|Disconnected|\u041e\u0442\u043a\u043b\u044e\u0447\u0435\u043d\u043e|\u041d\u0435\u0442 \u043f\u043e\u0434\u043a\u043b\u044e\u0447\u0435\u043d\u0438\u044f)$'){return 'Disconnected'}
    # Match a complete profile identifier, never a substring such as "...-copy".
    $managed=Test-WireSockManagedProfile $text $Profile
    $hasProfile=$text -match '(?im)^.*(?:profile|\u043f\u0440\u043e\u0444\u0438\u043b\u044f)\s+\S.*$'
    if($hasProfile -and -not $managed){return 'Foreign'}
    if($managed -and $text -match '(?i)\b(Disconnected|NotConnected|Not connected|No connection|\u041d\u0435\s+\u043f\u043e\u0434\u043a\u043b\u044e\u0447\u0435\u043d\u043e)\b'){return 'Disconnected'}
    if($text -match '(?i)\b(Connecting|Establishing connection|Disconnecting)\b|\u0423\u0441\u0442\u0430\u043d\u0430\u0432\u043b\u0438\u0432\u0430\u0435\u0442\u0441\u044f \u0441\u043e\u0435\u0434\u0438\u043d\u0435\u043d\u0438\u0435|\u041f\u043e\u0434\u043a\u043b\u044e\u0447\u0435\u043d\u0438\u0435|\u041e\u0442\u043a\u043b\u044e\u0447\u0435\u043d\u0438\u0435'){return 'Transitioning'}
    if($managed -and $text -match '(?i)(\bConnected to|\u041f\u043e\u0434\u043a\u043b\u044e\u0447\u0435\u043d\u043e \u043a \u0441\u0435\u0440\u0432\u0435\u0440\u0443)'){return 'Connected'}
    return 'Unknown'
}

function Get-WireSockObservation {
    $managed=$false
    try {$status=Read-WireSockStatus;$state=Get-WireSockState $status $smartProfile;$managed=Test-WireSockManagedProfile $status $smartProfile}
    catch {$state='Unknown'}
    return [pscustomobject]@{state=$state;managed=$managed;checkedUtc=[DateTime]::UtcNow.ToString('o')}
}

function Disconnect-ManagedWireSock {
    $observation=Get-WireSockObservation
    if($observation.state -in @('Disconnected','Foreign')){return}
    if(-not $observation.managed -or $observation.state -notin @('Connected','Transitioning')){
        throw 'Не удалось подтвердить отключение WireSock. Открой WireSock и проверь подключение.'
    }
    Invoke-WireSock @('disconnect') | Out-Null
    # CLI exit 0 alone is not the postcondition. A late/manual profile switch is
    # reported for attention; it must never trigger a second blind disconnect.
    if((Get-WireSockObservation).state -ne 'Disconnected'){
        throw 'WireSock не подтвердил отключение или сменил профиль. Проверь подключение в WireSock.'
    }
}
