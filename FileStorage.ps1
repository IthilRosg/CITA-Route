function Read-JsonFile([string]$Path) {
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader=New-Object IO.StreamReader($stream)
    try { $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
}

function Write-JsonFile($Path, $Value) {
    # Same directory/volume, without repeating a long operation filename.
    $temporaryPath = [IO.Path]::Combine([IO.Path]::GetDirectoryName($Path),('.'+[guid]::NewGuid().ToString('N')+'.tmp'))
    try {
        [IO.File]::WriteAllText($temporaryPath, ($Value | ConvertTo-Json -Depth 30), (New-Object Text.UTF8Encoding($false)))
        # Windows PowerShell 5.1 converts $null to an empty path for this overload.
        if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporaryPath,$Path,[NullString]::Value) }
        else { [IO.File]::Move($temporaryPath,$Path) }
    } finally { Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue }
}

function Test-ConnectionCancellation {
    $operationVariable=Get-Variable -Name OperationId -ErrorAction SilentlyContinue
    return $operationVariable -and $operationVariable.Value -and (Test-Path -LiteralPath (Join-Path $runtime ('operation-'+$operationVariable.Value+'.cancel')))
}

function Assert-ConnectionOperation {
    if(Test-ConnectionCancellation){throw 'Connection operation cancelled.'}
}

function Write-ConnectionProgress([string]$Phase) {
    Assert-ConnectionOperation
    if($OperationId){
        Write-JsonFile (Join-Path $runtime ('operation-'+$OperationId+'.progress.json')) @{
            id=$OperationId;phase=$Phase;updatedUtc=[DateTime]::UtcNow.ToString('o')
        }
    }
}

function Write-ConnectionEvidence([string]$Root,[string]$Event,$Session=$null) {
    if($Event -notin @('StartRequested','StartVerified','StartFailed','StopRequested','Stopped','ReconnectRequested','ReconnectVerified','ReconnectFailed','BridgePaused','BridgeStarted','BridgeListenerMissing','WatchObservationReset')){return}
    try {
        $directory=Join-Path $Root 'logs';$path=Join-Path $directory 'lifecycle-events.jsonl'
        if(-not(Test-Path -LiteralPath $directory)){return}
        if((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint){return}
        if((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)){return}
        $id=[guid]::Empty;$sessionId=''
        if($Session -and [guid]::TryParse([string]$Session.id,[ref]$id)){$sessionId=$id.ToString('N')}
        $entry=@{schema=1;utc=[DateTime]::UtcNow.ToString('o');event=$Event;sessionId=$sessionId;bridgeGeneration=[int]$Session.bridgeGeneration;running=[bool]$Session.running;bridgeReady=[bool]$Session.bridgeReady}
        $stream=[IO.File]::Open($path,'OpenOrCreate','ReadWrite','Read')
        try {
            if($stream.Length -gt 1MB){
                $keep=New-Object byte[] 131072;$stream.Position=$stream.Length-$keep.Length;$count=$stream.Read($keep,0,$keep.Length)
                $start=[Array]::IndexOf($keep,[byte]10)+1
                $stream.SetLength(0);$stream.Position=0
                if($start -gt 0 -and $start -lt $count){$stream.Write($keep,$start,$count-$start)}
            }
            $stream.Position=$stream.Length;$bytes=[Text.Encoding]::UTF8.GetBytes(($entry|ConvertTo-Json -Compress)+"`n");$stream.Write($bytes,0,$bytes.Length)
        }finally{$stream.Dispose()}
    }catch{ # Evidence must never turn a successful disconnect into a failure.
    }
}
