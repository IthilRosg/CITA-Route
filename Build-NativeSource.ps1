param([string]$Destination='runtime\native-corresponding-source.zip')
$ErrorActionPreference='Stop';$root=$PSScriptRoot
$receipt=Get-Content (Join-Path $root 'runtime/native-core-lab-build.json') -Raw|ConvertFrom-Json
if($receipt.binarySha256 -ne (Get-FileHash (Join-Path $root 'runtime/native-core-lab.exe')).Hash){throw 'Native binary receipt differs'}
foreach($patch in $receipt.patches){if((Get-FileHash (Join-Path $root ('tools/native-awg/core-patches/'+$patch.file))).Hash -ne $patch.sha256){throw 'Source patches changed after the accepted build'}}
$stage=Join-Path $root ('runtime/native-source-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $stage|Out-Null
foreach($name in @('sing-box-1.14.3','sing-cita','sing-tun-cita')){Copy-Item -LiteralPath (Join-Path $receipt.stage $name) -Destination (Join-Path $stage $name) -Recurse}
foreach($role in @('native-awg','native-guard-lab')){
 $target=Join-Path $stage $role;New-Item -ItemType Directory -Path $target|Out-Null
 Get-ChildItem -LiteralPath (Join-Path $root ('tools/'+$role)) -File|Where-Object {$_.Extension -eq '.go' -or $_.Name -in @('go.mod','go.sum')}|Copy-Item -Destination $target
}
$env:GOMODCACHE=Join-Path $root 'downloads/go-modcache';$env:GOCACHE=Join-Path $root 'downloads/go-buildcache';$env:GOTOOLCHAIN='local';$env:GOWORK='off';$env:GOOS='windows';$env:GOARCH='amd64';$env:CGO_ENABLED='0'
$proxy=Join-Path $stage 'module-proxy';New-Item -ItemType Directory -Path $proxy|Out-Null
$modules=@{}
foreach($image in @('native-core-lab.exe','native-awg-lab.exe','native-guard-lab.exe')){
 foreach($line in @(& go version -m (Join-Path $root ('runtime/'+$image)))){
  if($line -match '^\s*(?:dep|=>)\s+(\S+)\s+(v\S+)\s+'){$modules[$Matches[1]+'@'+$Matches[2]]=@($Matches[1],$Matches[2])}
 }
}
foreach($module in $modules.Values){
 $escaped=[regex]::Replace($module[0],'[A-Z]',{param($match)'!'+$match.Value.ToLowerInvariant()});$version=[regex]::Replace($module[1],'[A-Z]',{param($match)'!'+$match.Value.ToLowerInvariant()})
 $relative=$escaped+'/@v/'+$version;$target=Join-Path $proxy ([IO.Path]::GetDirectoryName($relative));New-Item -ItemType Directory -Path $target -Force|Out-Null
 foreach($extension in @('.zip','.mod','.info')){$file=Join-Path $env:GOMODCACHE ('cache/download/'+$relative+$extension);if(-not(Test-Path -LiteralPath $file)){throw 'Pinned dependency source is missing from the build cache'};Copy-Item -LiteralPath $file -Destination $target}
}
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $stage 'CITA-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $root 'distribution-licenses/GPL-3.0.txt') -Destination $stage
Copy-Item -LiteralPath (Join-Path $root 'tools/native-awg/core-patches') -Destination (Join-Path $stage 'patches') -Recurse
$recipe=@'
$ErrorActionPreference='Stop'
if((& go version) -notmatch 'go1\.26\.3 '){throw 'Go 1.26.3 required'}
$env:GOOS='windows';$env:GOARCH='amd64';$env:CGO_ENABLED='0';$env:GOWORK='off'
$env:GOPROXY=([uri](Join-Path $PSScriptRoot 'module-proxy')).AbsoluteUri;$env:GOSUMDB='off';$env:GOMODCACHE=Join-Path $PSScriptRoot 'rebuild-cache';$env:GOTOOLCHAIN='local'
Push-Location (Join-Path $PSScriptRoot 'sing-box-1.14.3')
try{go build -mod=readonly -buildvcs=false -trimpath -tags 'with_gvisor,with_quic,with_wireguard,with_utls,with_clash_api,badlinkname,tfogo_checklinkname0' -ldflags '-s -w -buildid= -X github.com/sagernet/sing-box/constant.Version=1.14.3-cita-native-lab.5 -checklinkname=0' -o ../native-core.exe ./cmd/sing-box;if($LASTEXITCODE){throw 'Core build failed'}}finally{Pop-Location}
foreach($role in @(@('native-awg','native-owner.exe'),@('native-guard-lab','native-guard.exe'))){Push-Location (Join-Path $PSScriptRoot $role[0]);try{go build -mod=readonly -buildvcs=false -trimpath -o (Join-Path '..' $role[1]) .;if($LASTEXITCODE){throw 'Component build failed'}}finally{Pop-Location}}
'@
[IO.File]::WriteAllText((Join-Path $stage 'BUILD.ps1'),$recipe,[Text.UTF8Encoding]::new($false))
$files=@(Get-ChildItem -LiteralPath $stage -File -Recurse|Sort-Object FullName|ForEach-Object {@{name=$_.FullName.Substring($stage.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$inventory=@{schema=1;coreBinarySha256=$receipt.binarySha256;files=$files};$inventory|ConvertTo-Json -Depth 5|Set-Content (Join-Path $stage 'SOURCE-MANIFEST.json') -Encoding UTF8
$output=[IO.Path]::GetFullPath((Join-Path $root $Destination));if(-not $output.StartsWith((Join-Path $root 'runtime\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Source artifact must stay in runtime'}
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $output -Force
@{schema=1;path=$output;stage=$stage;sha256=(Get-FileHash $output).Hash;bytes=(Get-Item $output).Length;coreBinarySha256=$receipt.binarySha256;rebuildVerified=$false;publicReleaseReady=$false}|ConvertTo-Json|Set-Content ($output+'.receipt.json') -Encoding UTF8
'Collected corresponding source candidate; reproducible rebuild verification is still required.'
