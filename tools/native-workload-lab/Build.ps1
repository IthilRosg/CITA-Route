$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if((& go version) -notmatch 'go1\.26\.3 '){throw 'Reviewed Go 1.26.3 required'}
$env:GOMODCACHE=Join-Path $root 'downloads/go-modcache';$env:GOCACHE=Join-Path $root 'downloads/go-buildcache';$env:GOTOOLCHAIN='local';$env:GOWORK='off';$env:CGO_ENABLED='0'
Push-Location $PSScriptRoot
try{
 go mod verify;if($LASTEXITCODE -ne 0){throw 'Workload dependency integrity failed'}
 go test ./...;if($LASTEXITCODE -ne 0){throw 'Workload tests failed'}
 go vet ./...;if($LASTEXITCODE -ne 0){throw 'Workload vet failed'}
 foreach($target in @(@('windows','native-workload-lab.exe'),@('linux','native-workload-server'))){$env:GOOS=$target[0];$env:GOARCH='amd64';go build -mod=readonly -trimpath -o (Join-Path $root ('runtime/'+$target[1])) .;if($LASTEXITCODE -ne 0){throw 'Workload build failed'}}
}finally{Pop-Location}
