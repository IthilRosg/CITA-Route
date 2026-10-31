$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$stage=Join-Path $root ('runtime/verify-zapret-stage-'+[guid]::NewGuid().ToString('N'))
$script=[IO.File]::ReadAllText((Join-Path $root 'Build-NativeComponents.ps1'))
$start=$script.IndexOf('if($IncludeZapret){');$end=$script.IndexOf("[IO.File]::WriteAllText((Join-Path "+'$stage'+" 'manifest.json')",$start)
if($start -lt 0 -or $end -le $start){throw 'Zapret build stage missing'}
$block=[ScriptBlock]::Create($script.Substring($start,$end-$start));$IncludeZapret=$true;$manifest=@{}
New-Item -ItemType Directory -Path $stage|Out-Null
try{
 & $block
 if(!$manifest.zapret -or $manifest.zapret.redistributionReady -ne $false -or $manifest.zapret.sha256 -cne (Get-FileHash -LiteralPath (Join-Path $stage 'zapret-manifest.json')).Hash){throw 'Zapret build stage receipt mismatch'}
 'PASS: pinned upstream bytes and Git blobs staged for internal-only bundle.'
}finally{
 $resolved=[IO.Path]::GetFullPath($stage);if([IO.Path]::GetDirectoryName($resolved) -ine (Join-Path $root 'runtime') -or [IO.Path]::GetFileName($resolved) -notmatch '^verify-zapret-stage-[a-f0-9]{32}$'){throw 'Unsafe staging verifier cleanup'}
 if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
