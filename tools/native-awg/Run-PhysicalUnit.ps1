$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'runtime/invoke-rm-lab.ps1')
. (Join-Path $workspace 'NativeSourceProvenance.ps1')
$source=Get-Content (Join-Path $workspace 'runtime/native-corresponding-source.zip.receipt.json') -Raw|ConvertFrom-Json
if(!$source.rebuildVerified -or !$source.workspaceInputs){throw 'Verified source required'}
Assert-NativeInventory $source.workspaceInputs @(Get-NativeWorkspaceInventory $workspace)
if((Get-FileHash (Join-Path $workspace 'runtime/native-awg-lab.exe')).Hash -ne $source.verifiedBinaries.'native-owner.exe'){throw 'Owner binary differs from verified source'}
$id=[guid]::NewGuid().ToString('N');$image=Join-Path $workspace 'runtime/cita-owner-physical.test.exe';$hash=(Get-FileHash $image).Hash
$buildReceipt=Get-Content (Join-Path $workspace 'runtime/cita-owner-physical.test.receipt.json') -Raw|ConvertFrom-Json
if($buildReceipt.testSha256 -ne $hash -or $buildReceipt.ownerSha256 -ne $source.verifiedBinaries.'native-owner.exe' -or $buildReceipt.sourceReceiptSha256 -ne (Get-FileHash (Join-Path $workspace 'runtime/native-corresponding-source.zip.receipt.json')).Hash){throw 'Owner test binary provenance mismatch'}
& scp -q -i "$env:USERPROFILE\.ssh\cita_test_station_ed25519" -o "UserKnownHostsFile=$env:USERPROFILE\.ssh\cita_test_station_known_hosts" -o StrictHostKeyChecking=yes -o ConnectTimeout=10 -o ServerAliveInterval=5 -o ServerAliveCountMax=3 $image "citastation@26.68.227.253:C:/CITA-Lab/owner-test-$id.exe"
if($LASTEXITCODE){throw 'Owner test transfer failed; inspect temporary file'}
$code=@'
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong physical station'}
$image='C:\CITA-Lab\owner-test-__ID__.exe';$script='C:\CITA-Lab\owner-test-__ID__.ps1';$process=$null;$success=$false;$clean=$false;$failure='';$exit=-1;$testCount=0
try{
 if((Get-FileHash -LiteralPath $image).Hash -ne '__HASH__'){throw 'Owner test image differs'}
 $lock=[IO.File]::Open($image,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 try{
  if((Get-FileHash -InputStream $lock).Hash -ne '__HASH__'){throw 'Owner test image changed'}
  $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$image;$info.Arguments='-test.v -test.timeout=30s -test.run=^TestOwner(RestartBudgetAndStopFence|EpochIsSeparateFromProfileGeneration|ProtocolDegradationDoesNotRepairHealthySocket)$';$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
  $process=[Diagnostics.Process]::Start($info);$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
  if(!$process.WaitForExit(45000)){throw 'Owner unit deadline'};$exit=$process.ExitCode;$output=$stdout.Result;$null=$stderr.Result
  $testCount=([regex]::Matches($output,'(?m)^--- PASS: TestOwner')).Count
  if($exit -ne 0 -or $output -notmatch '(?m)^PASS\s*$' -or $testCount -ne 3){throw 'Owner unit tests failed or incomplete'};$success=$true
 }finally{$lock.Dispose()}
}catch{$failure=$_.Exception.Message}
finally{
 if($process){try{if(!$process.HasExited){$process.Kill();if(!$process.WaitForExit(5000)){throw 'Owner test process exit unconfirmed'}}}catch{$failure+='; '+$_.Exception.Message}finally{$process.Dispose()}}
 try{
  if(@(Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -ieq $image}).Count){throw 'Owner test process still active'}
  foreach($path in @($image,$script)){$item=Get-Item -LiteralPath $path -Force;if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unexpected test artifact'};Remove-Item -LiteralPath $path -Force}
  $clean=$true
 }catch{$failure+='; cleanup: '+$_.Exception.Message}
}
$result=@{run='__ID__';imageHash='__HASH__';success=$success;cleanup=$clean;exit=$exit;tests=$testCount;failure=$failure;scope='three owner state-machine tests; no sockets, firewall or routes changed'}|ConvertTo-Json -Compress
$receipt='C:\CITA-Lab\owner-test-__ID__.json';[IO.File]::WriteAllText($receipt+'.new',$result,[Text.UTF8Encoding]::new($false));[IO.File]::Move($receipt+'.new',$receipt);$result
'@
$script=Join-Path $workspace "runtime/owner-test-$id.ps1";[IO.File]::WriteAllText($script,$code.Replace('__ID__',$id).Replace('__HASH__',$hash),[Text.UTF8Encoding]::new($true))
& scp -q -i "$env:USERPROFILE\.ssh\cita_test_station_ed25519" -o "UserKnownHostsFile=$env:USERPROFILE\.ssh\cita_test_station_known_hosts" -o StrictHostKeyChecking=yes -o ConnectTimeout=10 -o ServerAliveInterval=5 -o ServerAliveCountMax=3 $script "citastation@26.68.227.253:C:/CITA-Lab/owner-test-$id.ps1"
if($LASTEXITCODE){throw 'Owner test script transfer failed; inspect temporary files'}
$result=((Invoke-Rm "& ([ScriptBlock]::Create([IO.File]::ReadAllText('C:\CITA-Lab\owner-test-$id.ps1'))) ") -join "`n")|ConvertFrom-Json
Remove-Item -LiteralPath $script -Force
if($result.run -cne $id -or $result.imageHash -cne $hash){throw 'Owner test receipt identity mismatch'}
$result|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $workspace 'runtime/goal2-owner-physical-result.json') -Encoding utf8
if(!$result.success -or !$result.cleanup){throw 'Owner test or cleanup unconfirmed; receipts retained'}
Assert-NativeInventory $source.workspaceInputs @(Get-NativeWorkspaceInventory $workspace)
if((Get-FileHash $image).Hash -ne $hash -or (Get-FileHash (Join-Path $workspace 'runtime/native-corresponding-source.zip.receipt.json')).Hash -ne $buildReceipt.sourceReceiptSha256){throw 'Owner test provenance changed during run'}
$remove="`$p='C:\CITA-Lab\owner-test-$id.json';`$f=Get-Item -LiteralPath `$p -Force;if(`$f.PSIsContainer -or (`$f.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unexpected receipt'};Remove-Item -LiteralPath `$p -Force"
Invoke-Rm $remove|Out-Null
$result
