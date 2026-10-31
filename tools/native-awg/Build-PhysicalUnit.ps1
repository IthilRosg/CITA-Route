$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $root 'NativeSourceProvenance.ps1')
$receiptPath=Join-Path $root 'runtime/native-corresponding-source.zip.receipt.json'
$source=Get-Content $receiptPath -Raw|ConvertFrom-Json;$receiptHash=(Get-FileHash $receiptPath).Hash
if(!$source.rebuildVerified -or !$source.workspaceInputs){throw 'Verified source required'}
Assert-NativeInventory $source.workspaceInputs @(Get-NativeWorkspaceInventory $root)
$ownerHash=(Get-FileHash (Join-Path $root 'runtime/native-awg-lab.exe')).Hash
if($ownerHash -ne $source.verifiedBinaries.'native-owner.exe'){throw 'Owner binary differs from verified source'}
if((& go version) -notmatch 'go1\.26\.3 '){throw 'Reviewed Go version required'}
$env:GOMODCACHE=Join-Path $root 'downloads/go-modcache';$env:GOCACHE=Join-Path $root 'downloads/go-buildcache';$env:GOTOOLCHAIN='local';$env:GOWORK='off';$env:CGO_ENABLED='0';$env:GOOS='windows';$env:GOARCH='amd64'
$output=Join-Path $root 'runtime/cita-owner-physical.test.exe'
Push-Location $PSScriptRoot
try{& go test -mod=readonly -c -o $output .;if($LASTEXITCODE -ne 0){throw 'Owner unit executable build failed'}}finally{Pop-Location}
Assert-NativeInventory $source.workspaceInputs @(Get-NativeWorkspaceInventory $root)
if((Get-FileHash $receiptPath).Hash -ne $receiptHash){throw 'Source receipt changed during test build'}
@{schema=1;sourceReceiptSha256=$receiptHash;ownerSha256=$ownerHash;testSha256=(Get-FileHash $output).Hash;goVersion=(& go version);scope='owner tests only; no network proof'}|ConvertTo-Json|Set-Content (Join-Path $root 'runtime/cita-owner-physical.test.receipt.json') -Encoding utf8
'PASS: owner test binary tied to current verified source and owner.'
