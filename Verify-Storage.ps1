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
    # The final path fits Windows PowerShell 5.1's legacy limit, while appending
    # a GUID to its whole filename would exceed it. Temp names must stay short.
    $longDirectory=Join-Path $testFolder ('p'*[math]::Max(1,180-$testFolder.Length-1))
    [void][IO.Directory]::CreateDirectory($longDirectory)
    $longPath=Join-Path $longDirectory ('operation-'+[guid]::NewGuid().ToString('N')+'.progress.json')
    if($longPath.Length -ge 260){throw 'Storage fixture root is too long for the declared legacy-path scenario.'}
    Write-JsonFile $longPath @{id='long-first'};Write-JsonFile $longPath @{id='long-second'}
    if(([IO.File]::ReadAllText($longPath)|ConvertFrom-Json).id -ne 'long-second'){throw 'A valid long target path could not be written atomically.'}
    if(@(Get-ChildItem -LiteralPath $longDirectory -Filter '*.tmp').Count){throw 'Long-path temporary state leaked.'}
    Write-Output ('PASS: atomic first write, replacement, failure preservation, temporary cleanup; PowerShell ' + $PSVersionTable.PSVersion)
} finally {
    $resolvedTestFolder = [IO.Path]::GetFullPath($testFolder)
    $allowedPrefix = [IO.Path]::GetFullPath($testBase).TrimEnd('\') + '\storage-tests-'
    if (-not $resolvedTestFolder.StartsWith($allowedPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolvedTestFolder -Recurse -Force
}
