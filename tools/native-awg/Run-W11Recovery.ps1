param([switch]$RetryManualOnly)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runId=[guid]::NewGuid().ToString('N')
$report=@{schema=1;success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='Lab-only automatic encrypted UDP socket recovery, ordinary Windows scoped TUN TCP/UDP; no process restart or production agent integration';cases=@()}
$nativeHash=(Get-FileHash (Join-Path $root 'runtime\native-awg-lab.exe')).Hash;$coreHash=(Get-FileHash (Join-Path $root 'runtime\native-core-lab.exe')).Hash
$scenarios=@(@('automatic','-AutoRecovery'),@('manual-control',''))
if($RetryManualOnly){
 $previousFile=Get-ChildItem (Join-Path $root 'runtime') -Filter 'native-awg-recovery-*.json'|Sort-Object LastWriteTime -Descending|Select-Object -First 1
 $previous=Get-Content -LiteralPath $previousFile.FullName -Raw|ConvertFrom-Json
 $accepted=@($previous.cases|Where-Object {$_.name -eq 'automatic' -and $_.success})
 if($accepted.Count -ne 1 -or $previous.retryOf -or $previous.nativeSha256 -ne $nativeHash -or $previous.coreSha256 -ne $coreHash){throw 'One manual retry requires accepted automatic case and unchanged binaries'}
 $report.cases=$accepted;$report.retryOf=$previousFile.Name;$scenarios=@(,@('manual-control',''))
}
. (Join-Path $root 'runtime\vm-lab\W11-Calls.ps1')
try{
 W11-Call @('start',$labVmx,'nogui')|Out-Null
 foreach($pair in @(@('runtime/native-awg-lab.exe','native-awg-lab.exe'),@('runtime/native-core-lab.exe','native-core-lab.exe'),@('tools/native-awg/Run-GuestProxy.ps1','Run-GuestProxy.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
 foreach($case in $scenarios){
  $code='$p=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\CitaLab\NativeAwg\Run-GuestProxy.ps1 -Retention -CandidateCore -Migration Rebind -Tun OPTIONS" -RedirectStandardOutput C:\CitaLab\NativeAwg\recovery.out -RedirectStandardError C:\CitaLab\NativeAwg\recovery.err -PassThru;$null=$p.Handle;$p.WaitForExit()'
  W11-Code ($code.Replace('OPTIONS',$case[1]))|Out-Null
  $file=Join-Path $root ('runtime\vm-lab\w11\native-recovery-'+$runId+'-'+$case[0]+'.json')
  W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\proxy-migration.json',$file)|Out-Null
  $r=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json
  $match=[bool]($r.nativeSha256 -eq $nativeHash -and $r.coreSha256 -eq $coreHash)
  $clean=[bool]($r.processesStopped -and $r.topologyUnchanged -and $r.credentialFileRemoved)
  $item=@{name=$case[0];file=[IO.Path]::GetFileName($file);success=[bool]($r.success -and $match -and $clean -and $r.tunNoFallback -and $r.tunUdpNoFallback);hashesMatched=$match;clean=$clean;failedStage=$r.failedStage;recovery=$r.recoveryStatus;recoveryElapsedMilliseconds=$r.recoveryElapsedMilliseconds;requestMilliseconds=$r.retention.client.requestMilliseconds;tcpDials=$r.retention.client.dialsAfter;udpContinuity=$r.udpContinuity}
  if($case[0] -eq 'automatic' -and -not $r.automaticRecovery){$item.success=$false}
  $report.cases+=,$item;$item|ConvertTo-Json -Compress|Write-Output
 }
 $report.success=[bool]($report.cases.Count -eq 2 -and -not ($report.cases|Where-Object {-not $_.success}))
}catch{$report.errorType=$_.Exception.GetType().Name}
finally{$labCredential=$null;$labVmCredential=$null;$report.nativeSha256=$nativeHash;$report.coreSha256=$coreHash;$report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 7|Set-Content (Join-Path $root ('runtime\native-awg-recovery-'+$runId+'.json')) -Encoding UTF8}
if(-not $report.success){throw 'Recovery checkpoint incomplete; inspect preserved receipts'}
