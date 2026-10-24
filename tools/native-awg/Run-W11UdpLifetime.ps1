$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$run=[guid]::NewGuid().ToString('N')
$report=@{schema=1;success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='Targeted native UDP control-lifetime invalidation; same OS UDP socket after AWG child crash, retained direct TCP, scoped W11 TUN';cases=@()}
$nativeHash=(Get-FileHash (Join-Path $root 'runtime\native-awg-lab.exe')).Hash;$coreHash=(Get-FileHash (Join-Path $root 'runtime\native-core-lab.exe')).Hash
. (Join-Path $root 'runtime\vm-lab\W11-Calls.ps1')
try{
 W11-Call @('start',$labVmx,'nogui')|Out-Null
 foreach($pair in @(@('runtime/native-awg-lab.exe','native-awg-lab.exe'),@('runtime/native-core-lab.exe','native-core-lab.exe'),@('tools/native-awg/Run-GuestProxy.ps1','Run-GuestProxy.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
 foreach($case in @(@('baseline-stale',''),@('same-socket-fixed','-UDPLifetime'),@('stop-during-restart','-UDPLifetime -OwnerStopRace'))){
  $code='if(Test-Path -LiteralPath C:\CitaLab\NativeAwg\proxy-migration.json){Remove-Item -LiteralPath C:\CitaLab\NativeAwg\proxy-migration.json -Force -ErrorAction Stop};$p=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\CitaLab\NativeAwg\Run-GuestProxy.ps1 -Retention -CandidateCore -Migration Rebind -Tun -Supervised OPTIONS" -RedirectStandardOutput C:\CitaLab\NativeAwg\udp-life.out -RedirectStandardError C:\CitaLab\NativeAwg\udp-life.err -PassThru;$null=$p.Handle;$p.WaitForExit()'
  W11-Code ($code.Replace('OPTIONS',$case[1]))|Out-Null
  $file=Join-Path $root ('runtime\vm-lab\w11\native-udp-life-'+$run+'-'+$case[0]+'.json');W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\proxy-migration.json',$file)|Out-Null
  $r=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json
  $match=[bool]($r.nativeSha256 -eq $nativeHash -and $r.coreSha256 -eq $coreHash);$clean=[bool]($r.topologyUnchanged -and $r.processesStopped -and $r.credentialFileRemoved)
  $item=@{name=$case[0];file=[IO.Path]::GetFileName($file);success=[bool]($r.success -and $r.supervisionPassed -and $match -and $clean -and $r.tunNoFallback -and $r.tunUdpNoFallback);hashesMatched=$match;clean=$clean;failedStage=$r.failedStage;sameSocketRecovered=$r.sameSocketUdpRecovered;directRetained=$r.directRetained;udpBefore=$r.retention.client.udpBefore;udpAfter=$r.retention.client.udpAfter;dnsFailure=$r.retention.client.dnsFailure;restartMilliseconds=$r.childRestartMilliseconds}
  if($case[0] -eq 'baseline-stale'){$item.negativeControl=[bool]($r.retention.client.udpBefore -eq 3 -and $r.retention.client.udpAfter -lt 3 -and $r.retention.client.dnsFailure -eq 'ReadTimeout');$item.success=[bool]($item.success -and $item.negativeControl)}
  if($case[0] -eq 'same-socket-fixed'){$item.success=[bool]($item.success -and $r.sameSocketUdpRecovered -and $r.directRetained)}
  if($case[0] -eq 'stop-during-restart'){$item.success=[bool]($item.success -and $r.stopDuringBackoff -and $r.stopAck.childPid -eq 0 -and $r.directRetained)}
  $report.cases+=,$item;$item|ConvertTo-Json -Compress|Write-Output
 }
 $report.success=[bool]($report.cases.Count -eq 3 -and -not($report.cases|Where-Object {-not $_.success}))
}catch{$report.errorType=$_.Exception.GetType().Name}
finally{$labCredential=$null;$labVmCredential=$null;$report.nativeSha256=$nativeHash;$report.coreSha256=$coreHash;$report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 7|Set-Content (Join-Path $root ('runtime\native-awg-udp-life-'+$run+'.json')) -Encoding UTF8}
if(-not $report.success){throw 'UDP lifetime checkpoint incomplete; inspect preserved receipts'}
