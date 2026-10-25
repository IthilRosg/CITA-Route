# This helper only stops the protected native session. It never changes adapters,
# removes profiles or falls back to legacy process termination.
function Invoke-NativeMaintenanceRpc([string]$Command,[string]$Operation='') {
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.','RoutePilot.Rescue.v1',[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous,[Security.Principal.TokenImpersonationLevel]::Identification)
    try {
        $pipe.Connect(5000)
        $bytes=[Text.Encoding]::UTF8.GetBytes((@{Command=$Command;RunId=$Operation}|ConvertTo-Json -Compress)+"`n")
        $pipe.Write($bytes,0,$bytes.Length);$pipe.Flush()
        $reader=[IO.StreamReader]::new($pipe);$pending=$reader.ReadLineAsync()
        if(-not $pending.Wait(5000)){throw 'Native maintenance coordinator timed out.'}
        $reply=$pending.Result|ConvertFrom-Json
        if(-not $reply.Success){throw 'Native maintenance was refused. No components were replaced.'}
        return $reply
    } finally {$pipe.Dispose()}
}
function Stop-NativeForMaintenance([string]$NativeRoot=(Join-Path $env:ProgramFiles 'RoutePilot\Agent\native')) {
    $settingsPath=Join-Path $NativeRoot 'service.json'
    if(-not(Test-Path -LiteralPath $settingsPath)){return}
    $settings=[IO.File]::ReadAllText($settingsPath)|ConvertFrom-Json
    if($settings.Enabled -isnot [bool]){throw 'Invalid native settings. Update postponed.'}
    if(-not $settings.Enabled){return}
    $state=(Invoke-NativeMaintenanceRpc 'status').Native
    if(-not $state -or $state.Phase -eq 'Unavailable'){throw 'Cannot confirm native session state. Update postponed.'}
    if($state.Desired -or $state.Phase -ne 'Disconnected') {
        $operation=[string]$state.Operation
        if($operation -notmatch '^[a-fA-F0-9]{32}$'){throw 'Native session identity unavailable. Update postponed.'}
        $null=Invoke-NativeMaintenanceRpc 'native-stop' $operation
        $deadline=[DateTime]::UtcNow.AddSeconds(45)
        do {
            $state=(Invoke-NativeMaintenanceRpc 'status').Native
            if(-not $state -or $state.Operation -ne $operation){throw 'Native session changed during maintenance. Update postponed.'}
            if($state.Phase -eq 'Disconnected' -and -not $state.Desired){return}
            Start-Sleep -Milliseconds 250
        } while([DateTime]::UtcNow -lt $deadline)
        throw 'Native disconnect did not finish. Update postponed; files remain unchanged.'
    }
}
