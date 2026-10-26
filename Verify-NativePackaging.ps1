$ErrorActionPreference='Stop';$root=$PSScriptRoot
$original=Get-Content (Join-Path $root 'runtime/native-corresponding-source.zip.receipt.json') -Raw
$path=Join-Path $root ('runtime/native-package-negative-'+[guid]::NewGuid().ToString('N')+'.json');$passed=@()
try{
 foreach($test in @('UnverifiedSource','native-core.exe','native-owner.exe','native-guard.exe')){
  $receipt=$original|ConvertFrom-Json
  if($test -eq 'UnverifiedSource'){$receipt.rebuildVerified=$false;$expected='All native source rebuilds must be verified before packaging'}else{$receipt.verifiedBinaries.$test=('0'*64);$expected='A native role differs from its verified corresponding source'}
  $receipt|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $path -Encoding UTF8
  $rejected=$false;try{& (Join-Path $root 'Build-NativeComponents.ps1') -SourceReceipt $path -Destination 'runtime/native-rejected.zip'|Out-Null}catch{if($_.Exception.Message -eq $expected){$rejected=$true}else{throw}}
  if(-not $rejected){throw ('Unsafe component set accepted: '+$test)};$passed+=$test
 }
 @{success=$true;rejected=$passed}|ConvertTo-Json -Depth 3|Set-Content (Join-Path $root 'runtime/native-packaging-negative.json') -Encoding UTF8
 'PASS: unverified source and mismatched core/owner/guard receipts are refused.'
}finally{if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Force}}
