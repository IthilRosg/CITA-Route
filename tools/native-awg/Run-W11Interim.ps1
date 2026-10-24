param([switch]$RetryCaptureOnly,[ValidateSet('Tcp','Udp')][string]$Suite='Tcp')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$receiptRoot=Join-Path $root 'runtime\vm-lab\w11'
$summary=@{schema=1;success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='VM-only native AWG TCP interface-reset checkpoint, not a production release';cases=@()}
$prefix=if($Suite -eq 'Udp'){'native-udp-interim-'}else{'native-core-interim-'}
$summaryFile=if($Suite -eq 'Udp'){'runtime\native-awg-udp-acceptance.json'}else{'runtime\native-awg-interim-acceptance.json'}
if($Suite -eq 'Udp'){$summary.scope='VM-only native AWG UDP association/lifecycle checkpoint, IPv4 DNS samples, not a production release'}
$nativeHash=(Get-FileHash -LiteralPath (Join-Path $root 'runtime\native-awg-lab.exe')).Hash
$coreHash=(Get-FileHash -LiteralPath (Join-Path $root 'runtime\native-core-lab.exe')).Hash
. (Join-Path $root 'runtime\vm-lab\W11-Calls.ps1')
try{
 W11-Call @('start',$labVmx,'nogui')|Out-Null
 foreach($pair in @(@('runtime/native-awg-lab.exe','native-awg-lab.exe'),@('runtime/native-core-lab.exe','native-core-lab.exe'),@('tools/native-awg/Run-GuestProxy.ps1','Run-GuestProxy.ps1'),@('tools/native-awg/Run-GuestCapture.ps1','Run-GuestCapture.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
 $scenarios=@(
  @('mixed-link','Run-GuestProxy.ps1','-Retention -LinkInterruption -CandidateCore','proxy-retention.json'),
  @('lease-off','Run-GuestProxy.ps1','-Retention -LinkInterruption -CandidateCore -DisableRetentionLease','proxy-retention.json'),
  @('active-stop','Run-GuestProxy.ps1','-CandidateCore -ActiveOwnerStop','proxy-result.json'),
  @('active-crash','Run-GuestProxy.ps1','-CandidateCore -ActiveOwnerStop -CrashOwner','proxy-result.json'),
  @('capture','Run-GuestCapture.ps1','-ProxyChain -ChainRetention -CandidateCore','capture-chain-retention.json')
 )
 if($Suite -eq 'Udp'){
  $scenarios=@(
   @('mixed-link','Run-GuestProxy.ps1','-Retention -LinkInterruption -CandidateCore','proxy-retention.json'),
   @('lifecycle','Run-GuestProxy.ps1','-Udp','proxy-udp.json'),
   @('active-stop','Run-GuestProxy.ps1','-Udp -ActiveOwnerStop','proxy-udp.json'),
   @('active-crash','Run-GuestProxy.ps1','-Udp -ActiveOwnerStop -CrashOwner','proxy-udp.json'),
   @('capture','Run-GuestCapture.ps1','-ProxyChain -Udp','capture-proxy-udp.json')
  )
 }
 if($RetryCaptureOnly){
  $captureScenario=@($scenarios|Where-Object {$_[0] -eq 'capture'})
  $previous=Get-Content (Join-Path $root $summaryFile) -Raw|ConvertFrom-Json
  $completed=@($previous.cases|Where-Object {$_.name -ne 'capture'})
  if($previous.nativeSha256 -ne $nativeHash -or $previous.coreSha256 -ne $coreHash -or $completed.Count -ne 4 -or ($completed|Where-Object {-not $_.success}) -or $previous.captureAttempts){throw 'Capture retry requires unchanged binaries, four passing cases and no previous retry'}
  $summary.cases=$completed
  $summary.captureAttempts=@(@{file=($prefix+'capture.json');success=[bool](($previous.cases|Where-Object {$_.name -eq 'capture'}).success)})
  $scenarios=$captureScenario
 }
 foreach($scenario in $scenarios){
  $code='$p=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\CitaLab\NativeAwg\SCRIPT OPTIONS" -RedirectStandardOutput C:\CitaLab\NativeAwg\interim-runner.out -RedirectStandardError C:\CitaLab\NativeAwg\interim-runner.err -PassThru;$null=$p.Handle;$p.WaitForExit()'
  W11-Code ($code.Replace('SCRIPT',$scenario[1]).Replace('OPTIONS',$scenario[2]))|Out-Null
  $suffix=if($RetryCaptureOnly){'-repeat'}else{''}
  $file=Join-Path $receiptRoot ($prefix+$scenario[0]+$suffix+'.json')
  W11-Call @('copyFileFromGuestToHost',$labVmx,('C:\CitaLab\NativeAwg\'+$scenario[3]),$file)|Out-Null
  $report=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json
  $probe=if($scenario[0] -eq 'capture'){$report.proxyProbe}else{$report}
  $matched=[bool]($probe.nativeSha256 -eq $nativeHash -and $probe.coreSha256 -eq $coreHash)
  $clean=[bool]($probe.processesStopped -and $probe.credentialFileRemoved -and $probe.topologyUnchanged)
  $passed=[bool]($report.success -and $matched -and $clean)
  $result=@{name=$scenario[0];success=$passed;binaryHashesMatched=$matched;clean=$clean;requestMilliseconds=$probe.retention.client.requestMilliseconds;reset=$probe.interfaceReset;activeStreamClosed=$probe.activeStreamClosed}
  if($Suite -eq 'Udp'){$result.nativeQueries=$probe.nativeUdp.result.queries;$result.coreQueries=$probe.coreUdp.result.queries}
  $summary.cases+=,$result
  if($RetryCaptureOnly){$summary.captureAttempts+=@{file=[IO.Path]::GetFileName($file);success=$passed}}
  $result|ConvertTo-Json -Depth 4 -Compress|Write-Output
 }
 $summary.nativeSha256=$nativeHash;$summary.coreSha256=$coreHash
 $summary.success=[bool]($summary.cases.Count -eq 5 -and -not ($summary.cases|Where-Object {-not $_.success}))
}catch{$summary.errorType=$_.Exception.GetType().Name}
finally{
 $labCredential=$null;$labVmCredential=$null
 $summary.finishedUtc=[datetime]::UtcNow.ToString('o')
 $summary|ConvertTo-Json -Depth 7|Set-Content (Join-Path $root $summaryFile) -Encoding UTF8
}
if(-not $summary.success){throw 'Native AWG interim acceptance failed; inspect bounded local receipts'}
