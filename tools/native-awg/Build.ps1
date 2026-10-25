$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if((& go version) -notmatch 'go1\.26\.3 '){throw 'Native AWG lab requires the reviewed Go 1.26.3 toolchain'}
$env:GOMODCACHE=Join-Path $root 'downloads\go-modcache';$env:GOCACHE=Join-Path $root 'downloads\go-buildcache'
$env:GOTOOLCHAIN='local';$env:GOWORK='off';$env:CGO_ENABLED='0';$env:GOOS='windows';$env:GOARCH='amd64'
Push-Location $PSScriptRoot
try{
 go mod verify;if($LASTEXITCODE -ne 0){throw 'Dependency verification failed'}
 $packages=@(& go list ./...);if($LASTEXITCODE -ne 0 -or $packages.Count -ne 1 -or $packages[0] -ne 'cita/native-awg-lab'){throw 'Review stable test executable paths before testing new packages'}
 $testFolder=Join-Path $root 'runtime/native-awg-unit';New-Item -ItemType Directory -Path $testFolder -Force|Out-Null
 $testImage=Join-Path $testFolder 'native-awg-lab.test.exe'
 go test -c -o $testImage .;if($LASTEXITCODE -ne 0){throw 'Native AWG lab test build failed'}
 & (Join-Path $PSScriptRoot 'Set-TestFirewall.ps1') -Image $testImage
 & $testImage '-test.timeout=60s';if($LASTEXITCODE -ne 0){throw 'Native AWG lab tests failed'}
 go vet ./...;if($LASTEXITCODE -ne 0){throw 'Native AWG lab analysis failed'}
 go build -mod=readonly -buildvcs=false -trimpath -o (Join-Path $root 'runtime\native-awg-lab.exe') .
 if($LASTEXITCODE -ne 0){throw 'Native AWG lab build failed'}
}finally{Pop-Location}
