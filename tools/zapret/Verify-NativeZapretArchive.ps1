$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$work=Join-Path $root ('runtime/verify-zapret-'+[guid]::NewGuid().ToString('N'))
$upstream=Join-Path $root 'runtime/zapret-upstream-4840909b1a26'
$script=[IO.File]::ReadAllText((Join-Path $root 'Install-NativeComponents.ps1'))
$start=$script.IndexOf(" "+'$noticeNames=@(');$endMarker="New-Item -ItemType Directory -Path "+'$stage -ErrorAction Stop|Out-Null}';$end=$script.IndexOf($endMarker,$start);if($end -ge 0){$end+=$endMarker.Length}
$optionalStart=$script.IndexOf(" if("+'$manifest.zapret){',$end);$optionalEnd=$script.IndexOf(" # Pick the actual",$optionalStart)
if($start -lt 0 -or $end -le $start -or $optionalStart -le $end -or $optionalEnd -le $optionalStart){throw 'Install verification regions missing'}
$validate=[ScriptBlock]::Create($script.Substring($start,$end-$start)+$script.Substring($optionalStart,$optionalEnd-$optionalStart))
$receipt=Get-Content -LiteralPath (Join-Path $upstream 'upstream-receipt.json') -Raw|ConvertFrom-Json
$required=@('manifest.json','native-owner.exe','native-core.exe','native-guard.exe','GPL-3.0.txt','sing-box-LICENSE.txt','amneziawg-LICENSE.txt','wf-LICENSE.txt')
New-Item -ItemType Directory -Path $work|Out-Null
try{
 $passed=@()
 foreach($case in @('valid','legacy','traversal','duplicate','symlink','tamper','bad-schema','duplicate-json','missing','bad-blob','bad-manifest-hash')){
  $fixture=Join-Path $work $case;New-Item -ItemType Directory -Path $fixture|Out-Null
  foreach($name in $required|Where-Object {$_ -ne 'manifest.json'}){[IO.File]::WriteAllText((Join-Path $fixture $name),'fixture')}
  $outer=@{schema=1;architecture='win-x64';setupProtocol=1;policyProtocol=1;directScopeProtocol=1;files=@(1,2,3)}
  if($case -ne 'legacy'){
   $files=@();foreach($item in $receipt.files){$name=[string]$item.name;$dest=Join-Path (Join-Path $fixture 'zapret') $name;New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($dest)) -Force|Out-Null;Copy-Item -LiteralPath (Join-Path $upstream $name) -Destination $dest;$files+=@{Name=$name;Sha256=$item.sha256;Bytes=$item.bytes;GitBlob=$item.gitBlob}}
   if($case -eq 'bad-blob'){$files[0].GitBlob='0'*40}
   $doc=@{Schema=1;Repository=$receipt.repository;Commit=$receipt.commit;Tree=$receipt.tree;Files=$files;RedistributionReady=$false}
   if($case -eq 'bad-schema'){$doc.Schema=2}
   $json=$doc|ConvertTo-Json -Depth 5
   if($case -eq 'duplicate-json'){$json=$json.Insert($json.IndexOf('{')+1,'"Schema":1,')}
   $zp=Join-Path $fixture 'zapret-manifest.json';[IO.File]::WriteAllText($zp,$json,[Text.UTF8Encoding]::new($false))
   $outer.zapret=@{name='zapret-manifest.json';sha256=(Get-FileHash $zp).Hash;bytes=(Get-Item $zp).Length;redistributionReady=$false}
   if($case -eq 'bad-manifest-hash'){$outer.zapret.sha256='0'*64}
   if($case -eq 'tamper'){[IO.File]::WriteAllText((Join-Path $fixture 'zapret/winws2.exe'),'tampered')}
   if($case -eq 'missing'){Remove-Item -LiteralPath (Join-Path $fixture 'zapret/cygwin1.dll')}
  }
  [IO.File]::WriteAllText((Join-Path $fixture 'manifest.json'),($outer|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
  $archive=Join-Path $work ($case+'.zip');Compress-Archive -Path (Join-Path $fixture '*') -DestinationPath $archive
  if($case -in @('traversal','duplicate','symlink')){Add-Type -AssemblyName System.IO.Compression.FileSystem;$modified=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Update);try{if($case -eq 'symlink'){$modified.GetEntry('zapret/winws2.exe').ExternalAttributes=-1610612736}else{$name=if($case -eq 'traversal'){'../escape'}else{'zapret/winws2.exe'};[void]$modified.CreateEntry($name)}}finally{$modified.Dispose()}}
  $stage=Join-Path $work ($case+'-stage')
  Add-Type -AssemblyName System.IO.Compression.FileSystem;$zip=[IO.Compression.ZipFile]::OpenRead($archive);$ok=$false
  try{& $validate;$ok=$true}catch{if($case -in @('valid','legacy')){throw}}finally{$zip.Dispose()}
  if($ok -ne ($case -in @('valid','legacy'))){throw ('Unexpected archive decision: '+$case)};$passed+=$case
 }
 'PASS: internal Zapret archive validation: '+($passed -join ', ')
}finally{
 $resolved=[IO.Path]::GetFullPath($work);if([IO.Path]::GetDirectoryName($resolved) -ine (Join-Path $root 'runtime') -or [IO.Path]::GetFileName($resolved) -notmatch '^verify-zapret-[a-f0-9]{32}$'){throw 'Unsafe verifier cleanup'}
 if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
