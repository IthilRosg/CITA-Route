param([switch]$RetryCaptureOnly)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$summaryPath=Join-Path $root 'runtime\native-awg-tun-acceptance.json'
$summary=@{schema=1;success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='Ordinary OS IPv4 TCP/TLS and UDP through scoped TUN→core→native AWG, single VMware subnet';cases=@()}
$nativeHash=(Get-FileHash (Join-Path $root 'runtime\native-awg-lab.exe')).Hash;$coreHash=(Get-FileHash (Join-Path $root 'runtime\native-core-lab.exe')).Hash
. (Join-Path $root 'runtime\vm-lab\W11-Calls.ps1')
try{
 if(-not $RetryCaptureOnly -and (Test-Path -LiteralPath $summaryPath)){
  $history=Join-Path $root ('runtime\vm-lab\w11\history\tun-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $history|Out-Null
  Copy-Item -LiteralPath $summaryPath -Destination (Join-Path $history 'acceptance.json')
  foreach($name in @('rebind','ipv4','lease-off','active-crash','capture')){$old=Join-Path $root ('runtime\vm-lab\w11\native-tun-final-'+$name+'.json');if(Test-Path -LiteralPath $old){Copy-Item -LiteralPath $old -Destination $history}}
  $summary.previousSeries=$history
 }
 W11-Call @('start',$labVmx,'nogui')|Out-Null
 foreach($pair in @(@('runtime/native-awg-lab.exe','native-awg-lab.exe'),@('runtime/native-core-lab.exe','native-core-lab.exe'),@('tools/native-awg/Run-GuestProxy.ps1','Run-GuestProxy.ps1'),@('tools/native-awg/Run-GuestCapture.ps1','Run-GuestCapture.ps1'),@('tools/native-awg/Guest-IPv4Migration.ps1','Guest-IPv4Migration.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
 $capture=@('capture','Run-GuestCapture.ps1','-ProxyChain -Rebind -Tun','capture-tun.json')
 $scenarios=@(
  @('rebind','Run-GuestProxy.ps1','-Retention -CandidateCore -Migration Rebind -Tun','proxy-migration.json'),
  @('ipv4','Run-GuestProxy.ps1','-Retention -CandidateCore -Migration GuestIPv4 -Tun','proxy-migration.json'),
  @('lease-off','Run-GuestProxy.ps1','-Retention -CandidateCore -Migration GuestIPv4 -Tun -DisableRetentionLease','proxy-migration.json'),
  @('active-crash','Run-GuestProxy.ps1','-Retention -CandidateCore -Migration Rebind -Tun -TunOwnerCrash','proxy-migration.json'),
  $capture
 )
 if($RetryCaptureOnly){$previous=Get-Content $summaryPath -Raw|ConvertFrom-Json;$done=@($previous.cases|Where-Object {$_.name -ne 'capture'});if($done.Count -ne 4 -or ($done|Where-Object {-not $_.success}) -or $previous.captureAttempts -or $previous.nativeSha256 -ne $nativeHash -or $previous.coreSha256 -ne $coreHash){throw 'Retry requires four passes, same binaries and no earlier retry'};$summary.cases=$done;$summary.captureAttempts=@(@{file='native-tun-final-capture.json';success=[bool](($previous.cases|Where-Object {$_.name -eq 'capture'}).success)});$scenarios=@(,$capture)}
 foreach($case in $scenarios){
  $code='$p=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\CitaLab\NativeAwg\SCRIPT OPTIONS" -RedirectStandardOutput C:\CitaLab\NativeAwg\tun-final.out -RedirectStandardError C:\CitaLab\NativeAwg\tun-final.err -PassThru;$null=$p.Handle;$p.WaitForExit()'
  W11-Code ($code.Replace('SCRIPT',$case[1]).Replace('OPTIONS',$case[2]))|Out-Null
  $suffix=if($RetryCaptureOnly){'-repeat'}else{''};$file=Join-Path $root ('runtime\vm-lab\w11\native-tun-final-'+$case[0]+$suffix+'.json')
  W11-Call @('copyFileFromGuestToHost',$labVmx,('C:\CitaLab\NativeAwg\'+$case[3]),$file)|Out-Null
  $result=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json;$probe=if($case[0] -eq 'capture'){$result.proxyProbe}else{$result}
  $hashes=[bool]($probe.nativeSha256 -eq $nativeHash -and $probe.coreSha256 -eq $coreHash);$clean=[bool]($probe.topologyUnchanged -and $probe.processesStopped -and $probe.credentialFileRemoved)
  $item=@{name=$case[0];success=[bool]($result.success -and $hashes -and $clean -and $probe.tunNoFallback -and $probe.tunUdpNoFallback);hashesMatched=$hashes;clean=$clean;failedStage=$probe.failedStage;udpContinuity=$probe.udpContinuity;tcpDials=$probe.retention.client.dialsAfter;tcpRequestMilliseconds=$probe.retention.client.requestMilliseconds;udpAfter=$probe.retention.client.udpAfter;expectedReset=$probe.expectedDefaultReset;expectedCrash=$probe.expectedActiveTunClose}
  $summary.cases+=,$item;if($RetryCaptureOnly){$summary.captureAttempts+=@{file=[IO.Path]::GetFileName($file);success=$item.success}};$item|ConvertTo-Json -Compress|Write-Output
 }
 $summary.success=[bool]($summary.cases.Count -eq 5 -and -not ($summary.cases|Where-Object {-not $_.success}))
}catch{$summary.errorType=$_.Exception.GetType().Name}
finally{$labCredential=$null;$labVmCredential=$null;$summary.nativeSha256=$nativeHash;$summary.coreSha256=$coreHash;$summary.finishedUtc=[datetime]::UtcNow.ToString('o');$summary|ConvertTo-Json -Depth 7|Set-Content $summaryPath -Encoding UTF8}
if(-not $summary.success){throw 'TUN checkpoint incomplete; inspect preserved receipts'}
