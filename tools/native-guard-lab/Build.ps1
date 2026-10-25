$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if((& go version) -notmatch 'go1\.26\.3 '){throw 'Guard lab requires the reviewed Go toolchain'}
$env:GOMODCACHE=Join-Path $root 'downloads/go-modcache';$env:GOCACHE=Join-Path $root 'downloads/go-buildcache';$env:GOTOOLCHAIN='local';$env:GOWORK='off';$env:CGO_ENABLED='0';$env:GOOS='windows';$env:GOARCH='amd64'
Push-Location $PSScriptRoot
try{
 & go mod verify;if($LASTEXITCODE -ne 0){throw 'Guard dependency verification failed'}
 & go test -mod=readonly ./...;if($LASTEXITCODE -ne 0){throw 'Guard unit tests failed'}
 & go vet -mod=readonly ./...;if($LASTEXITCODE -ne 0){throw 'Guard static analysis failed'}
 & go build -mod=readonly -buildvcs=false -trimpath -o (Join-Path $root 'runtime/native-guard-lab.exe') .;if($LASTEXITCODE -ne 0){throw 'Guard build failed'}
}finally{Pop-Location}
