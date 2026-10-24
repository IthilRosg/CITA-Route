$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if((& go version) -notmatch 'go1\.26\.3 '){throw 'Reviewed Go 1.26.3 required'}
$source=Join-Path $root 'distribution-licenses\cita-routing-core-1.14.3-cita.5-source.zip'
if((Get-FileHash -LiteralPath $source).Hash -ne 'ECD1BC4D44C42BFD8D94148F47BA821C999DD1EDC13604324BEBE01785A9564D'){throw 'Pinned corresponding source differs'}
$stage=Join-Path $root ('downloads\native-retention-core-'+[guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $source -DestinationPath $stage
$core=Join-Path $stage 'sing-box-1.14.3'
& (Join-Path $PSScriptRoot 'core-patches\Apply.ps1') -Core $core
& (Join-Path $PSScriptRoot 'core-patches\Apply-TunBarrier.ps1') -Core $core
& (Join-Path $PSScriptRoot 'core-patches\Apply-UdpGuard.ps1') -Sing (Join-Path $stage 'sing-cita')
foreach($test in @(@('lease_test.go','common\citaretain\lease_test.go'),@('route_retention_test.go','route\cita_retention_test.go'),@('socks_retention_test.go','protocol\socks\cita_retention_test.go'))){Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('core-patches\'+$test[0])) -Destination (Join-Path $core $test[1])}
$env:GOMODCACHE=Join-Path $root 'downloads\go-modcache';$env:GOCACHE=Join-Path $root 'downloads\go-buildcache';$env:GOTOOLCHAIN='local';$env:GOWORK='off';$env:CGO_ENABLED='0';$env:GOOS='windows';$env:GOARCH='amd64'
$target=Join-Path $root 'runtime\native-core-lab.exe'
Push-Location (Join-Path $stage 'sing-cita')
try{& gofmt -w protocol/socks/cita_udp_guard.go protocol/socks/cita_udp_guard_test.go protocol/socks/handshake.go;& go test -mod=readonly -run '^TestCitaUDPGuard' ./protocol/socks;if($LASTEXITCODE -ne 0){throw 'Core UDP guard tests failed'};& go vet -mod=readonly ./protocol/socks;if($LASTEXITCODE -ne 0){throw 'Core UDP guard analysis failed'}}finally{Pop-Location}
Push-Location $core
try{
 $files=@('common/citaretain/lease.go','common/citaretain/lease_test.go','route/cita_retention.go','route/cita_retention_test.go','protocol/socks/cita_retention.go','protocol/socks/cita_retention_test.go','option/cita_retention.go','option/simple.go','route/network.go','common/dialer/default.go','protocol/socks/outbound.go')
 & gofmt -w @files;if($LASTEXITCODE -ne 0){throw 'Core patch formatting failed'}
 & go test -mod=readonly -run '^TestCitaRetention' -count=1 -timeout 60s ./common/citaretain ./route ./protocol/socks;if($LASTEXITCODE -ne 0){throw 'Core retention tests failed'}
 & go vet -mod=readonly ./common/citaretain ./route ./protocol/socks;if($LASTEXITCODE -ne 0){throw 'Core retention analysis failed'}
 & go build -mod=readonly -buildvcs=false -trimpath -tags 'with_gvisor,with_quic,with_wireguard,with_utls,with_clash_api,badlinkname,tfogo_checklinkname0' -ldflags '-s -w -buildid= -X github.com/sagernet/sing-box/constant.Version=1.14.3-cita-native-lab.3 -checklinkname=0' -o $target ./cmd/sing-box
 if($LASTEXITCODE -ne 0){throw 'Native retention lab core build failed'}
 $patches=@(Get-ChildItem (Join-Path $PSScriptRoot 'core-patches') -File|Sort-Object Name|ForEach-Object {@{file=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
 @{schema=1;sourceSha256=(Get-FileHash $source).Hash;binarySha256=(Get-FileHash $target).Hash;patches=$patches;stage=$stage;production=$false}|ConvertTo-Json -Depth 4|Set-Content (Join-Path $root 'runtime\native-core-lab-build.json')
}finally{Pop-Location}
