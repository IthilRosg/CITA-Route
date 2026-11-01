[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [switch]$SkipAppSelfTest,
    [string]$CandidateReceipt='',
    [switch]$RunPrivilegedSelfTests
)

$ErrorActionPreference='Stop'
$package=[IO.Path]::GetFullPath($PackageDirectory)
if(-not (Test-Path -LiteralPath (Join-Path $package 'manifest.json'))){throw 'Fast-release package manifest is missing.'}

function Assert([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Run-SelfTest([string]$Path,[string]$Label){
    if(-not (Test-Path -LiteralPath $Path)){throw "$Label is missing."}
    $p=Start-Process -FilePath $Path -ArgumentList '--self-test' -WindowStyle Hidden -PassThru -Wait
    Assert ($p.ExitCode -eq 0) "$Label self-test failed with exit code $($p.ExitCode)."
}

$manifest=Get-Content -LiteralPath (Join-Path $package 'manifest.json') -Raw|ConvertFrom-Json
$product=Get-Content -LiteralPath (Join-Path $package 'product.json') -Raw|ConvertFrom-Json
Assert ($product.channel -in @('native-preview','standalone-preview')) 'Fast-release gate accepts native-preview or standalone-preview only.'
Assert ($manifest.edition -eq 'native') 'Fast-release package must be native.'
Assert ($manifest.version -eq $product.version) 'Manifest and product versions differ.'

$bad=@()
foreach($entry in $manifest.files){
    $path=[IO.Path]::GetFullPath((Join-Path $package ([string]$entry.path)))
    Assert ($path.StartsWith($package.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) "Manifest path escaped package: $($entry.path)"
    if(-not (Test-Path -LiteralPath $path)){ $bad += "missing:$($entry.path)"; continue }
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256){$bad += "hash:$($entry.path)"}
}
Assert ($bad.Count -eq 0) ('Manifest verification failed: '+($bad -join ', '))

$forbidden=@(Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object {
    $_.Extension -in @('.conf','.log') -or $_.Name -in @('session.json','connection.json','traffic.json','controller-token.txt','policy-cache.json')
})
Assert ($forbidden.Count -eq 0) 'Private runtime files were found in the package.'

$updater=Join-Path $package 'runtime\native-updater\Cita.NativeUpdater.exe'
$selfTestMode='RECORDED'
if($RunPrivilegedSelfTests){
    Run-SelfTest (Join-Path $package 'runtime\agent-build\RoutePilot.Agent.exe') 'Native agent'
    if(Test-Path -LiteralPath $updater){Run-SelfTest $updater 'Native updater'}
    if(-not $SkipAppSelfTest){Run-SelfTest (Join-Path $package 'RoutePilot.exe') 'GUI'}
    $selfTestMode='LIVE'
}else{
    Assert ($CandidateReceipt.Length -gt 0) 'Provide -CandidateReceipt or explicitly request -RunPrivilegedSelfTests.'
    $receipt=Get-Content -LiteralPath $CandidateReceipt -Raw|ConvertFrom-Json
    Assert ($receipt.version -eq $product.version) 'Candidate receipt version differs from package.'
    Assert ($receipt.managedSelfTest -eq 'PASS_CI' -and $receipt.appSelfTest -eq 'PASS' -and $receipt.updaterSelfTest -eq 'PASS') 'Recorded self-test receipt is incomplete.'
}

$nativeZip=Join-Path $package 'runtime\native-components.zip'
Assert (Test-Path -LiteralPath $nativeZip) 'Native components archive is missing.'
if($product.distribution.nativeComponentSha256){
    Assert ((Get-FileHash -LiteralPath $nativeZip -Algorithm SHA256).Hash -eq $product.distribution.nativeComponentSha256) 'Native components hash differs from product receipt.'
}

[pscustomobject]@{
    status='PASS'
    version=$product.version
    edition=$manifest.edition
    files=@($manifest.files).Count
    selfTestMode=$selfTestMode
    updater=if(Test-Path -LiteralPath $updater){'PASS'}else{'NOT_PRESENT'}
    app=if($SkipAppSelfTest){'SKIPPED'}else{'RECORDED'}
    nativeComponents='PASS'
}|ConvertTo-Json -Compress
