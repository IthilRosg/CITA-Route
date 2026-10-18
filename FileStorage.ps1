function Read-JsonFile([string]$Path) {
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader=New-Object IO.StreamReader($stream)
    try { $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
}

function Write-JsonFile($Path, $Value) {
    $temporaryPath = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporaryPath, ($Value | ConvertTo-Json -Depth 30), (New-Object Text.UTF8Encoding($false)))
        # Windows PowerShell 5.1 converts $null to an empty path for this overload.
        if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporaryPath,$Path,[NullString]::Value) }
        else { [IO.File]::Move($temporaryPath,$Path) }
    } finally { Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue }
}
