$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if((& go version) -notmatch 'go1\.26\.3 '){throw 'Native AWG lab requires the reviewed Go 1.26.3 toolchain'}
$env:GOMODCACHE=Join-Path $root 'downloads\go-modcache';$env:GOCACHE=Join-Path $root 'downloads\go-buildcache'
$env:GOTOOLCHAIN='local';$env:GOWORK='off';$env:CGO_ENABLED='0';$env:GOOS='windows';$env:GOARCH='amd64'
Push-Location $PSScriptRoot
try{
 go mod verify;if($LASTEXITCODE -ne 0){throw 'Dependency verification failed'}
 go test ./...;if($LASTEXITCODE -ne 0){throw 'Native AWG lab tests failed'}
 go vet ./...;if($LASTEXITCODE -ne 0){throw 'Native AWG lab analysis failed'}
 go build -mod=readonly -trimpath -o (Join-Path $root 'runtime\native-awg-lab.exe') .
 if($LASTEXITCODE -ne 0){throw 'Native AWG lab build failed'}
}finally{Pop-Location}
