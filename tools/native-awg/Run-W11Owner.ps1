param([switch]$LeaseOnly)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$run=[guid]::NewGuid().ToString('N')
$report=@{schema=1;success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='Isolated process-owner candidate, scoped ordinary Windows TUN TCP/UDP; no installed agent change';cases=@()}
$nativeHash=(Get-FileHash (Join-Path $root 'runtime\native-awg-lab.exe')).Hash;$coreHash=(Get-FileHash (Join-Path $root 'runtime\native-core-lab.exe')).Hash
$scenarios=@(@('crash-and-budget',''),@('stop-during-restart','-OwnerStopRace'))
if($LeaseOnly){
 $previousFile=Get-ChildItem (Join-Path $root 'runtime') -Filter 'native-awg-owner-*.json'|Sort-Object LastWriteTime -Descending|Select-Object -First 1
 $previous=Get-Content -LiteralPath $previousFile.FullName -Raw|ConvertFrom-Json
 if(-not $previous.success -or $previous.cases.Count -ne 2 -or $previous.nativeSha256 -ne $nativeHash -or $previous.coreSha256 -ne $coreHash){throw 'Lease continuation requires two accepted cases on unchanged binaries'}
 $report.cases=@($previous.cases);$report.previousSeries=$previousFile.Name;$scenarios=@()
}
. (Join-Path $root 'runtime\vm-lab\W11-Calls.ps1')
try{
 W11-Call @('start',$labVmx,'nogui')|Out-Null
 foreach($pair in @(@('runtime/native-awg-lab.exe','native-awg-lab.exe'),@('runtime/native-core-lab.exe','native-core-lab.exe'),@('tools/native-awg/Run-GuestProxy.ps1','Run-GuestProxy.ps1'),@('tools/native-awg/Run-GuestOwnerLease.ps1','Run-GuestOwnerLease.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
 foreach($case in $scenarios){
  $code='$p=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\CitaLab\NativeAwg\Run-GuestProxy.ps1 -Retention -CandidateCore -Migration Rebind -Tun -Supervised OPTIONS" -RedirectStandardOutput C:\CitaLab\NativeAwg\owner.out -RedirectStandardError C:\CitaLab\NativeAwg\owner.err -PassThru;$null=$p.Handle;$p.WaitForExit()'
  W11-Code ($code.Replace('OPTIONS',$case[1]))|Out-Null
  $file=Join-Path $root ('runtime\vm-lab\w11\native-owner-'+$run+'-'+$case[0]+'.json');W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\proxy-migration.json',$file)|Out-Null
  $r=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json
  $match=[bool]($r.nativeSha256 -eq $nativeHash -and $r.coreSha256 -eq $coreHash);$clean=[bool]($r.topologyUnchanged -and $r.processesStopped -and $r.credentialFileRemoved)
  $item=@{name=$case[0];file=[IO.Path]::GetFileName($file);success=[bool]($r.success -and $r.supervisionPassed -and $match -and $clean -and $r.tunNoFallback -and $r.tunUdpNoFallback);hashesMatched=$match;clean=$clean;failedStage=$r.failedStage;restartMilliseconds=$r.childRestartMilliseconds;oldStreamClosed=$r.oldStreamClosed;freshTraffic=$r.freshAfterCrash.success;budgetEnforced=$r.restartBudgetEnforced;stopDuringBackoff=$r.stopDuringBackoff}
  $report.cases+=,$item;$item|ConvertTo-Json -Compress|Write-Output
 }
 W11-Code '$p=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\CitaLab\NativeAwg\Run-GuestOwnerLease.ps1" -RedirectStandardOutput C:\CitaLab\NativeAwg\lease.out -RedirectStandardError C:\CitaLab\NativeAwg\lease.err -PassThru;$null=$p.Handle;$p.WaitForExit()'|Out-Null
 $file=Join-Path $root ('runtime\vm-lab\w11\native-owner-'+$run+'-lease.json');W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\owner-lease.json',$file)|Out-Null
 $lease=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json
 $item=@{name='owner-lease-and-fencing';file=[IO.Path]::GetFileName($file);success=[bool]($lease.success -and $lease.processesStopped -and $lease.nativeSha256 -eq $nativeHash);orphanClosed=$lease.orphanLeaseClosed;sessionFenced=$lease.sessionFenced;generationFenced=$lease.generationFenced}
 $report.cases+=,$item;$item|ConvertTo-Json -Compress|Write-Output
 $report.success=[bool]($report.cases.Count -eq 3 -and -not($report.cases|Where-Object {-not $_.success}))
}catch{$report.errorType=$_.Exception.GetType().Name}
finally{$labCredential=$null;$labVmCredential=$null;$report.nativeSha256=$nativeHash;$report.coreSha256=$coreHash;$report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 7|Set-Content (Join-Path $root ('runtime\native-awg-owner-'+$run+'.json')) -Encoding UTF8}
if(-not $report.success){throw 'Owner checkpoint incomplete; inspect preserved receipts'}
