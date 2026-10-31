param([string]$Destination='runtime\native-components.zip',[string]$SourceReceipt='runtime\native-corresponding-source-current.zip.receipt.json',[switch]$IncludeZapret)
$ErrorActionPreference='Stop';$root=$PSScriptRoot
. (Join-Path $root 'NativeSourceProvenance.ps1')
$core=Get-Content (Join-Path $root 'runtime\native-core-lab-build.json') -Raw|ConvertFrom-Json
if($core.binarySha256 -ne (Get-FileHash (Join-Path $root 'runtime\native-core-lab.exe')).Hash){throw 'Native core receipt mismatch'}
$stage=Join-Path $root ('runtime\native-bundle-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $stage|Out-Null
try{
$files=@();foreach($pair in @(@('runtime\native-awg-lab.exe','native-owner.exe'),@('runtime\native-core-lab.exe','native-core.exe'),@('runtime\native-guard-lab.exe','native-guard.exe'))){$target=Join-Path $stage $pair[1];Copy-Item -LiteralPath (Join-Path $root $pair[0]) -Destination $target;$files+=@{name=$pair[1];sha256=(Get-FileHash $target).Hash;bytes=(Get-Item $target).Length}}
$noticeFiles=@(@('distribution-licenses\GPL-3.0.txt','GPL-3.0.txt'),@('distribution-licenses\sing-box-LICENSE.txt','sing-box-LICENSE.txt'),@('downloads\go-modcache\github.com\amnezia-vpn\amneziawg-go\v3@v3.1.20260828\LICENSE','amneziawg-LICENSE.txt'),@('downloads\go-modcache\github.com\tailscale\wf@v0.0.0-20240214030419-6fbb0a674ee6\LICENSE','wf-LICENSE.txt'))
$notices=@();foreach($pair in $noticeFiles){$path=Join-Path $stage $pair[1];Copy-Item -LiteralPath (Join-Path $root $pair[0]) -Destination $path;$notices+=@{name=$pair[1];sha256=(Get-FileHash $path).Hash;bytes=(Get-Item $path).Length}}
$receiptPath=[IO.Path]::GetFullPath($(if([IO.Path]::IsPathRooted($SourceReceipt)){$SourceReceipt}else{Join-Path $root $SourceReceipt}));if(-not $receiptPath.StartsWith((Join-Path $root 'runtime')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Source receipt must stay inside runtime'}
$source=Get-Content $receiptPath -Raw|ConvertFrom-Json;if($source.coreBinarySha256 -ne $core.binarySha256 -or (Get-FileHash $source.path).Hash -ne $source.sha256){throw 'Native source package does not match core'}
if($source.rebuildVerified -ne $true){throw 'All native source rebuilds must be verified before packaging'}
if(!$source.workspaceInputs){throw 'Verified native workspace inventory missing'}
Assert-NativeInventory $source.workspaceInputs @(Get-NativeWorkspaceInventory $root)
foreach($file in $files){if($source.verifiedBinaries.($file.name) -ne $file.sha256){throw 'A native role differs from its verified corresponding source'}}
$manifest=@{schema=1;architecture='win-x64';setupProtocol=1;policyProtocol=1;directScopeProtocol=1;guardProtocol=1;files=$files;notices=$notices;sourceSha256=$source.sha256;experimental=$true;corePolicyOnlyTests=[bool]$core.policyOnlyTests}
if($IncludeZapret){
 $upstream=Join-Path $root 'runtime/zapret-upstream-4840909b1a26';$receipt=Get-Content -LiteralPath (Join-Path $upstream 'upstream-receipt.json') -Raw|ConvertFrom-Json
 if($receipt.schema -ne 1 -or $receipt.repository -cne 'bol-van/zapret-win-bundle' -or $receipt.commit -cne '4840909b1a26389cb72c46be5301d610b3768928' -or $receipt.tree -cne '1c4bcc0bd1294e8b06b59c775c4783fbdce22b89' -or $receipt.redistributionReady -ne $false -or @($receipt.files).Count -ne 6){throw 'Zapret pinned upstream receipt refused'}
 $pins=@{'winws2.exe'='BA4CECCDEA3576DA0A0991A50D7893EA911DE197281338132DBEC02A5B2FCF58';'cygwin1.dll'='103104A52E5293CE418944725DF19E2BF81AD9269B9A120D71D39028E821499B';'WinDivert.dll'='16ABD6A029E65557C6A309BEA7B13BF81FFF4E193567582E1CDDBF6719F323E0';'WinDivert64.sys'='8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2';'lua/zapret-lib.lua'='6BF528FEF1F0FC3C65E189216FC8A06BB873F6E25D1C29D2BFA4695A09B73440';'lua/zapret-antidpi.lua'='6D1FA9168E5DE1682FEF91FD174F68B58C50F6739EF00F42CA7DB2B940B6246D'}
 $seen=@{};$zapretFiles=@();foreach($item in $receipt.files){$name=[string]$item.name;if(!$pins.ContainsKey($name) -or $seen.ContainsKey($name) -or $item.sha256 -cne $pins[$name] -or $item.gitBlob -cnotmatch '^[a-f0-9]{40}$'){throw 'Zapret pinned component identity refused'};$seen[$name]=$true
  $path=Join-Path $upstream $name;$sourceItem=Get-Item -LiteralPath $path -Force;if($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Zapret linked component refused'};for($part=$sourceItem.Directory;$part;$part=$part.Parent){if($part.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Zapret linked component refused'}}
  if((Get-Item -LiteralPath $path).Length -ne $item.bytes -or $item.bytes -lt 1 -or $item.bytes -gt 8388608 -or (Get-FileHash -LiteralPath $path).Hash -cne $pins[$name]){throw 'Zapret pinned component bytes differ'}
  $content=[IO.File]::ReadAllBytes($path);$prefix=[Text.Encoding]::ASCII.GetBytes('blob '+$content.Length+[char]0);$sha=[Security.Cryptography.SHA1]::Create();try{[void]$sha.TransformBlock($prefix,0,$prefix.Length,$prefix,0);[void]$sha.TransformFinalBlock($content,0,$content.Length);$blob=([BitConverter]::ToString($sha.Hash)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()};if($blob -cne $item.gitBlob){throw 'Zapret Git blob differs'}
  $target=Join-Path (Join-Path $stage 'zapret') $name;New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force|Out-Null;Copy-Item -LiteralPath $path -Destination $target
  if((Get-FileHash -LiteralPath $target).Hash -cne $pins[$name]){throw 'Zapret stage copy differs'};$zapretFiles+=@{Name=$name;Sha256=$pins[$name];Bytes=[long]$item.bytes;GitBlob=[string]$item.gitBlob}
 }
 $zapretDocument=@{Schema=1;Repository=$receipt.repository;Commit=$receipt.commit;Tree=$receipt.tree;Files=$zapretFiles;RedistributionReady=$false};$zp=Join-Path $stage 'zapret-manifest.json'
 [IO.File]::WriteAllText($zp,($zapretDocument|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false));$manifest.zapret=@{name='zapret-manifest.json';sha256=(Get-FileHash -LiteralPath $zp).Hash;bytes=(Get-Item -LiteralPath $zp).Length;redistributionReady=$false}
}
[IO.File]::WriteAllText((Join-Path $stage 'manifest.json'),($manifest|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
$output=[IO.Path]::GetFullPath((Join-Path $root $Destination));$allowed=[IO.Path]::GetFullPath((Join-Path $root 'runtime'))+'\';if(-not $output.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Native bundle must stay inside runtime'}
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $output -Force
@{schema=1;path=$output;sha256=(Get-FileHash $output).Hash;bytes=(Get-Item $output).Length;experimental=$true;publicReleaseReady=$false}|ConvertTo-Json|Set-Content ($output+'.receipt.json') -Encoding UTF8
'Prepared experimental native component bundle; public packaging remains gated.'
}finally{
 $resolved=[IO.Path]::GetFullPath($stage)
 if([IO.Path]::GetDirectoryName($resolved) -ine (Join-Path $root 'runtime') -or [IO.Path]::GetFileName($resolved) -notmatch '^native-bundle-[a-f0-9]{32}$'){throw 'Unsafe native staging cleanup path'}
 if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
