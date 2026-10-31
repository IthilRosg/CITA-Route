param([string]$Commit='4840909b1a26389cb72c46be5301d610b3768928')
$ErrorActionPreference='Stop'
if($Commit -cnotmatch '^[a-f0-9]{40}$'){throw 'An exact upstream commit is required'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$destination=Join-Path $root ('runtime/zapret-upstream-'+$Commit.Substring(0,12))
$headers=@{'User-Agent'='CITA-Route-development';Accept='application/vnd.github+json'}
$commitInfo=Invoke-RestMethod -Headers $headers -Uri ('https://api.github.com/repos/bol-van/zapret-win-bundle/commits/'+$Commit)
if($commitInfo.sha -cne $Commit){throw 'Upstream commit mismatch'}
$tree=Invoke-RestMethod -Headers $headers -Uri ('https://api.github.com/repos/bol-van/zapret-win-bundle/git/trees/'+$commitInfo.commit.tree.sha+'?recursive=1')
if($tree.truncated -or $tree.sha -cne $commitInfo.commit.tree.sha){throw 'Incomplete upstream inventory'}
$names=@('winws2.exe','cygwin1.dll','WinDivert.dll','WinDivert64.sys','lua/zapret-lib.lua','lua/zapret-antidpi.lua')
New-Item -ItemType Directory -Path $destination -Force|Out-Null
$files=@()
foreach($name in $names){
 $entry=@($tree.tree|Where-Object {$_.path -ceq ('zapret-winws/'+$name)})
 if($entry.Count -ne 1 -or $entry[0].type -ne 'blob' -or $entry[0].size -lt 1 -or $entry[0].size -gt 8388608){throw 'Invalid component inventory'}
 $path=Join-Path $destination $name
 New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($path)) -Force|Out-Null
 Invoke-WebRequest -Headers $headers -Uri ('https://raw.githubusercontent.com/bol-van/zapret-win-bundle/'+$Commit+'/zapret-winws/'+$name) -OutFile $path
 $bytes=[IO.File]::ReadAllBytes($path)
 $prefix=[Text.Encoding]::ASCII.GetBytes('blob '+$bytes.Length+[char]0)
 $blob=New-Object byte[] ($prefix.Length+$bytes.Length)
 [Array]::Copy($prefix,0,$blob,0,$prefix.Length);[Array]::Copy($bytes,0,$blob,$prefix.Length,$bytes.Length)
 if($bytes.Length -ne $entry[0].size -or [Convert]::ToHexString([Security.Cryptography.SHA1]::HashData($blob)).ToLowerInvariant() -cne $entry[0].sha){Remove-Item -LiteralPath $path -Force;throw 'Upstream blob verification failed'}
 $files+=@{name=$name;bytes=$bytes.Length;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash;gitBlob=$entry[0].sha}
}
$signature=Get-AuthenticodeSignature -LiteralPath (Join-Path $destination 'WinDivert64.sys')
if($signature.Status -ne 'Valid'){throw 'The WinDivert driver must have a valid Windows signature; do not change OS signing/security policy'}
$receipt=@{schema=1;repository='bol-van/zapret-win-bundle';commit=$Commit;tree=$tree.sha;files=$files;driverSignature=@{status=$signature.Status.ToString();subject=$signature.SignerCertificate.Subject;thumbprint=$signature.SignerCertificate.Thumbprint};redistributionReady=$false;runtimeValidated=$false}
$receipt|ConvertTo-Json -Depth 5|Set-Content (Join-Path $destination 'upstream-receipt.json') -Encoding utf8
Write-Output ('ZAPRET-STAGE: '+$destination)
