param([switch]$CrashOwner,[switch]$Retention,[switch]$InboundLoss,[ValidateSet(3000,10000)][int]$FaultMilliseconds=3000,[switch]$LinkInterruption,[switch]$DirectRetention,[switch]$CandidateCore,[switch]$ActiveOwnerStop,[switch]$DisableRetentionLease,[switch]$Udp,[ValidateSet('None','Rebind','GuestIPv4')][string]$Migration='None')
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$root='C:\CitaLab\NativeAwg';$exe=Join-Path $root 'native-awg-lab.exe'
$report=@{success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='Authenticated TCP-only loopback AWG proxy and isolated CITA core chain; no installed session change'}
$owner=$null;$router=$null;$retaining=$null;$stage='Preconditions';$private=Join-Path $root ('private\proxy-'+[guid]::NewGuid().ToString('N'))
$linkAdapter=$null;$linkRestore=$null
$directClient=$null
$migrationUdp=$null
$ipStatePath=$null
function Topology{
 $text=@(Get-NetRoute -AddressFamily IPv4|ForEach-Object {'route|'+$_.InterfaceIndex+'|'+$_.DestinationPrefix+'|'+$_.NextHop+'|'+$_.RouteMetric})+@(Get-NetAdapter -IncludeHidden|ForEach-Object {'nic|'+$_.InterfaceGuid+'|'+$_.Name+'|'+$_.Status})
 $sha=[Security.Cryptography.SHA256]::Create();try{return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes((($text|Sort-Object)-join "`n"))))).Replace('-','')}finally{$sha.Dispose()}
}
function ProcessInfo([string]$file,[string]$arguments){
 $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$file;$info.Arguments=$arguments;$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;return $info
}
function Client([int]$port,[string]$token){
 $p=[Diagnostics.Process]::Start((ProcessInfo $exe '--proxy-client'));$output=$p.StandardOutput.ReadToEndAsync();$errors=$p.StandardError.ReadToEndAsync()
 try{$p.StandardInput.WriteLine((@{Port=$port;Token=$token}|ConvertTo-Json -Compress));$p.StandardInput.Close();if(-not $p.WaitForExit(18000)){$p.Kill();$p.WaitForExit();throw 'Client deadline'};$result=$output.GetAwaiter().GetResult()|ConvertFrom-Json;$null=$errors.GetAwaiter().GetResult();return @{exitCode=$p.ExitCode;result=$result}}finally{if(-not $p.HasExited){$p.Kill();$p.WaitForExit()};$p.Dispose()}
}
function UdpClient([int]$port,[string]$token){
 $p=[Diagnostics.Process]::Start((ProcessInfo $exe '--proxy-udp-client'));$output=$p.StandardOutput.ReadToEndAsync();$errors=$p.StandardError.ReadToEndAsync()
 try{$p.StandardInput.WriteLine((@{Port=$port;Token=$token;Server=$script:udpServer}|ConvertTo-Json -Compress));$p.StandardInput.Close();if(-not $p.WaitForExit(20000)){throw 'UDP client deadline'};$result=$output.GetAwaiter().GetResult()|ConvertFrom-Json;$null=$errors.GetAwaiter().GetResult();return @{exitCode=$p.ExitCode;result=$result}}finally{if(-not $p.HasExited){$p.Kill();$p.WaitForExit()};$p.Dispose()}
}
function Control([string]$action){
 $ack=$owner.StandardOutput.ReadLineAsync()
 $command=@{Session=$session;Generation=1;Action=$action};if($action -eq 'Pause'){$command.Milliseconds=$FaultMilliseconds;$command.IncomingOnly=[bool]$InboundLoss}
 $owner.StandardInput.WriteLine(($command|ConvertTo-Json -Compress));if(-not $ack.Wait(2000)){throw 'Control acknowledgement deadline'}
 $result=$ack.Result|ConvertFrom-Json;if($result.stage -ne $action -or $result.session -ne $session -or $result.generation -ne 1){throw 'Control acknowledgement mismatch'};return $result
}
function ReadResetEvent{
 if(-not $script:resetEvent.Wait(10000)){throw 'Core interface reset not observed'}
 $eventText=$script:resetEvent.GetAwaiter().GetResult()
 if($eventText -notmatch '^CITA_LAB_INTERFACE_RESET closed=(\d+) retained=(\d+)$'){throw 'Unexpected core reset receipt'}
 $event=@{closed=[int]$Matches[1];retained=[int]$Matches[2]}
 $script:resetEvent=$router.StandardError.ReadLineAsync();return $event
}
try{
 if(($InboundLoss -and -not $Retention) -or ($CrashOwner -and $Retention)){throw 'Choose one supported scenario'}
 if($LinkInterruption -and (-not $Retention -or $InboundLoss -or $FaultMilliseconds -ne 3000)){throw 'Link interruption requires three-second retention without synthetic loss'}
 if($DirectRetention -and -not $Retention){throw 'Direct diagnostic requires retention'}
 if($ActiveOwnerStop -and $Retention){throw 'Active owner stop is separate from loss test'}
 if($DisableRetentionLease -and -not $CandidateCore){throw 'Lease-off control requires candidate core'}
 if($Udp -and ($Retention -or $CandidateCore -or $DirectRetention)){throw 'UDP lifecycle is a separate non-retention lab mode'}
 if($Migration -ne 'None' -and (-not $Retention -or -not $CandidateCore -or $LinkInterruption -or $InboundLoss -or $DisableRetentionLease)){throw 'Migration requires standalone candidate TCP retention'}
 if(Get-Process smart-router,vpn-bridge,RoutePilot,native-awg-lab,native-core-lab -ErrorAction SilentlyContinue){throw 'Guest must be idle'}
 $report.nativeSha256=(Get-FileHash -LiteralPath $exe).Hash
 . C:\CitaLab\PlatformNext\WireSockCli.ps1
 $cli='C:\Program Files\WireSock Secure Connect\command-line\wiresock-connect-cli.exe'
 if((Get-WireSockState (Read-HelperOutput $cli 'status') 'SmartRouting-AWG2') -ne 'Disconnected'){throw 'WireSock must be disconnected'}
 $before=Topology
 New-Item -ItemType Directory -Path $private|Out-Null
 $rng=[Security.Cryptography.RandomNumberGenerator]::Create();$bytes=New-Object byte[] 32;try{$rng.GetBytes($bytes);$token=([BitConverter]::ToString($bytes)).Replace('-','').ToLowerInvariant()}finally{$rng.Dispose();[Array]::Clear($bytes,0,32)}
 $session=[guid]::NewGuid().ToString('N');$stage='OwnerStart'
 $mode=if($Retention){'--serve-proxy-loss-lab'}else{'--serve-proxy'}
 if($Udp){$mode='--serve-proxy-udp-lab';$peerText=[IO.File]::ReadAllText((Join-Path $root 'private\peer.conf'));if($peerText -notmatch '(?m)^DNS\s*=\s*(\d+\.\d+\.\d+\.\d+)'){throw 'UDP lab requires literal DNS'};$script:udpServer=$Matches[1];$peerText=$null}
 if($Migration -ne 'None'){$mode='--serve-proxy-migration-lab';$peerText=[IO.File]::ReadAllText((Join-Path $root 'private\peer.conf'));if($peerText -notmatch '(?m)^DNS\s*=\s*(\d+\.\d+\.\d+\.\d+)'){throw 'Migration requires literal DNS'};$script:udpServer=$Matches[1];$peerText=$null}
 $owner=[Diagnostics.Process]::Start((ProcessInfo $exe $mode));$ownerErrors=$owner.StandardError.ReadToEndAsync();$line=$owner.StandardOutput.ReadLineAsync()
 $owner.StandardInput.WriteLine((@{Config=[IO.File]::ReadAllText((Join-Path $root 'private\peer.conf'));Token=$token;Session=$session;Generation=1}|ConvertTo-Json -Compress))
 if(-not $line.Wait(6000)){throw 'Readiness deadline'};$ready=$line.Result|ConvertFrom-Json
 if($ready.stage -ne 'ListeningUnverified' -or $ready.session -ne $session -or $ready.generation -ne 1 -or $ready.port -le 0){throw 'Owner readiness mismatch'}
 $report.generationMatched=$true;$report.loopbackPort=$ready.port;$reference=[IO.File]::ReadAllText('C:\CitaLab\server-egress-reference.json')|ConvertFrom-Json
 $stage='Authentication';$bad=Client $ready.port ('0'*64);$none=Client $ready.port ''
 $report.wrongSecretRefused=[bool]($bad.exitCode -eq 1 -and -not $bad.result.success);$report.noSecretRefused=[bool]($none.exitCode -eq 1 -and -not $none.result.success)
 $stage='NativeProxyHttps';$good=Client $ready.port $token;$report.nativeHttps=[bool]($good.exitCode -eq 0 -and $good.result.success -and $good.result.egressHash -eq $reference.serverEgressHash)
 # The existing routing core connects to the authenticated transport as SOCKS5.
 # Its lab inbound has no authentication and is reachable only on loopback.
 $stage='RouterChain';$port=20938
 $configuration=@{log=@{disabled=$true};inbounds=@(@{type='socks';tag='lab';listen='127.0.0.1';listen_port=$port});outbounds=@(@{type='socks';tag='native-vpn';server='127.0.0.1';server_port=[int]$ready.port;version='5';username='cita';password=$token;network='tcp'});route=@{final='native-vpn'}}
 if($Udp){$configuration.outbounds[0].Remove('network');$report.scope='Authenticated loopback UDP ASSOCIATE lifecycle, direct native proxy and existing CITA core; IPv4 public DNS samples, not QUIC/game acceptance'}
 if($CandidateCore){
  if(-not $DisableRetentionLease){$configuration.outbounds[0].cita_native_retention=@{session=$session;generation=1}}
  $configuration.inbounds+=@{type='socks';tag='lab-direct';listen='127.0.0.1';listen_port=20939}
  $configuration.outbounds+=@{type='direct';tag='direct'}
  $configuration.route.rules=@(@{inbound=@('lab-direct');action='route';outbound='direct'})
 }
 $path=Join-Path $private 'core.json';[IO.File]::WriteAllText($path,($configuration|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false));$configuration=$null
 $core=if($CandidateCore -or $Udp){'C:\CitaLab\NativeAwg\native-core-lab.exe'}else{'C:\CitaLab\PlatformNext\runtime\smart-router.exe'}
 $report.coreSha256=(Get-FileHash -LiteralPath $core).Hash
 $routerInfo=ProcessInfo $core ('run -c "'+$path+'"');if($Udp){$routerInfo.EnvironmentVariables['CITA_NATIVE_UDP_LAB']='1'}
 $router=[Diagnostics.Process]::Start($routerInfo);$routerOutput=$router.StandardOutput.ReadToEndAsync()
 if($CandidateCore){$script:resetEvent=$router.StandardError.ReadLineAsync()}else{$routerErrors=$router.StandardError.ReadToEndAsync()}
 Start-Sleep -Milliseconds 700;$chain=Client $port ''; $report.routerChainHttps=[bool]($chain.exitCode -eq 0 -and $chain.result.success -and $chain.result.egressHash -eq $reference.serverEgressHash)
 $owner.Refresh();$router.Refresh();$report.resourceSample=@{nativePrivateBytes=$owner.PrivateMemorySize64;nativeWorkingSetBytes=$owner.WorkingSet64;routerPrivateBytes=$router.PrivateMemorySize64;routerWorkingSetBytes=$router.WorkingSet64;scope='Single post-HTTPS process sample, not steady-state or a comparison with WireSock'}
 if($Udp){
  $stage='UdpAuthentication';$badUdp=UdpClient $ready.port ('0'*64);$noneUdp=UdpClient $ready.port ''
  $report.udpWrongSecretRefused=[bool]($badUdp.exitCode -eq 1 -and -not $badUdp.result.success);$report.udpNoSecretRefused=[bool]($noneUdp.exitCode -eq 1 -and -not $noneUdp.result.success)
  $stage='NativeUdp';$report.nativeUdp=UdpClient $ready.port $token
  $stage='CoreUdp';$report.coreUdp=UdpClient $port ''
  if($ActiveOwnerStop){
   $stage='ActiveUdpBaseline';$retaining=[Diagnostics.Process]::Start((ProcessInfo $exe '--proxy-udp-client'));$activeErrors=$retaining.StandardError.ReadToEndAsync();$activeReady=$retaining.StandardOutput.ReadLineAsync()
   $retaining.StandardInput.WriteLine((@{Port=$port;Server=$script:udpServer;Hold=$true}|ConvertTo-Json -Compress))
   if(-not $activeReady.Wait(18000) -or ($activeReady.Result|ConvertFrom-Json).stage -ne 'UdpClientReady'){throw 'Held UDP baseline unavailable'}
  }
 }
 if($ActiveOwnerStop -and -not $Udp){
  $stage='ActiveOwnerBaseline';$retaining=[Diagnostics.Process]::Start((ProcessInfo $exe '--proxy-retention-client'));$activeErrors=$retaining.StandardError.ReadToEndAsync();$activeReady=$retaining.StandardOutput.ReadLineAsync()
  $retaining.StandardInput.WriteLine((@{Port=$port;FaultMilliseconds=3000}|ConvertTo-Json -Compress))
  if(-not $activeReady.Wait(18000) -or ($activeReady.Result|ConvertFrom-Json).stage -ne 'RetentionClientReady'){throw 'Active baseline unavailable'}
 }
 if($Retention){
  $stage='ChainRetention';$retaining=[Diagnostics.Process]::Start((ProcessInfo $exe '--proxy-retention-client'));$clientErrors=$retaining.StandardError.ReadToEndAsync();$clientReady=$retaining.StandardOutput.ReadLineAsync()
  $retentionSpec=@{Port=$port;FaultMilliseconds=$FaultMilliseconds;HoldForFinal=[bool]($CandidateCore -and $LinkInterruption)};if($DirectRetention){$retentionSpec.Port=[int]$ready.port;$retentionSpec.Token=$token}
  $retaining.StandardInput.WriteLine(($retentionSpec|ConvertTo-Json -Compress));$retentionSpec=$null;if(-not $clientReady.Wait(18000)){throw 'Retention warmup deadline'}
  if(($clientReady.Result|ConvertFrom-Json).stage -ne 'RetentionClientReady'){throw 'Retention warmup failed'}
  if($Migration -ne 'None'){
   $migrationUdp=[Diagnostics.Process]::Start((ProcessInfo $exe '--proxy-udp-client'));$migrationErrors=$migrationUdp.StandardError.ReadToEndAsync();$migrationReady=$migrationUdp.StandardOutput.ReadLineAsync()
   $migrationUdp.StandardInput.WriteLine((@{Port=[int]$ready.port;Token=$token;Server=$script:udpServer;Hold=$true;Resume=$true}|ConvertTo-Json -Compress))
   if(-not $migrationReady.Wait(15000) -or ($migrationReady.Result|ConvertFrom-Json).stage -ne 'UdpClientReady'){throw 'Migration UDP baseline unavailable'}
  }
  if($CandidateCore -and $LinkInterruption){
   $directClient=[Diagnostics.Process]::Start((ProcessInfo $exe '--proxy-retention-client'));$directErrors=$directClient.StandardError.ReadToEndAsync();$directReady=$directClient.StandardOutput.ReadLineAsync()
   $directClient.StandardInput.WriteLine('{"Port":20939,"FaultMilliseconds":3000}')
   if(-not $directReady.Wait(18000) -or ($directReady.Result|ConvertFrom-Json).stage -ne 'RetentionClientReady'){throw 'Mixed direct baseline unavailable'}
  }
  if($CandidateCore -and $LinkInterruption){while($script:resetEvent.IsCompleted){$null=ReadResetEvent}}
  $baseline=Control 'Snapshot'
  if($Migration -ne 'None'){$migrationTimer=[Diagnostics.Stopwatch]::StartNew()}
  if($LinkInterruption){
   $stage='GuestLinkInterruption'
   $adapters=@(Get-NetIPConfiguration|Where-Object {$_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up'})
   if($adapters.Count -ne 1){throw 'Exactly one active gateway adapter required'}
   $linkAdapter=$adapters[0].NetAdapter
   $adapterGuid=([guid]$linkAdapter.InterfaceGuid).ToString('D')
   # Independent local recovery helper runs even if this runner throws or dies.
   # Guard by guest UUID and adapter GUID; no host/global network changes.
   $restoreCode="Start-Sleep -Seconds 8;if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){exit 1};Get-NetAdapter -IncludeHidden | Where-Object {[guid]`$_.InterfaceGuid -eq [guid]'$adapterGuid'} | Enable-NetAdapter -Confirm:`$false"
   $restoreInfo=ProcessInfo 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' ('-NoProfile -NonInteractive -EncodedCommand '+[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($restoreCode)))
   $linkRestore=[Diagnostics.Process]::Start($restoreInfo);$restoreOutput=$linkRestore.StandardOutput.ReadToEndAsync();$restoreErrors=$linkRestore.StandardError.ReadToEndAsync();$linkRestore.StandardInput.Close()
   $linkTimer=[Diagnostics.Stopwatch]::StartNew()
   $linkAdapter|Disable-NetAdapter -Confirm:$false
   $disabled=Get-NetAdapter -IncludeHidden|Where-Object {[guid]$_.InterfaceGuid -eq [guid]$adapterGuid}
   $report.linkDisabled=[bool]($disabled.AdminStatus -eq 'Down')
   if(-not $report.linkDisabled){throw 'Adapter disable not confirmed'}
  }elseif($Migration -eq 'Rebind'){$report.rebind=Control 'Rebind'}elseif($Migration -eq 'GuestIPv4'){
   while($script:resetEvent.IsCompleted){$null=ReadResetEvent}
   $ipStatePath=Join-Path $root ('private\ip-'+$session+'.json')
   $stage='GuestIPv4Change';$ipState=& (Join-Path $root 'Guest-IPv4Migration.ps1') -Action Begin -StatePath $ipStatePath
   $report.guestIpChanged=[bool]$ipState.changed
   $report.interfaceReset=ReadResetEvent
   $report.rebind=Control 'Rebind'
  }else{$pause=Control 'Pause'}
  $retaining.StandardInput.WriteLine('{"Continue":true}')
  if($CandidateCore -and $LinkInterruption){$afterLossLine=$retaining.StandardOutput.ReadLineAsync()}else{$retaining.StandardInput.Close();$clientOutput=$retaining.StandardOutput.ReadToEndAsync()}
  if($directClient){$directClient.StandardInput.WriteLine('{"Continue":true}');$directClient.StandardInput.Close();$directOutput=$directClient.StandardOutput.ReadToEndAsync()}
  if($migrationUdp){$migrationUdp.StandardInput.WriteLine('{"Continue":true}');$migrationUdp.StandardInput.Close();$migrationOutput=$migrationUdp.StandardOutput.ReadToEndAsync()}
  if($LinkInterruption){
   Start-Sleep -Milliseconds 3000
   Get-NetAdapter -IncludeHidden|Where-Object {[guid]$_.InterfaceGuid -eq [guid]$adapterGuid}|Enable-NetAdapter -Confirm:$false
   $report.linkDisableToEnableMilliseconds=$linkTimer.ElapsedMilliseconds
  }
  if($CandidateCore -and $LinkInterruption){
   $stage='ConfirmedCoreReset';$report.interfaceReset=ReadResetEvent
   if(-not $afterLossLine.Wait(18000) -or ($afterLossLine.Result|ConvertFrom-Json).stage -ne 'RetentionAfterLoss'){throw 'Second request barrier failed'}
   # A third GET must still reuse TCP AFTER an actual core reset receipt.
   $retaining.StandardInput.WriteLine('{"Continue":true}');$retaining.StandardInput.Close();$clientOutput=$retaining.StandardOutput.ReadToEndAsync()
  }
  if(-not $retaining.WaitForExit(30000)){throw 'Retention response deadline'}
  $client=$clientOutput.GetAwaiter().GetResult()|ConvertFrom-Json
  if($migrationUdp){if(-not $migrationUdp.WaitForExit(15000)){throw 'Migration UDP response deadline'};$report.migrationUdp=$migrationOutput.GetAwaiter().GetResult()|ConvertFrom-Json}
  $afterLoss=Control 'Snapshot'
  if($directClient){
   if(-not $directClient.WaitForExit(30000)){throw 'Mixed direct result deadline'}
   $report.directAfterLink=$directOutput.GetAwaiter().GetResult()|ConvertFrom-Json
   $report.directResetObserved=[bool]($report.directAfterLink.dialsBefore -eq 1 -and $report.directAfterLink.dialsAfter -gt 1 -and -not $report.directAfterLink.success)
  }
  $dropped=if($LinkInterruption){$report.linkDisabled}else{if($InboundLoss){$afterLoss.droppedIncoming -gt $baseline.droppedIncoming}else{$afterLoss.droppedOutgoing -gt $baseline.droppedOutgoing}}
  $report.retention=@{client=$client;before=$baseline;after=$afterLoss;faultMilliseconds=$FaultMilliseconds;incomingOnly=[bool]$InboundLoss;linkInterruption=[bool]$LinkInterruption;coreBypassed=[bool]$DirectRetention}
  $report.retention.success=[bool]($retaining.ExitCode -eq 0 -and $client.success -and $client.egressHash -eq $reference.serverEgressHash -and $baseline.dials -eq $afterLoss.dials -and $baseline.bindOpens -eq 1 -and $afterLoss.bindOpens -eq 1 -and $dropped -and $client.requestMilliseconds -ge ($FaultMilliseconds-500))
  if($Migration -ne 'None'){$report.retention.Remove('faultMilliseconds');$report.migrationElapsedMilliseconds=$migrationTimer.ElapsedMilliseconds;$report.migrationMode=$Migration;$report.retention.success=[bool]($client.success -and $client.egressHash -eq $reference.serverEgressHash -and $baseline.dials -eq $afterLoss.dials -and $baseline.bindOpens -eq 1 -and $afterLoss.bindOpens -eq 2 -and $baseline.outerPort -ne $afterLoss.outerPort -and $baseline.udpDials -eq 1 -and $afterLoss.udpDials -eq 1 -and $report.migrationUdp.success -and $report.migrationUdp.resumedAssociation)}
  if($Migration -eq 'GuestIPv4' -and (-not $report.guestIpChanged -or $report.interfaceReset.retained -lt 1)){$report.retention.success=$false}
  if($CandidateCore -and $LinkInterruption -and -not $DisableRetentionLease -and $report.interfaceReset.retained -lt 1){$report.retention.success=$false}
 }
 $stage='OwnerEof';if($CrashOwner){$stage='OwnerCrash';$owner.Kill()}else{$owner.StandardInput.Close()};$report.ownerStopped=[bool]$owner.WaitForExit(6000);$report.ownerExitClean=[bool]($report.ownerStopped -and $owner.ExitCode -eq 0)
 $report.ownerScenario=if($CrashOwner){'KilledTransport'}else{'ClosedOwnerStdin'}
 $report.expectedOwnerExit=[bool]($report.ownerStopped -and (($CrashOwner -and $owner.ExitCode -ne 0) -or (-not $CrashOwner -and $report.ownerExitClean)))
 if($ActiveOwnerStop){
  $stage='ActiveOwnerStopped';$retaining.StandardInput.WriteLine('{"Continue":true}');$retaining.StandardInput.Close();$activeOutput=$retaining.StandardOutput.ReadToEndAsync()
  if(-not $retaining.WaitForExit(18000)){throw 'Active stop client deadline'}
  $report.activeOwnerResult=$activeOutput.GetAwaiter().GetResult()|ConvertFrom-Json
  if($Udp){$report.activeStreamClosed=[bool]($retaining.ExitCode -eq 0 -and $report.activeOwnerResult.success -and $report.activeOwnerResult.noReplyAfterClose)}else{$report.activeStreamClosed=[bool]($retaining.ExitCode -eq 1 -and -not $report.activeOwnerResult.success -and -not $report.activeOwnerResult.sameExit)}
 }
 $after=Client $port '';$report.noFallbackAfterOwnerStop=[bool]($after.exitCode -eq 1 -and -not $after.result.success)
 $report.success=[bool]($report.generationMatched -and $report.wrongSecretRefused -and $report.noSecretRefused -and $report.nativeHttps -and $report.routerChainHttps -and $report.expectedOwnerExit -and $report.noFallbackAfterOwnerStop)
 if($DisableRetentionLease -and $LinkInterruption){
  $report.expectedDefaultReset=[bool](-not $report.retention.client.success -and $report.retention.client.dialsAfter -gt $report.retention.client.dialsBefore -and $report.retention.after.dials -gt $report.retention.before.dials)
  $report.expectedDefaultReset=[bool]($report.expectedDefaultReset -and $report.interfaceReset.retained -eq 0 -and $report.interfaceReset.closed -gt 0)
  if(-not $report.expectedDefaultReset){$report.success=$false}
 }elseif($Retention -and -not $report.retention.success){$report.success=$false}
 if($CandidateCore -and $LinkInterruption -and -not $report.directResetObserved){$report.success=$false}
 if($ActiveOwnerStop -and -not $report.activeStreamClosed){$report.success=$false}
 if($Udp -and (-not $report.udpWrongSecretRefused -or -not $report.udpNoSecretRefused -or -not $report.nativeUdp.result.success -or -not $report.coreUdp.result.success)){$report.success=$false}
 $report.candidateCore=[bool]$CandidateCore
 $report.retentionLeaseEnabled=[bool]($CandidateCore -and -not $DisableRetentionLease)
}catch{$report.failedStage=$stage;$report.errorType=$_.Exception.GetType().Name}
finally{
 if($ipStatePath -and (Test-Path -LiteralPath $ipStatePath)){try{$ipRestored=& (Join-Path $root 'Guest-IPv4Migration.ps1') -Action Restore -StatePath $ipStatePath;$report.guestIpRestored=[bool]$ipRestored.restored}catch{$report.guestIpRestored=$false};if(-not $report.guestIpRestored){$report.success=$false}}
 if($linkAdapter){
  try{Get-NetAdapter -IncludeHidden|Where-Object {[guid]$_.InterfaceGuid -eq [guid]$linkAdapter.InterfaceGuid}|Enable-NetAdapter -Confirm:$false
   if($linkRestore -and -not $linkRestore.WaitForExit(12000)){throw 'Recovery helper deadline'}
   $restored=Get-NetAdapter -IncludeHidden|Where-Object {[guid]$_.InterfaceGuid -eq [guid]$linkAdapter.InterfaceGuid};$report.linkRestored=[bool]($restored.Status -eq 'Up')
  }catch{$report.linkRestored=$false};if(-not $report.linkRestored){$report.success=$false}
 }
 if($linkRestore){$linkRestore.Dispose()}
 foreach($process in @($migrationUdp,$directClient,$retaining,$router,$owner)){if($process){if(-not $process.HasExited){$process.Kill();$process.WaitForExit()};$process.Dispose()}}
 $token=$null;$report.finishedUtc=[datetime]::UtcNow.ToString('o')
 # Delete just this run's credential-bearing file, never another run's folder.
 try{if($path -and (Test-Path -LiteralPath $path)){Remove-Item -LiteralPath $path -Force};$report.credentialFileRemoved=[bool](-not $path -or -not (Test-Path -LiteralPath $path))}catch{$report.credentialFileRemoved=$false}
 if(-not $report.credentialFileRemoved){$report.success=$false}
 $report.processesStopped=[bool](-not (Get-Process native-awg-lab,smart-router,native-core-lab -ErrorAction SilentlyContinue))
 if(-not $report.processesStopped){$report.success=$false}
 if($before){$report.topologyUnchanged=[bool]($before -eq (Topology));if(-not $report.topologyUnchanged){$report.success=$false}}
 $report.finishedUtc=[datetime]::UtcNow.ToString('o')
 $file=if($Migration -ne 'None'){'proxy-migration.json'}elseif($Udp){'proxy-udp.json'}elseif($Retention){'proxy-retention.json'}else{'proxy-result.json'}
 $report|ConvertTo-Json -Depth 8|Set-Content (Join-Path $root $file) -Encoding UTF8
}
if(-not $report.success){exit 1}
