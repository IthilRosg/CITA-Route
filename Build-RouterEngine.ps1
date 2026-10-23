[CmdletBinding()]
param([switch]$PublishRuntime)
$ErrorActionPreference='Stop'
$project=$PSScriptRoot
$go=Get-Command go.exe -ErrorAction Stop
$env:GOMODCACHE=Join-Path $project 'downloads\go-modcache'
$env:GOCACHE=Join-Path $project 'downloads\go-buildcache'
$env:GOTOOLCHAIN='local';$env:CGO_ENABLED='0';$env:GOOS='windows';$env:GOARCH='amd64';$env:GOWORK='off'
$version=& $go.Source version
if($LASTEXITCODE -ne 0 -or $version -notmatch 'go1\.26\.3 '){throw 'CITA routing core requires the pinned Go 1.26.3 toolchain.'}
$inputs=@('Build-RouterEngine.ps1','engine-patches\cita_dns_windows.go','engine-patches\cita_dns_windows_test.go','engine-patches\cita_fallback.go','engine-patches\cita_fallback_test.go','engine-patches\cita_quic_domain.go','engine-patches\cita_quic_domain_test.go','engine-patches\cita_first_use.go','engine-patches\cita_first_use_test.go','engine-patches\cita_windows_read_fix.ps1','engine-patches\cita_windows_read_test.go')
$recipe=($inputs | ForEach-Object {(Get-FileHash -LiteralPath (Join-Path $project $_)).Hash}) -join ':'
$receipt=Join-Path $project 'runtime\router-engine-build.json';$target=Join-Path $project 'runtime\router-engine-build\smart-router.exe'
New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force|Out-Null
function Publish-Core {
    if($PublishRuntime){foreach($name in @('smart-router.exe','vpn-bridge.exe')){Copy-Item -LiteralPath $target -Destination (Join-Path $project ('runtime\'+$name)) -Force}}
}
if((Test-Path -LiteralPath $receipt) -and (Test-Path -LiteralPath $target)){
    $cached=Get-Content -LiteralPath $receipt -Raw|ConvertFrom-Json
    $cachedSource=Join-Path $project 'distribution-licenses\cita-routing-core-1.14.3-cita.5-source.zip'
    if($cached.recipe -eq $recipe -and $cached.sha256 -eq (Get-FileHash -LiteralPath $target).Hash -and
        (Test-Path -LiteralPath $cachedSource) -and $cached.sourceSha256 -eq (Get-FileHash -LiteralPath $cachedSource).Hash){Publish-Core;Write-Output 'Verified CITA routing core build cache.';return}
}
$upstream=Join-Path $project 'distribution-licenses\sing-box-1.14.3-source.zip'
if((Get-FileHash -LiteralPath $upstream).Hash -ne '5BF11EEB2F5B93377166AF1ECDCF24DD2C4BE3C92BCB43C09D95EDD01EB85949'){throw 'Upstream source archive differs from the pin.'}
$stage=Join-Path $project ('downloads\cita-core-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force|Out-Null
Expand-Archive -LiteralPath $upstream -DestinationPath $stage
$core=Join-Path $stage 'sing-box-1.14.3';$tun=Join-Path $stage 'sing-tun-cita';$sing=Join-Path $stage 'sing-cita'
Push-Location $core
try {
    $metadata=& $go.Source mod download -json github.com/sagernet/sing-tun@v0.9.7-0.20261009020158-bca4bf029237
    if($LASTEXITCODE -ne 0){throw 'Pinned TUN source unavailable.'}
    $module=($metadata -join "`n")|ConvertFrom-Json
    if($module.Sum -ne 'h1:B9tUcRf1FAatPyzN8sKEvIHMvKNkFMBZikeIOL4gqtQ='){throw 'Pinned TUN source checksum differs.'}
    Copy-Item -LiteralPath $module.Dir -Destination $tun -Recurse
    Get-ChildItem -LiteralPath $tun -Recurse -File|ForEach-Object {$_.IsReadOnly=$false}
    foreach($name in @('cita_dns_windows.go','cita_dns_windows_test.go')){Copy-Item -LiteralPath (Join-Path $project ('engine-patches\'+$name)) -Destination (Join-Path $tun $name)}
    $socketMetadata=& $go.Source mod download -json github.com/sagernet/sing@v0.9.7-0.20260929150544-0ad23b637bd4
    if($LASTEXITCODE -ne 0){throw 'Pinned socket source unavailable.'}
    $socketModule=($socketMetadata -join "`n")|ConvertFrom-Json
    if($socketModule.Sum -ne 'h1:d4WT3KO4zhp9qzPHUZCKqCP+ZmP1GXBsAmReqlnE4zA='){throw 'Pinned socket source checksum differs.'}
    Copy-Item -LiteralPath $socketModule.Dir -Destination $sing -Recurse
    Get-ChildItem -LiteralPath $sing -Recurse -File|ForEach-Object {$_.IsReadOnly=$false}
    . (Join-Path $project 'engine-patches\cita_windows_read_fix.ps1')
    Apply-CitaWindowsReadFix $sing
    Copy-Item -LiteralPath (Join-Path $project 'engine-patches\cita_windows_read_test.go') -Destination (Join-Path $sing 'common\bufio\cita_windows_read_test.go')
    & $go.Source mod edit '-replace=github.com/sagernet/sing=../sing-cita'
    if($LASTEXITCODE -ne 0){throw 'Windows socket source override failed.'}
    Push-Location $sing
    try{& $go.Source test -mod=readonly -run '^TestCitaWindowsReadDeadlineRetry$' -count=10 -timeout 20s ./common/bufio;if($LASTEXITCODE -ne 0){throw 'Windows socket deadline retry tests failed.'}}
    finally{Pop-Location}
    $file=Join-Path $tun 'tun_windows.go';$text=[IO.File]::ReadAllText($file).Replace("`r`n","`n")
    function Replace-Once([string]$Old,[string]$New){
        if(([regex]::Matches($script:text,[regex]::Escape($Old))).Count -ne 1){throw 'Pinned TUN patch context differs.'}
        $script:text=$script:text.Replace($Old,$New)
    }
    Replace-Once "`tfwpmSession uintptr`n" "`tfwpmSession uintptr`n`tcitaDNSStop chan struct{}`n`tcitaDNSDone chan struct{}`n"
    Replace-Once "`t`t}`n`t}`n`treturn nil`n}`n`nfunc (t *NativeTun) Read(" "`t`t}`n`t`tif t.options.DNSModeOrDefault() == DNSModeHijack {`n`t`t`tif err := t.startCitaDNS(subLayerKey); err != nil { return err }`n`t`t}`n`t}`n`treturn nil`n}`n`nfunc (t *NativeTun) Read("
    Replace-Once "`t`tt.close.Store(1)`n" "`t`tt.close.Store(1)`n`t`tif t.citaDNSStop != nil { close(t.citaDNSStop); <-t.citaDNSDone }`n"
    [IO.File]::WriteAllText($file,$text,[Text.UTF8Encoding]::new($false))
    foreach($name in @('cita_fallback.go','cita_fallback_test.go')){Copy-Item -LiteralPath (Join-Path $project ('engine-patches\'+$name)) -Destination (Join-Path $core ('dns\transport\'+$name))}
    foreach($name in @('cita_quic_domain.go','cita_quic_domain_test.go')){Copy-Item -LiteralPath (Join-Path $project ('engine-patches\'+$name)) -Destination (Join-Path $core ('route\'+$name))}
    foreach($name in @('cita_first_use.go','cita_first_use_test.go')){Copy-Item -LiteralPath (Join-Path $project ('engine-patches\'+$name)) -Destination (Join-Path $core ('route\'+$name))}
    $routerFile=Join-Path $core 'route\router.go';$routerText=[IO.File]::ReadAllText($routerFile).Replace("`r`n","`n")
    $routerAnchor="type Router struct {`n"
    if(([regex]::Matches($routerText,[regex]::Escape($routerAnchor))).Count -ne 1){throw 'Pinned QUIC router context differs.'}
    [IO.File]::WriteAllText($routerFile,$routerText.Replace($routerAnchor,$routerAnchor+"`tcitaQUICDomains citaQUICDomainCache`n"),[Text.UTF8Encoding]::new($false))
    $routeFile=Join-Path $core 'route\route.go';$routeText=[IO.File]::ReadAllText($routeFile).Replace("`r`n","`n")
    $quicAnchor="`t`t`tif newErr != nil {`n`t`t`t`tfatalErr = newErr`n`t`t`t`treturn`n`t`t`t}`n"
    if(([regex]::Matches($routeText,[regex]::Escape($quicAnchor))).Count -ne 1){throw 'Pinned QUIC sniff context differs.'}
    [IO.File]::WriteAllText($routeFile,$routeText.Replace($quicAnchor,$quicAnchor+"`t`t`tif inputPacketConn != nil { r.citaQUICDomain(metadata, packetBuffers) }`n"),[Text.UTF8Encoding]::new($false))
    $gateText=[IO.File]::ReadAllText($routeFile).Replace("`r`n","`n")
    $gateAnchor="`t`tselectedOutbound = defaultOutbound`n`t}`n"
    if(([regex]::Matches($gateText,[regex]::Escape($gateAnchor))).Count -ne 2){throw 'Pinned first-use fallback context differs.'}
    $gatePatch=@"
        selectedOutbound = defaultOutbound
        if route := r.citaFirstUse(ctx, &metadata); route != "" {
            if chosen, loaded := r.outbound.Outbound(route); loaded && common.Contains(chosen.Network(), N.NetworkTCP) {
                selectedOutbound = chosen
                metadata.TLSRecordFragment = route == "dpi"
            }
        }
    }
"@
    [IO.File]::WriteAllText($routeFile,$gateText.Remove($gateText.IndexOf($gateAnchor),$gateAnchor.Length).Insert($gateText.IndexOf($gateAnchor),$gatePatch+"`n"),[Text.UTF8Encoding]::new($false))
    $registry=Join-Path $core 'include\registry.go';$registryText=[IO.File]::ReadAllText($registry)
    $registryAnchor='transport.RegisterHTTPS(registry)'
    if(([regex]::Matches($registryText,[regex]::Escape($registryAnchor))).Count -ne 1){throw 'Pinned DNS registry context differs.'}
    [IO.File]::WriteAllText($registry,$registryText.Replace($registryAnchor,$registryAnchor+"`n`ttransport.RegisterCitaFallback(registry)"),[Text.UTF8Encoding]::new($false))
    # Clearly identify the derivative; upstream names remain only in attribution.
    foreach($patch in @(@('cmd\sing-box\cmd.go','"sing-box"','"cita-routing-core"'),@('cmd\sing-box\cmd_version.go','sing-box version ','CITA routing core version '))){
        $path=Join-Path $core $patch[0];$value=[IO.File]::ReadAllText($path)
        [IO.File]::WriteAllText($path,$value.Replace($patch[1],$patch[2]),[Text.UTF8Encoding]::new($false))
    }
    $entry=Join-Path $core 'cmd\sing-box\cmd_version.go'
    [IO.File]::WriteAllText($entry,[IO.File]::ReadAllText($entry).Replace('Print current version of sing-box','Print current version of CITA routing core'),[Text.UTF8Encoding]::new($false))
    $box=Join-Path $core 'box.go'
    [IO.File]::WriteAllText($box,[IO.File]::ReadAllText($box).Replace('sing-box started (','CITA routing core started ('),[Text.UTF8Encoding]::new($false))
    & $go.Source mod edit '-replace=github.com/sagernet/sing-tun=../sing-tun-cita'
    if($LASTEXITCODE -ne 0){throw 'TUN source override failed.'}
    Push-Location $tun
    try{& $go.Source test -mod=readonly -run '^TestCitaTailAddress$' .;if($LASTEXITCODE -ne 0){throw 'CITA TUN boundary tests failed.'}}
    finally{Pop-Location}
    $binary=Join-Path $stage 'cita-routing-core.exe'
    & $go.Source test -mod=readonly -tags 'with_gvisor,with_quic,with_wireguard,with_utls,with_clash_api,badlinkname,tfogo_checklinkname0' -ldflags '-checklinkname=0' -run '^TestCita(QUIC|FirstUse)' ./route
    if($LASTEXITCODE -ne 0){throw 'CITA QUIC domain identity/expiry/bounds tests failed.'}
    & $go.Source build -mod=readonly -buildvcs=false -trimpath -tags 'with_gvisor,with_quic,with_wireguard,with_utls,with_clash_api,badlinkname,tfogo_checklinkname0' -ldflags '-s -w -buildid= -X github.com/sagernet/sing-box/constant.Version=1.14.3-cita.5 -checklinkname=0' -o $binary ./cmd/sing-box
    if($LASTEXITCODE -ne 0){throw 'CITA routing core build failed.'}
    & $go.Source test -mod=readonly -c -o (Join-Path $project 'runtime\cita-dns-tests.exe') ./dns/transport
    if($LASTEXITCODE -ne 0){throw 'CITA DNS guest fixture compilation failed.'}
    Push-Location $tun
    try{& $go.Source test -mod=readonly -c -o (Join-Path $project 'runtime\cita-wfp-tests.exe') .;if($LASTEXITCODE -ne 0){throw 'WFP guest fixture compilation failed.'}}
    finally{Pop-Location}
    Copy-Item -LiteralPath $binary -Destination $target -Force
    [IO.File]::WriteAllText((Join-Path $core 'CITA-MODIFICATIONS.txt'),"CITA routing core 1.14.3-cita.5. No upstream affiliation. TUN modifications: narrowly scoped Quad100 DNS permits in the existing dynamic WFP session, restricted to active Tailscale adapter LUIDs; refresh every 2 seconds and stop before WFP cleanup. DNS: bounded sequential cita-fallback transport preserving valid negative responses and using separately configured child transports. QUIC: IPv4/443 domain metadata retained for 30 seconds, at most 1024 entries, keyed by process ID/path, inbound, source/destination tuple, version and Initial connection ID; current rules are evaluated again; no packet payload or routing decision retained. First-use: explicitly opted-in fallback TCP/443 flows may await an authenticated bounded local controller response before outbound selection; no payload replay; missing proof retains ordinary fallback. Windows sockets: reset readiness state before every scalar TCP, vector TCP and UDP RawConn.Read, including retry after a deadline; used by both router and VPN bridge. CLI branding changed. Corresponding pinned Go 1.26.3 build: go build -mod=readonly -trimpath -tags with_gvisor,with_quic,with_wireguard,with_utls,with_clash_api,badlinkname,tfogo_checklinkname0 -ldflags '-s -w -buildid= -X github.com/sagernet/sing-box/constant.Version=1.14.3-cita.5 -checklinkname=0' ./cmd/sing-box",[Text.UTF8Encoding]::new($false))
    # Provide the complete corresponding modified source, including go.mod/sum.
    $sourceZip=Join-Path $project 'distribution-licenses\cita-routing-core-1.14.3-cita.5-source.zip'
    Compress-Archive -Path $core,$tun,$sing -DestinationPath $sourceZip -Force
    $record=@{schema=1;version='1.14.3-cita.5';recipe=$recipe;sha256=(Get-FileHash -LiteralPath $target).Hash;go='1.26.3';sourceSha256=(Get-FileHash -LiteralPath $sourceZip).Hash}
    [IO.File]::WriteAllText($receipt,($record|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    Publish-Core
    Write-Output 'CITA routing core built from pinned source; Tailscale DNS exception remains scoped to Quad100 and Tailscale LUIDs.'
} finally {Pop-Location}
