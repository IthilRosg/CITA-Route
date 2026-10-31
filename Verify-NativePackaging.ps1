param([string]$SourceReceipt='runtime/native-corresponding-source-current.zip.receipt.json')
$ErrorActionPreference='Stop';$root=$PSScriptRoot
$original=Get-Content (Join-Path $root $SourceReceipt) -Raw
$path=Join-Path $root ('runtime/native-package-negative-'+[guid]::NewGuid().ToString('N')+'.json');$passed=@()
try{
 foreach($test in @('UnverifiedSource','native-core.exe','native-owner.exe','native-guard.exe','StaleWorkspace','MissingWorkspaceInput','AddedWorkspaceInput')){
  $receipt=$original|ConvertFrom-Json
  if($test -eq 'UnverifiedSource'){$receipt.rebuildVerified=$false;$expected='All native source rebuilds must be verified before packaging'}
  elseif($test -eq 'StaleWorkspace'){$receipt.workspaceInputs[0].sha256=('0'*64);$expected='Native source workspace differs from verified inventory'}
  elseif($test -eq 'MissingWorkspaceInput'){$receipt.workspaceInputs=@($receipt.workspaceInputs|Select-Object -Skip 1);$expected='Native source workspace differs from verified inventory'}
  elseif($test -eq 'AddedWorkspaceInput'){$receipt.workspaceInputs=@($receipt.workspaceInputs)+@([pscustomobject]@{path='tools/nonexistent-input.go';sha256=('A'*64)});$expected='Native source workspace differs from verified inventory'}
  else{$receipt.verifiedBinaries.$test=('0'*64);$expected='A native role differs from its verified corresponding source'}
  $receipt|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $path -Encoding UTF8
  $rejected=$false;try{& (Join-Path $root 'Build-NativeComponents.ps1') -SourceReceipt $path -Destination 'runtime/native-rejected.zip'|Out-Null}catch{if($_.Exception.Message -eq $expected){$rejected=$true}else{throw}}
  if(-not $rejected){throw ('Unsafe component set accepted: '+$test)};$passed+=$test
 }
 @{success=$true;rejected=$passed}|ConvertTo-Json -Depth 3|Set-Content (Join-Path $root 'runtime/native-packaging-negative.json') -Encoding UTF8
 'PASS: unverified source, mismatched roles and changed/added/missing workspace inputs are refused.'
 & (Join-Path $root 'tools/zapret/Verify-ZapretStage.ps1')
 & (Join-Path $root 'tools/zapret/Verify-NativeZapretArchive.ps1')
}finally{if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Force}}
