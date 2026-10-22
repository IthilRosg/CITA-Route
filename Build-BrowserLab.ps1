#requires -Version 7.0
$ErrorActionPreference='Stop'
$labProject=$PSScriptRoot
$labGo=Get-Command go.exe -ErrorAction Stop
$labGoVersion=& $labGo.Source version
if($LASTEXITCODE -ne 0 -or $labGoVersion -notmatch 'go1\.26\.3 '){throw 'Browser fixture requires the pinned Go 1.26.3 toolchain.'}
$labReceipt=Get-Content (Join-Path $labProject 'runtime\router-engine-build.json') -Raw|ConvertFrom-Json
$labZip=Join-Path $labProject 'distribution-licenses\cita-routing-core-1.14.3-cita.3-source.zip'
if((Get-FileHash -LiteralPath $labZip).Hash -ne $labReceipt.sourceSha256){throw 'Pinned core source archive differs'}
$labStage=Join-Path $labProject ('downloads\browser-lab-'+[guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $labZip -DestinationPath $labStage
$labCore=Join-Path $labStage 'sing-box-1.14.3';$labCommand=Join-Path $labCore 'cmd\cita-browser-lab'
[void][IO.Directory]::CreateDirectory($labCommand)
Copy-Item -LiteralPath (Join-Path $labProject 'tools\browser-routing-lab\main.go') -Destination (Join-Path $labCommand 'main.go')
$env:GOMODCACHE=Join-Path $labProject 'downloads\go-modcache';$env:GOCACHE=Join-Path $labProject 'downloads\go-buildcache'
$env:GOWORK='off';$env:GOTOOLCHAIN='local';$env:GOPROXY='off';$env:CGO_ENABLED='0';$env:GOOS='windows';$env:GOARCH='amd64'
$labOutput=Join-Path $labProject 'runtime\browser-lab-build';[void][IO.Directory]::CreateDirectory($labOutput)
Push-Location $labCore
try{& $labGo.Source build -mod=readonly -buildvcs=false -trimpath -tags 'with_gvisor,with_quic,with_wireguard,with_utls,with_clash_api,badlinkname,tfogo_checklinkname0' -ldflags '-s -w -buildid= -checklinkname=0' -o (Join-Path $labOutput 'Cita.BrowserLab.exe') ./cmd/cita-browser-lab;if($LASTEXITCODE -ne 0){throw 'Browser lab build failed'}}finally{Pop-Location}
@{schema=1;go=$labGoVersion;sourceSha256=$labReceipt.sourceSha256;fixtureSourceSha256=(Get-FileHash (Join-Path $labProject 'tools\browser-routing-lab\main.go')).Hash;helperSha256=(Get-FileHash (Join-Path $labOutput 'Cita.BrowserLab.exe')).Hash}|ConvertTo-Json|Set-Content (Join-Path $labOutput 'receipt.json') -Encoding UTF8
Write-Output 'Owned VMware-only browser/TUN fixture built.'
