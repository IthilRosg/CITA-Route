$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'FileStorage.ps1')
$testBase = Join-Path $PSScriptRoot 'runtime'
$testFolder = Join-Path $testBase ('storage-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testFolder | Out-Null
try {
    $path = Join-Path $testFolder 'state.json'
    Write-JsonFile $path @{running=$true;id='first'}
    Write-JsonFile $path @{running=$false;id='second'}
    $state = [IO.File]::ReadAllText($path) | ConvertFrom-Json
    if ($state.running -or $state.id -ne 'second') { throw 'Repeated JSON write did not replace the file.' }
    # Exercise a real replacement failure: original data must survive, temporary files must not.
    $reader = [IO.File]::Open($path,'Open','Read','Read')
    $rejected = $false
    try { Write-JsonFile $path @{id='must-not-replace'} } catch { $rejected = $true }
    finally { $reader.Dispose() }
    if (-not $rejected -or ([IO.File]::ReadAllText($path) | ConvertFrom-Json).id -ne 'second') {
        throw 'Failed replacement did not preserve the original state.'
    }
    if (@(Get-ChildItem -LiteralPath $testFolder -Filter '*.tmp').Count) { throw 'Temporary state files were leaked.' }
    Write-Output ('PASS: atomic first write, replacement, failure preservation, temporary cleanup; PowerShell ' + $PSVersionTable.PSVersion)
} finally {
    $resolvedTestFolder = [IO.Path]::GetFullPath($testFolder)
    $allowedPrefix = [IO.Path]::GetFullPath($testBase).TrimEnd('\') + '\storage-tests-'
    if (-not $resolvedTestFolder.StartsWith($allowedPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolvedTestFolder -Recurse -Force
}
