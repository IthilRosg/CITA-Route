function Invoke-WireSock([string[]]$CliArguments) {
    $result = & $cli @CliArguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and $CliArguments[0] -eq 'disconnect') {
        # Some WireSock versions return 1 even after a completed disconnect.
        # Accept it only when an independent status query confirms the postcondition.
        $statusResult = & $cli status 2>&1
        $statusExitCode = $LASTEXITCODE
        $statusText = ($statusResult -join ' ').Trim()
        $disconnectedPattern = '(?i)^(Not connected|No connection|\u041d\u0435\u0442 \u043f\u043e\u0434\u043a\u043b\u044e\u0447\u0435\u043d\u0438\u044f)$'
        if ($statusExitCode -eq 0 -and $statusText -match $disconnectedPattern) { return $result }
    }
    if ($exitCode -ne 0) { throw "WireSock CLI failed: $($result -join ' ')" }
    return $result
}
