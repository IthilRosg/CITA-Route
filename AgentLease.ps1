function Assert-NoAgentRun {
    $journal = Join-Path $env:ProgramData 'RoutePilot\run.json'
    if (-not (Test-Path -LiteralPath $journal)) { return }
    $stream = [IO.File]::Open($journal,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader = New-Object IO.StreamReader($stream)
    try { $state = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($state.Phase -in @('Armed','Testing','Restoring','RecoveryFailed')) {
        throw 'An autonomous diagnostic run owns the network. Finish recovery in RoutePilot before changing routing.'
    }
}
function Invoke-AgentCoordinator([ValidateSet('begin-operation','end-operation')][string]$Command) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'RoutePilot.Rescue.v1', [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous, [Security.Principal.TokenImpersonationLevel]::Identification)
    try {
        $pipe.Connect(5000)
        $request = [Text.Encoding]::UTF8.GetBytes((@{Command=$Command} | ConvertTo-Json -Compress) + "`n")
        $pipe.Write($request,0,$request.Length); $pipe.Flush()
        $reader = New-Object IO.StreamReader($pipe)
        $pending = $reader.ReadLineAsync()
        if (-not $pending.Wait(5000)) { throw 'The rescue coordinator did not respond.' }
        $response = $pending.Result | ConvertFrom-Json
        if (-not $response.Success) { throw $response.Message }
    } finally { $pipe.Dispose() }
}
