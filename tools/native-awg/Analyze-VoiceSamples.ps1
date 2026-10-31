[CmdletBinding()]
param([Parameter(Mandatory)][string[]]$Report)
$ErrorActionPreference='Stop'
foreach($item in $Report){
 $file=[IO.Path]::GetFullPath($item)
 $data=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json
 if(-not $data.metrics -or -not $data.metrics.samples){throw 'Missing voice test samples'}
 $phases=@()
 foreach($group in @($data.metrics.samples|Group-Object phase)){
  $samples=@($group.Group);$sent=0L;$received=0L;$late=0L;$gap=0.0;$p95=0.0
  foreach($sample in $samples){
   if($sample.sent -lt 0 -or $sample.received -lt 0 -or $sample.received -gt $sample.sent){throw 'Invalid packet counts'}
   $sent+=[long]$sample.sent;$received+=[long]$sample.received;$late+=[long]$sample.lateOver250Ms
   $gap=[Math]::Max($gap,[double]$sample.maxReceiveGapMs);$p95=[Math]::Max($p95,[double]$sample.p95RttMs)
  }
  # streamBytes is the phase total repeated in each sample, not per-sample bytes.
  $values=@($samples|ForEach-Object {[long]$_.streamBytes}|Sort-Object -Unique)
  $hasLoad=$group.Name -match '^upload(\d+)Mbps$';$requestedMbps=if($hasLoad){[int]$Matches[1]}else{0}
  # The upload fixture sends for 38 s per 40 s phase. A nonzero transfer
  # alone does not prove that the intended pressure was achieved.
  $expected=if($hasLoad){$requestedMbps*1000000.0*38/8}else{0}
  $phaseIndex=switch($requestedMbps){2{1} 4{2} 8{3} default{-1}}
  $hasAck=$phaseIndex -gt 0 -and $null -ne $data.metrics.ackByPhase -and $null -ne $data.metrics.bytesReceivedByPhase -and @($data.metrics.ackByPhase).Count -gt $phaseIndex -and @($data.metrics.bytesReceivedByPhase).Count -gt $phaseIndex
  $receivedBytes=if($hasAck -and $data.metrics.ackByPhase[$phaseIndex] -eq $true){[long]$data.metrics.bytesReceivedByPhase[$phaseIndex]}else{$null}
  $ratio=if($hasLoad -and $null -ne $receivedBytes){[Math]::Round($receivedBytes/$expected,3)}else{$null}
  $loadConfirmed=(-not $hasLoad) -or ($null -ne $ratio -and $ratio -ge 0.8)
  $phases+=@{phase=$group.Name;sent=$sent;received=$received;lossPercent=if($sent -gt 0){[Math]::Round(100.0*($sent-$received)/$sent,3)}else{$null};udpQualityPassed=($sent -eq 2000 -and $received*100 -ge $sent*95);lateOver250Ms=$late;maxGapMs=$gap;maxSampleP95Ms=$p95;loadConfirmed=$loadConfirmed;loadRatio=$ratio;loadBytes=if($hasLoad -and $values.Count -eq 1){$values[0]}else{$null}}
 }
 $expectedPhases=@('baseline','upload2Mbps','upload4Mbps','upload8Mbps');$phaseSetValid=($phases.Count -eq 4 -and @($expectedPhases|Where-Object {$_ -notin @($phases.phase)}).Count -eq 0)
 @{report=[IO.Path]::GetFileName($file);mode=$data.mode;durationSeconds=$data.metrics.durationSeconds;scope='Synthetic UDP echo and TCP load; no Discord endpoint or actual call';udpMinimumReplyPercent=95;udpQualityPassed=($phaseSetValid -and @($phases|Where-Object {-not $_.udpQualityPassed}).Count -eq 0);allLoadsConfirmed=($phaseSetValid -and @($phases|Where-Object {-not $_.loadConfirmed}).Count -eq 0);phases=$phases}|ConvertTo-Json -Depth 5 -Compress
}
