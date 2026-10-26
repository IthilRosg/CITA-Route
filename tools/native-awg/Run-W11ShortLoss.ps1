param([ValidateSet(500,2000,10000)][int[]]$Durations=@(500,2000,10000))
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$run=[guid]::NewGuid().ToString('N')
$report=@{schema=1;success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='Bounded encrypted UDP loss; retained TCP through candidate core, fixed public IPv4 HTTPS. No watchdog, OS TUN, game or QUIC acceptance.';cases=@()}
$nativeHash=(Get-FileHash (Join-Path $root 'runtime/native-awg-lab.exe')).Hash
$coreHash=(Get-FileHash (Join-Path $root 'runtime/native-core-lab.exe')).Hash
. (Join-Path $root 'runtime/vm-lab/W11-Calls.ps1')
try{
 W11-Call @('start',$labVmx,'nogui')|Out-Null
 foreach($pair in @(@('runtime/native-awg-lab.exe','native-awg-lab.exe'),@('runtime/native-core-lab.exe','native-core-lab.exe'),@('tools/native-awg/Run-GuestProxy.ps1','Run-GuestProxy.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
 foreach($ms in ($Durations|Sort-Object -Unique)){
  foreach($incoming in @($false,$true)){
   $name=([string]$ms)+'-'+$(if($incoming){'inbound'}else{'bidirectional'})
   $options='-Retention -CandidateCore -LiteralControl -FaultMilliseconds '+$ms+$(if($incoming){' -InboundLoss'}else{''})
   $code='Remove-Item -LiteralPath C:\CitaLab\NativeAwg\proxy-retention.json -Force -ErrorAction SilentlyContinue;$p=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\CitaLab\NativeAwg\Run-GuestProxy.ps1 OPTIONS" -RedirectStandardOutput C:\CitaLab\NativeAwg\short-loss.out -RedirectStandardError C:\CitaLab\NativeAwg\short-loss.err -PassThru;$null=$p.Handle;$p.WaitForExit()'
   W11-Code ($code.Replace('OPTIONS',$options))|Out-Null
   $file=Join-Path $root ('runtime/vm-lab/w11/native-short-loss-'+$run+'-'+$name+'.json')
   W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\proxy-retention.json',$file)|Out-Null
   $r=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json
   $match=[bool]($r.nativeSha256 -eq $nativeHash -and $r.coreSha256 -eq $coreHash)
   $clean=[bool]($r.processesStopped -and $r.topologyUnchanged -and $r.credentialFileRemoved)
   $after=$r.retention.after;$before=$r.retention.before;$client=$r.retention.client
   $faultProof=[bool](($incoming -and $after.droppedIncoming -gt 0 -and $after.droppedOutgoing -eq 0) -or (-not $incoming -and $after.droppedOutgoing -gt 0))
   $item=@{name=$name;durationMs=$ms;incomingOnly=$incoming;file=[IO.Path]::GetFileName($file);hashesMatched=$match;clean=$clean;failedStage=$r.failedStage;faultObserved=$faultProof;requestMs=$client.requestMilliseconds;tcpDialsBefore=$client.dialsBefore;tcpDialsAfter=$client.dialsAfter;tcpReused=$client.reusedTcp;sameExit=$client.sameExit;bindOpensBefore=$before.bindOpens;bindOpensAfter=$after.bindOpens;droppedIncoming=$after.droppedIncoming;droppedOutgoing=$after.droppedOutgoing;success=[bool]($r.success -and $match -and $clean -and $faultProof -and $client.reusedTcp -and $client.dialsBefore -eq 1 -and $client.dialsAfter -eq 1)}
   $report.cases+=,$item;$item|ConvertTo-Json -Compress|Write-Output
   if(-not $clean){throw 'Guest cleanup failed; matrix stopped'}
  }
 }
 $report.success=[bool]($report.cases.Count -eq 2*@($Durations|Sort-Object -Unique).Count -and -not($report.cases|Where-Object {-not $_.success}))
}catch{$report.errorType=$_.Exception.GetType().Name}
finally{$labCredential=$null;$labVmCredential=$null;$report.nativeSha256=$nativeHash;$report.coreSha256=$coreHash;$report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 7|Set-Content (Join-Path $root ('runtime/native-awg-short-loss-'+$run+'.json')) -Encoding UTF8}
if(-not $report.success){throw 'Short loss matrix incomplete; inspect preserved receipts'}
