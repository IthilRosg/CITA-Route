param([string]$Destination='runtime\native-components.zip')
$ErrorActionPreference='Stop';$root=$PSScriptRoot
$core=Get-Content (Join-Path $root 'runtime\native-core-lab-build.json') -Raw|ConvertFrom-Json
if($core.binarySha256 -ne (Get-FileHash (Join-Path $root 'runtime\native-core-lab.exe')).Hash){throw 'Native core receipt mismatch'}
$stage=Join-Path $root ('runtime\native-bundle-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $stage|Out-Null
$files=@();foreach($pair in @(@('runtime\native-awg-lab.exe','native-owner.exe'),@('runtime\native-core-lab.exe','native-core.exe'),@('runtime\native-guard-lab.exe','native-guard.exe'))){$target=Join-Path $stage $pair[1];Copy-Item -LiteralPath (Join-Path $root $pair[0]) -Destination $target;$files+=@{name=$pair[1];sha256=(Get-FileHash $target).Hash;bytes=(Get-Item $target).Length}}
$noticeFiles=@(@('distribution-licenses\GPL-3.0.txt','GPL-3.0.txt'),@('distribution-licenses\sing-box-LICENSE.txt','sing-box-LICENSE.txt'),@('downloads\go-modcache\github.com\amnezia-vpn\amneziawg-go\v3@v3.1.20260828\LICENSE','amneziawg-LICENSE.txt'),@('downloads\go-modcache\github.com\tailscale\wf@v0.0.0-20240214030419-6fbb0a674ee6\LICENSE','wf-LICENSE.txt'))
$notices=@();foreach($pair in $noticeFiles){$path=Join-Path $stage $pair[1];Copy-Item -LiteralPath (Join-Path $root $pair[0]) -Destination $path;$notices+=@{name=$pair[1];sha256=(Get-FileHash $path).Hash;bytes=(Get-Item $path).Length}}
$source=Get-Content (Join-Path $root 'runtime/native-corresponding-source.zip.receipt.json') -Raw|ConvertFrom-Json;if($source.coreBinarySha256 -ne $core.binarySha256 -or (Get-FileHash $source.path).Hash -ne $source.sha256){throw 'Native source package does not match core'}
$manifest=@{schema=1;architecture='win-x64';setupProtocol=1;policyProtocol=1;guardProtocol=1;files=$files;notices=$notices;sourceSha256=$source.sha256;experimental=$true;corePolicyOnlyTests=[bool]$core.policyOnlyTests}
[IO.File]::WriteAllText((Join-Path $stage 'manifest.json'),($manifest|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
$output=[IO.Path]::GetFullPath((Join-Path $root $Destination));$allowed=[IO.Path]::GetFullPath((Join-Path $root 'runtime'))+'\';if(-not $output.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Native bundle must stay inside runtime'}
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $output -Force
@{schema=1;path=$output;sha256=(Get-FileHash $output).Hash;bytes=(Get-Item $output).Length;experimental=$true;publicReleaseReady=$false}|ConvertTo-Json|Set-Content ($output+'.receipt.json') -Encoding UTF8
'Prepared experimental native component bundle; public packaging remains gated.'
