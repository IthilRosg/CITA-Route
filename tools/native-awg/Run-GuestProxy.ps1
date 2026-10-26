param([switch]$CrashOwner,[switch]$Retention,[switch]$InboundLoss,[ValidateSet(500,2000,3000,10000)][int]$FaultMilliseconds=3000,[switch]$LinkInterruption,[switch]$DirectRetention,[switch]$CandidateCore,[switch]$ActiveOwnerStop,[switch]$DisableRetentionLease,[switch]$Udp,[ValidateSet('None','Rebind','GuestIPv4')][string]$Migration='None',[switch]$Tun,[switch]$TunOwnerCrash,[switch]$AutoRecovery,[switch]$Supervised,[switch]$OwnerStopRace,[switch]$UDPLifetime,[switch]$Coordinated,[switch]$AgentBackend,[switch]$LiteralControl,[switch]$SupervisedLoss)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$root='C:\CitaLab\NativeAwg';$exe=Join-Path $root 'native-awg-lab.exe'
$report=@{success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='Authenticated TCP-only loopback AWG proxy and isolated CITA core chain; no installed session change'}
$owner=$null;$router=$null;$retaining=$null;$stage='Preconditions';$private=Join-Path $root ('private\proxy-'+[guid]::NewGuid().ToString('N'))
$linkAdapter=$null;$linkRestore=$null
$directClient=$null
$migrationUdp=$null
$ipStatePath=$null
$tunRescue=$null
function Topology{
 $text=@(Get-NetRoute -AddressFamily IPv4|ForEach-Object {'route|'+$_.InterfaceIndex+'|'+$_.DestinationPrefix+'|'+$_.NextHop+'|'+$_.RouteMetric})+@(Get-NetAdapter -IncludeHidden|ForEach-Object {'nic|'+$_.InterfaceGuid+'|'+$_.Name+'|'+$_.Status})
 $sha=[Security.Cryptography.SHA256]::Create();try{return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes((($text|Sort-Object)-join "`n"))))).Replace('-','')}finally{$sha.Dispose()}
}
function ProcessInfo([string]$file,[string]$arguments){
 $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$file;$info.Arguments=$arguments;$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;return $info
}
function Client([int]$port,[string]$token){
 $p=[Diagnostics.Process]::Start((ProcessInfo $exe '--proxy-client'));$output=$p.StandardOutput.ReadToEndAsync();$errors=$p.StandardError.ReadToEndAsync()
 try{$p.StandardInput.WriteLine((@{Port=$port;Token=$token;LiteralIPv4=[bool]($Tun -or $LiteralControl)}|ConvertTo-Json -Compress));$p.StandardInput.Close();if(-not $p.WaitForExit(18000)){$p.Kill();$p.WaitForExit();throw 'Client deadline'};$result=$output.GetAwaiter().GetResult()|ConvertFrom-Json;$null=$errors.GetAwaiter().GetResult();return @{exitCode=$p.ExitCode;result=$result}}finally{if(-not $p.HasExited){$p.Kill();$p.WaitForExit()};$p.Dispose()}
}
function UdpClient([int]$port,[string]$token){
 $p=[Diagnostics.Process]::Start((ProcessInfo $exe '--proxy-udp-client'));$output=$p.StandardOutput.ReadToEndAsync();$errors=$p.StandardError.ReadToEndAsync()
 try{$p.StandardInput.WriteLine((@{Port=$port;Token=$token;Server=$script:udpServer}|ConvertTo-Json -Compress));$p.StandardInput.Close();if(-not $p.WaitForExit(20000)){throw 'UDP client deadline'};$result=$output.GetAwaiter().GetResult()|ConvertFrom-Json;$null=$errors.GetAwaiter().GetResult();return @{exitCode=$p.ExitCode;result=$result}}finally{if(-not $p.HasExited){$p.Kill();$p.WaitForExit()};$p.Dispose()}
}
function Control([string]$action){
 $ack=$owner.StandardOutput.ReadLineAsync()
 $command=@{Session=$session;Generation=1;Action=$action};if($action -in @('Pause','PauseChild')){$command.Milliseconds=$FaultMilliseconds;$command.IncomingOnly=[bool]$InboundLoss}
 $owner.StandardInput.WriteLine(($command|ConvertTo-Json -Compress));if(-not $ack.Wait(2000)){throw 'Control acknowledgement deadline'}
 $result=$ack.Result|ConvertFrom-Json;if($result.stage -ne $action -or $result.session -ne $session -or $result.generation -ne 1){throw 'Control acknowledgement mismatch'};return $result
}
function ReadResetEvent{
 if(-not $script:resetEvent.Wait(10000)){throw 'Core interface reset not observed'}
 $eventText=$script:resetEvent.GetAwaiter().GetResult()
 if($eventText -notmatch '^CITA_LAB_INTERFACE_RESET closed=(\d+) retained=(\d+)$'){throw 'Unexpected core reset receipt'}
 $event=@{closed=[int]$Matches[1];retained=[int]$Matches[2]}
 if($Tun){$barrier=$router.StandardError.ReadLineAsync();if(-not $barrier.Wait(10000) -or $barrier.Result -ne 'CITA_LAB_INTERFACE_RESET_DONE'){throw 'TUN callbacks completion not observed'};$event.callbacksCompleted=$true}
 $script:resetEvent=$router.StandardError.ReadLineAsync();return $event
}
function TunRoundTrip([string]$fault='None'){
 $p=[Diagnostics.Process]::Start((ProcessInfo $exe '--tun-client'));$errors=$p.StandardError.ReadToEndAsync();$line=$p.StandardOutput.ReadLineAsync()
 try{
  $p.StandardInput.WriteLine((@{DnsServer=$script:udpServer}|ConvertTo-Json -Compress));if(-not $line.Wait(30000)){throw 'Fresh TUN warmup deadline'}
  $warmup=$line.Result|ConvertFrom-Json;if($warmup.stage -ne 'RetentionClientReady'){return $warmup}
  if($fault -eq 'Crash'){$beforeFault=Control 'Snapshot';$null=Control 'CrashChild';$afterFault=WaitOwner ($beforeFault.epoch+1) 'RunningUnverified';$script:roundTripFault=@{before=$beforeFault;after=$afterFault}}
  if($fault -eq 'Blackhole'){$beforeFault=WaitHealth 'Reachable';$null=Control 'BlackholeChild';$script:roundTripFault=@{before=$beforeFault}}
  $p.StandardInput.WriteLine('{"Continue":true}');$p.StandardInput.Close();$output=$p.StandardOutput.ReadToEndAsync();if(-not $p.WaitForExit(20000)){throw 'Fresh TUN result deadline'};return ($output.GetAwaiter().GetResult()|ConvertFrom-Json)
 }finally{if(-not $p.HasExited){$p.Kill();$p.WaitForExit()};$p.Dispose()}
}
function WaitHealth([string]$state){
 for($i=0;$i -lt 20;$i++){$s=Control 'Snapshot';if($s.health -eq $state){return $s};Start-Sleep -Milliseconds 500};throw 'Coordinated health state deadline'
}
function WaitOwner([int]$epoch,[string]$state){
 for($i=0;$i -lt 10;$i++){$s=Control 'Snapshot';if($s.state -eq $state -and $s.epoch -eq $epoch){return $s};Start-Sleep -Milliseconds 500};throw 'Owner state deadline'
}
try{
 if($SupervisedLoss -and (-not $Retention -or -not $CandidateCore -or -not $LiteralControl -or $Migration -ne 'None' -or $Supervised -or $Coordinated -or $AgentBackend -or $LinkInterruption -or $DirectRetention)){throw 'Supervised loss requires standalone literal control fixture'}
 if(($InboundLoss -and -not $Retention) -or ($CrashOwner -and $Retention)){throw 'Choose one supported scenario'}
 if($LinkInterruption -and (-not $Retention -or $InboundLoss -or $FaultMilliseconds -ne 3000)){throw 'Link interruption requires three-second retention without synthetic loss'}
 if($DirectRetention -and -not $Retention){throw 'Direct diagnostic requires retention'}
 if($ActiveOwnerStop -and $Retention){throw 'Active owner stop is separate from loss test'}
 if($DisableRetentionLease -and -not $CandidateCore){throw 'Lease-off control requires candidate core'}
 if($Udp -and ($Retention -or $CandidateCore -or $DirectRetention)){throw 'UDP lifecycle is a separate non-retention lab mode'}
 if($Migration -ne 'None' -and (-not $Retention -or -not $CandidateCore -or $LinkInterruption -or $InboundLoss -or ($DisableRetentionLease -and -not $Tun))){throw 'Migration requires standalone candidate TCP retention'}
 if($Tun -and ($Migration -eq 'None' -or -not $CandidateCore)){throw 'Scoped TUN requires migration scenario'}
 if($TunOwnerCrash -and (-not $Tun -or $Migration -ne 'Rebind')){throw 'Active TUN crash requires rebind scenario'}
 if($AutoRecovery -and (-not $Tun -or $Migration -ne 'Rebind' -or $TunOwnerCrash -or $DisableRetentionLease)){throw 'Recovery requires standalone leased TUN rebind case'}
 if($Supervised -and (-not $Tun -or $Migration -ne 'Rebind' -or $TunOwnerCrash -or $AutoRecovery -or $DisableRetentionLease)){throw 'Supervision requires standalone leased TUN case'}
 if($OwnerStopRace -and -not $Supervised){throw 'Stop race requires supervisor'}
 if($UDPLifetime -and -not $Supervised){throw 'UDP lifetime checkpoint requires supervised TUN'}
 if($Coordinated -and (-not $Supervised -or -not $UDPLifetime)){throw 'Coordinated owner requires native UDP lifetime'}
 if($AgentBackend -and -not $Coordinated){throw 'Agent backend requires coordinated owner'}
 if($Tun -and (Get-NetAdapter -Name 'Cita-NativeLab' -IncludeHidden -ErrorAction SilentlyContinue)){throw 'Lab TUN already exists'}
 if(Get-Process smart-router,vpn-bridge,RoutePilot,native-awg-lab,native-core-lab,Cita.NativeAgentLab -ErrorAction SilentlyContinue){throw 'Guest must be idle'}
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
 if($AutoRecovery){$mode='--serve-proxy-recovery-lab'}
 if($Supervised){$mode='--supervise-proxy-lab'}
 if($Coordinated -or $SupervisedLoss){$mode='--coordinated-owner-lab'}
 $ownerImage=$exe;if($AgentBackend){$ownerImage=Join-Path $root 'AgentBackend\Cita.NativeAgentLab.exe';$mode='--owner-agent-lab C:\CitaLab\NativeAwg';$report.agentBackendSha256=(Get-FileHash (Join-Path $root 'AgentBackend\Cita.NativeAgentLab.dll')).Hash}
 $ownerInfo=ProcessInfo $ownerImage $mode;if($SupervisedLoss){$ownerInfo.EnvironmentVariables['CITA_NATIVE_SHORT_LOSS_LAB']='1';$ownerInfo.EnvironmentVariables.Remove('CITA_NATIVE_PERSISTENT_CONTROL')}
 $owner=[Diagnostics.Process]::Start($ownerInfo);$ownerErrors=$owner.StandardError.ReadToEndAsync();$line=$owner.StandardOutput.ReadLineAsync()
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
 if($Tun){
  $configuration.inbounds+=@{type='tun';tag='native-tun';interface_name='Cita-NativeLab';address=@('172.30.127.1/30');route_address=@('1.1.1.1/32',($script:udpServer+'/32'))|Sort-Object -Unique;auto_route=$true;strict_route=$false;dns_mode='disabled';stack='gvisor';mtu=1400}
  $configuration.outbounds+=@{type='socks';tag='native-udp';server='127.0.0.1';server_port=[int]$ready.port;version='5';username='cita';password=$token;network='udp'}
  if($UDPLifetime){$configuration.outbounds[-1].cita_native_udp_lifetime=$true}
  $configuration.route.rules=@(@{network='udp';action='route';outbound='native-udp'})+$configuration.route.rules
  $report.scope='Ordinary OS TCP/TLS and UDP through scoped IPv4 /32 TUN routes and native AWG; no application proxy or TLS override'
 }
 $path=Join-Path $private 'core.json';[IO.File]::WriteAllText($path,($configuration|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false));$configuration=$null
 $core=if($CandidateCore -or $Udp){'C:\CitaLab\NativeAwg\native-core-lab.exe'}else{'C:\CitaLab\PlatformNext\runtime\smart-router.exe'}
 $report.coreSha256=(Get-FileHash -LiteralPath $core).Hash
 $routerInfo=ProcessInfo $core ('run -c "'+$path+'"');if($Udp){$routerInfo.EnvironmentVariables['CITA_NATIVE_UDP_LAB']='1'}
 if($Tun){$routerInfo.EnvironmentVariables['CITA_NATIVE_TUN_LAB']='1'}
 if($UDPLifetime){$routerInfo.EnvironmentVariables['CITA_NATIVE_UDP_LIFETIME_LAB']='1'}
 $router=[Diagnostics.Process]::Start($routerInfo);$routerOutput=$router.StandardOutput.ReadToEndAsync()
 if($CandidateCore){$script:resetEvent=$router.StandardError.ReadLineAsync()}else{$routerErrors=$router.StandardError.ReadToEndAsync()}
 if($Tun){
  $rescueCode="Start-Sleep -Seconds 90;if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){exit 1};`$p=Get-Process -Id $($router.Id) -ErrorAction SilentlyContinue;if(`$p -and `$p.StartTime.ToUniversalTime().Ticks -eq $($router.StartTime.ToUniversalTime().Ticks)){Stop-Process -Id `$p.Id -Force}"
  $tunRescue=[Diagnostics.Process]::Start((ProcessInfo 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' ('-NoProfile -NonInteractive -EncodedCommand '+[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($rescueCode)))))
  for($i=0;$i -lt 100;$i++){if(Get-NetRoute -DestinationPrefix '1.1.1.1/32' -ErrorAction SilentlyContinue|Where-Object {$_.InterfaceAlias -eq 'Cita-NativeLab'}){break};if($router.HasExited){throw 'TUN core exited'};Start-Sleep -Milliseconds 100}
  $report.tunRoute=[bool](Get-NetRoute -DestinationPrefix '1.1.1.1/32' -ErrorAction SilentlyContinue|Where-Object {$_.InterfaceAlias -eq 'Cita-NativeLab'});if(-not $report.tunRoute){throw 'TUN route deadline'}
  $report.tunRoutePrefixes=@(Get-NetRoute -InterfaceAlias 'Cita-NativeLab' -AddressFamily IPv4|ForEach-Object {$_.DestinationPrefix}|Sort-Object -Unique)
  if($report.tunRoutePrefixes|Where-Object {$_ -in @('0.0.0.0/0','0.0.0.0/1','128.0.0.0/1')}){throw 'TUN route scope wider than approved'}
 }
 Start-Sleep -Milliseconds 700;$chain=Client $port '';$report.chainBaseline=$chain;$report.chainBaselineTarget=if($Tun -or $LiteralControl){'LiteralIPv4'}else{'Hostname'}; $report.routerChainHttps=[bool]($chain.exitCode -eq 0 -and $chain.result.success -and $chain.result.egressHash -eq $reference.serverEgressHash)
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
  $clientMode=if($Tun){'--tun-client'}else{'--proxy-retention-client'}
  $stage='ChainRetention';$retaining=[Diagnostics.Process]::Start((ProcessInfo $exe $clientMode));$clientErrors=$retaining.StandardError.ReadToEndAsync();$clientReady=$retaining.StandardOutput.ReadLineAsync()
  $retentionSpec=@{Port=$port;FaultMilliseconds=$FaultMilliseconds;HoldForFinal=[bool]($CandidateCore -and $LinkInterruption);LiteralIPv4=[bool]$LiteralControl};if($DirectRetention){$retentionSpec.Port=[int]$ready.port;$retentionSpec.Token=$token}
  if($Tun){$retentionSpec=@{DnsServer=$script:udpServer;HoldForFinal=[bool]$TunOwnerCrash}}
  $retaining.StandardInput.WriteLine(($retentionSpec|ConvertTo-Json -Compress));$retentionSpec=$null;$warmupBudget=if($Tun){30000}else{18000};if(-not $clientReady.Wait($warmupBudget)){throw 'Retention warmup deadline'}
  $warmupResult=$clientReady.Result|ConvertFrom-Json
  if($warmupResult.stage -ne 'RetentionClientReady'){$report.clientWarmup=$warmupResult;throw 'Retention warmup failed'}
  if($Migration -ne 'None' -and -not $Tun){
   $migrationUdp=[Diagnostics.Process]::Start((ProcessInfo $exe '--proxy-udp-client'));$migrationErrors=$migrationUdp.StandardError.ReadToEndAsync();$migrationReady=$migrationUdp.StandardOutput.ReadLineAsync()
   $migrationUdp.StandardInput.WriteLine((@{Port=[int]$ready.port;Token=$token;Server=$script:udpServer;Hold=$true;Resume=$true}|ConvertTo-Json -Compress))
   if(-not $migrationReady.Wait(15000) -or ($migrationReady.Result|ConvertFrom-Json).stage -ne 'UdpClientReady'){throw 'Migration UDP baseline unavailable'}
  }
  if($CandidateCore -and ($LinkInterruption -or ($Supervised -and $UDPLifetime))){
   $directClient=[Diagnostics.Process]::Start((ProcessInfo $exe '--proxy-retention-client'));$directErrors=$directClient.StandardError.ReadToEndAsync();$directReady=$directClient.StandardOutput.ReadLineAsync()
   $directClient.StandardInput.WriteLine((@{Port=20939;FaultMilliseconds=3000;HoldForFinal=[bool]$Coordinated}|ConvertTo-Json -Compress))
   if(-not $directReady.Wait(18000) -or ($directReady.Result|ConvertFrom-Json).stage -ne 'RetentionClientReady'){throw 'Mixed direct baseline unavailable'}
  }
  if($CandidateCore -and $LinkInterruption){while($script:resetEvent.IsCompleted){$null=ReadResetEvent}}
  $baseline=Control 'Snapshot'
  if($Coordinated){$baseline=WaitHealth 'Reachable'}
  if($SupervisedLoss){$report.ownerBefore=WaitHealth 'Reachable';$baseline=$report.ownerBefore.transport;$report.healthTimeline=@();$faultUtcMs=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds();$report.faultUtcMs=$faultUtcMs;$faultTimer=[Diagnostics.Stopwatch]::StartNew()}
  if($AutoRecovery){
   for($i=0;$i -lt 12 -and $baseline.recovery.state -ne 'Healthy';$i++){Start-Sleep -Seconds 1;$baseline=Control 'Snapshot'}
   if($baseline.recovery.state -ne 'Healthy' -or $baseline.recovery.attempts -ne 0){throw 'Recovery baseline not verified'}
  }
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
  }elseif($SupervisedLoss){$stage='SupervisedBoundedLoss';$null=Control 'PauseChild';$report.faultAcknowledgedMs=$faultTimer.ElapsedMilliseconds
  }elseif($Coordinated){
   $stage='CoordinatedSocketFailure';$null=Control 'BlackholeChild'
   if($OwnerStopRace){$report.stopWindow=WaitHealth 'Suspect';$report.stopAck=Control 'Stop';if(-not $owner.WaitForExit(6000)){throw 'Coordinated stop deadline'};$report.stopDuringBackoff=[bool]($report.stopAck.childPid -eq 0 -and $report.stopAck.epoch -eq 1 -and $report.stopAck.socketRepairs -eq 0 -and $report.stopAck.budgetUsed -eq 0)}
  }elseif($Supervised){
   $stage='ChildCrash';$report.childBefore=$baseline;$null=Control 'CrashChild'
   if($OwnerStopRace){$report.stopWindow=WaitOwner 1 'Restarting';$report.stopAck=Control 'Stop';if(-not $owner.WaitForExit(6000)){throw 'Supervisor stop deadline'};Start-Sleep -Seconds 1;$report.stopDuringBackoff=[bool]($report.stopAck.epoch -eq 1 -and $report.stopAck.childPid -eq 0)}
   else{$report.childAfter=WaitOwner 2 'RunningUnverified';$report.childRestartMilliseconds=$migrationTimer.ElapsedMilliseconds}
  }elseif($AutoRecovery){$report.blackhole=Control 'Blackhole';$stage='AutomaticEncryptedSocketRecovery'}elseif($Migration -eq 'Rebind'){$report.rebind=Control 'Rebind'}elseif($Migration -eq 'GuestIPv4'){
   while($script:resetEvent.IsCompleted){$null=ReadResetEvent}
   $ipStatePath=Join-Path $root ('private\ip-'+$session+'.json')
   $stage='GuestIPv4Change';$ipState=& (Join-Path $root 'Guest-IPv4Migration.ps1') -Action Begin -StatePath $ipStatePath
   $report.guestIpChanged=[bool]$ipState.changed
   $report.interfaceReset=ReadResetEvent
   $report.rebind=Control 'Rebind'
  }else{$pause=Control 'Pause'}
  $retaining.StandardInput.WriteLine('{"Continue":true}')
  if(($CandidateCore -and $LinkInterruption) -or $TunOwnerCrash){$afterLossLine=$retaining.StandardOutput.ReadLineAsync()}else{$retaining.StandardInput.Close();$clientOutput=$retaining.StandardOutput.ReadToEndAsync()}
  if($directClient){$directClient.StandardInput.WriteLine('{"Continue":true}');if($Coordinated){$directMid=$directClient.StandardOutput.ReadLineAsync()}else{$directClient.StandardInput.Close();$directOutput=$directClient.StandardOutput.ReadToEndAsync()}}
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
  if($TunOwnerCrash){
   $stage='ActiveTunCrash';if(-not $afterLossLine.Wait(18000) -or ($afterLossLine.Result|ConvertFrom-Json).stage -ne 'RetentionAfterLoss'){throw 'TUN active stream barrier failed'}
   $beforeCrash=Control 'Snapshot';$owner.Kill();$owner.WaitForExit()
   $retaining.StandardInput.WriteLine('{"Continue":true}');$retaining.StandardInput.Close();$clientOutput=$retaining.StandardOutput.ReadToEndAsync()
  }
  if($SupervisedLoss){for($sample=0;$sample -lt 48;$sample++){if($retaining.WaitForExit(500)){break};$snapshot=Control 'Snapshot';$report.healthTimeline+=,@{elapsedMs=$faultTimer.ElapsedMilliseconds;health=$snapshot.health;epoch=$snapshot.epoch;socketRepairs=$snapshot.socketRepairs;restarts=$snapshot.restarts;probe=$snapshot.healthProbe}}}
  if(-not $retaining.WaitForExit(30000)){throw 'Retention response deadline'}
  $client=$clientOutput.GetAwaiter().GetResult()|ConvertFrom-Json
  if($migrationUdp){if(-not $migrationUdp.WaitForExit(15000)){throw 'Migration UDP response deadline'};$report.migrationUdp=$migrationOutput.GetAwaiter().GetResult()|ConvertFrom-Json}
  $afterLoss=if($TunOwnerCrash){$beforeCrash}elseif($OwnerStopRace){$report.stopAck}else{Control 'Snapshot'}
  if($SupervisedLoss){
   for($fresh=0;$fresh -lt 20;$fresh++){$report.ownerAfter=Control 'Snapshot';$probe=$report.ownerAfter.healthProbe;if($report.ownerAfter.health -eq 'Reachable' -and $probe.https -eq $true -and $probe.udp -eq $true -and $probe.completedUnixMilliseconds -ge ($faultUtcMs+$FaultMilliseconds)){break};Start-Sleep -Milliseconds 500}
   $report.postFaultFreshHealth=[bool]($report.ownerAfter.health -eq 'Reachable' -and $probe.https -eq $true -and $probe.udp -eq $true -and $probe.completedUnixMilliseconds -ge ($faultUtcMs+$FaultMilliseconds));$afterLoss=$report.ownerAfter.transport
  }
  if($Coordinated -and -not $OwnerStopRace){
   $report.firstRepair=WaitHealth 'Reachable'
   $report.coordinatedSocketRetained=[bool]($client.success -and $client.dialsAfter -eq 1 -and $client.udpAfter -eq 3 -and $report.firstRepair.epoch -eq 1 -and $report.firstRepair.socketRepairs -eq 1 -and $report.firstRepair.budgetUsed -eq 1 -and $baseline.transport.bindOpens -eq 1 -and $report.firstRepair.transport.bindOpens -eq 2 -and $baseline.transport.outerPort -ne $report.firstRepair.transport.outerPort)
   $stage='CoordinatedChildCrash';$report.afterCoordinatedCrash=TunRoundTrip 'Crash';$report.crashTransition=$script:roundTripFault;$report.afterRestart=WaitHealth 'Reachable'
   $stage='CoordinatedSecondRepair';$report.secondRepairClient=TunRoundTrip 'Blackhole';$report.secondRepair=WaitHealth 'Reachable'
   $stage='SharedBudgetExhaustion';$null=Control 'CrashChild';$report.sharedExhausted=WaitOwner 2 'Exhausted'
   $report.coordinatedPassed=[bool]($report.coordinatedSocketRetained -and $report.afterCoordinatedCrash.udpBefore -eq 3 -and $report.afterCoordinatedCrash.udpAfter -eq 3 -and $report.afterCoordinatedCrash.dialsAfter -gt 1 -and $report.afterRestart.budgetUsed -eq 2 -and $report.secondRepairClient.success -and $report.secondRepair.socketRepairs -eq 2 -and $report.secondRepair.restarts -eq 1 -and $report.secondRepair.budgetUsed -eq 3 -and $report.sharedExhausted.budgetUsed -eq 3 -and $report.sharedExhausted.restarts -eq 1 -and $report.sharedExhausted.childPid -eq 0)
  }elseif($Supervised -and -not $OwnerStopRace){
   $stage='FreshOsTrafficAfterChildCrash';$report.freshAfterCrash=TunRoundTrip
   $report.oldStreamClosed=[bool]($client.dialsAfter -gt 1 -or -not $client.sameExit)
   $report.childRestarted=[bool]($report.childBefore.epoch -eq 1 -and $report.childAfter.epoch -eq 2 -and $report.childAfter.restarts -eq 1 -and $report.childAfter.childPid -ne $report.childBefore.childPid)
   $stage='SupervisorRestartBudget';$null=Control 'CrashChild';$report.thirdChild=WaitOwner 3 'RunningUnverified';$null=Control 'CrashChild';$report.exhausted=WaitOwner 3 'Exhausted'
   Start-Sleep -Seconds 1;$report.exhaustedStable=Control 'Snapshot'
   $report.restartBudgetEnforced=[bool]($report.exhaustedStable.state -eq 'Exhausted' -and $report.exhaustedStable.restarts -eq 2 -and $report.exhaustedStable.childPid -eq 0)
  }
  if($AutoRecovery){
   for($i=0;$i -lt 12 -and ($afterLoss.recovery.state -ne 'Healthy' -or $afterLoss.recovery.successes -lt 1);$i++){Start-Sleep -Seconds 1;$afterLoss=Control 'Snapshot'}
   $report.automaticRecovery=[bool]($afterLoss.recovery.state -eq 'Healthy' -and $afterLoss.recovery.attempts -eq 1 -and $afterLoss.recovery.successes -eq 1)
   $report.recoveryStatus=$afterLoss.recovery
   $report.recoveryElapsedMilliseconds=$migrationTimer.ElapsedMilliseconds
  }
  if($directClient){
   if($Coordinated){if(-not $directMid.Wait(20000) -or ($directMid.Result|ConvertFrom-Json).stage -ne 'RetentionAfterLoss'){throw 'Direct intermediate barrier failed'};$directClient.StandardInput.WriteLine('{"Continue":true}');$directClient.StandardInput.Close();$directOutput=$directClient.StandardOutput.ReadToEndAsync()}
   if(-not $directClient.WaitForExit(30000)){throw 'Mixed direct result deadline'}
   $report.directAfterLink=$directOutput.GetAwaiter().GetResult()|ConvertFrom-Json
   $report.directResetObserved=[bool]($report.directAfterLink.dialsBefore -eq 1 -and $report.directAfterLink.dialsAfter -gt 1 -and -not $report.directAfterLink.success)
   if($UDPLifetime){$report.directRetained=[bool]($report.directAfterLink.success -and $report.directAfterLink.dialsAfter -eq 1)}
  }
  $dropped=if($LinkInterruption){$report.linkDisabled}else{if($InboundLoss){$afterLoss.droppedIncoming -gt $baseline.droppedIncoming}else{$afterLoss.droppedOutgoing -gt $baseline.droppedOutgoing}}
  $report.retention=@{client=$client;before=$baseline;after=$afterLoss;faultMilliseconds=$FaultMilliseconds;incomingOnly=[bool]$InboundLoss;linkInterruption=[bool]$LinkInterruption;coreBypassed=[bool]$DirectRetention}
  $report.retention.success=[bool]($retaining.ExitCode -eq 0 -and $client.success -and $client.egressHash -eq $reference.serverEgressHash -and $baseline.dials -eq $afterLoss.dials -and $baseline.bindOpens -eq 1 -and $afterLoss.bindOpens -eq 1 -and $dropped -and $client.requestMilliseconds -ge ($FaultMilliseconds-$(if($LiteralControl){100}else{500})))
  if($SupervisedLoss){$report.retention.success=[bool]($retaining.ExitCode -eq 0 -and $client.success -and $client.egressHash -eq $reference.serverEgressHash -and $dropped -and $report.ownerAfter.epoch -eq $report.ownerBefore.epoch -and $report.ownerAfter.restarts -eq 0 -and $report.postFaultFreshHealth -and $report.ownerAfter.health -eq 'Reachable' -and $afterLoss.bindOpens -eq (1+$report.ownerAfter.socketRepairs));$report.supervisedLoss=$true}
  if($Migration -ne 'None'){$report.retention.Remove('faultMilliseconds');$report.migrationElapsedMilliseconds=$migrationTimer.ElapsedMilliseconds;$report.migrationMode=$Migration;$report.retention.success=[bool]($client.success -and $client.egressHash -eq $reference.serverEgressHash -and $baseline.dials -eq $afterLoss.dials -and $baseline.bindOpens -eq 1 -and $afterLoss.bindOpens -eq 2 -and $baseline.outerPort -ne $afterLoss.outerPort -and $baseline.udpDials -eq 1 -and $afterLoss.udpDials -eq 1 -and $report.migrationUdp.success -and $report.migrationUdp.resumedAssociation)}
  if($Tun){$report.tun=$true;$udpBound=$baseline.udpDials+$(if($Migration -eq 'GuestIPv4'){1}else{0});$report.retention.success=[bool]($client.success -and $client.tcpFromTun -and $client.udpFromTun -and $client.egressHash -eq $reference.serverEgressHash -and $baseline.dials -eq $afterLoss.dials -and $baseline.bindOpens -eq 1 -and $afterLoss.bindOpens -eq 2 -and $baseline.outerPort -ne $afterLoss.outerPort -and $baseline.udpDials -ge 1 -and $baseline.udpDials -le 2 -and $afterLoss.udpDials -ge $baseline.udpDials -and $afterLoss.udpDials -le $udpBound -and $client.udpBefore -eq 3 -and $client.udpAfter -eq 3);$report.udpContinuity=if($afterLoss.udpDials -eq $baseline.udpDials){'Retained'}else{'RecoveredWithNewCoreAssociation'}}
  if($Migration -eq 'GuestIPv4' -and (-not $report.guestIpChanged -or $report.interfaceReset.retained -lt 1)){$report.retention.success=$false}
  if($CandidateCore -and $LinkInterruption -and -not $DisableRetentionLease -and $report.interfaceReset.retained -lt 1){$report.retention.success=$false}
 }
 $stage='OwnerEof';$crashCase=[bool]($CrashOwner -or $TunOwnerCrash);if(-not $owner.HasExited){if($crashCase){$stage='OwnerCrash';$owner.Kill()}else{$owner.StandardInput.Close()}};$report.ownerStopped=[bool]$owner.WaitForExit(6000);$report.ownerExitClean=[bool]($report.ownerStopped -and $owner.ExitCode -eq 0)
 $report.ownerScenario=if($crashCase){'KilledTransport'}else{'ClosedOwnerStdin'}
 $report.expectedOwnerExit=[bool]($report.ownerStopped -and (($crashCase -and $owner.ExitCode -ne 0) -or (-not $crashCase -and $report.ownerExitClean)))
 if($ActiveOwnerStop){
  $stage='ActiveOwnerStopped';$retaining.StandardInput.WriteLine('{"Continue":true}');$retaining.StandardInput.Close();$activeOutput=$retaining.StandardOutput.ReadToEndAsync()
  if(-not $retaining.WaitForExit(18000)){throw 'Active stop client deadline'}
  $report.activeOwnerResult=$activeOutput.GetAwaiter().GetResult()|ConvertFrom-Json
  if($Udp){$report.activeStreamClosed=[bool]($retaining.ExitCode -eq 0 -and $report.activeOwnerResult.success -and $report.activeOwnerResult.noReplyAfterClose)}else{$report.activeStreamClosed=[bool]($retaining.ExitCode -eq 1 -and -not $report.activeOwnerResult.success -and -not $report.activeOwnerResult.sameExit)}
 }
 $after=Client $port '';$report.noFallbackAfterOwnerStop=[bool]($after.exitCode -eq 1 -and -not $after.result.success)
 if($Tun){
  $probe=[Diagnostics.Process]::Start((ProcessInfo 'C:\Windows\System32\curl.exe' '--noproxy "*" --connect-timeout 2 --max-time 4 --silent --output NUL https://1.1.1.1/cdn-cgi/trace'))
  $probeOut=$probe.StandardOutput.ReadToEndAsync();$probeError=$probe.StandardError.ReadToEndAsync()
  try{if(-not $probe.WaitForExit(6000)){$probe.Kill();$probe.WaitForExit();throw 'TUN failed-transport probe deadline'};$report.tunNoFallback=[bool]($probe.ExitCode -ne 0 -and (Get-NetRoute -DestinationPrefix '1.1.1.1/32' -ErrorAction SilentlyContinue|Where-Object {$_.InterfaceAlias -eq 'Cita-NativeLab'}))}finally{$probe.Dispose()}
  $dnsProbe=[Diagnostics.Process]::Start((ProcessInfo $exe '--os-dns-probe'));$dnsOutput=$dnsProbe.StandardOutput.ReadToEndAsync();$dnsError=$dnsProbe.StandardError.ReadToEndAsync()
  try{$dnsProbe.StandardInput.WriteLine((@{Server=$script:udpServer}|ConvertTo-Json -Compress));$dnsProbe.StandardInput.Close();if(-not $dnsProbe.WaitForExit(4000)){$dnsProbe.Kill();$dnsProbe.WaitForExit();throw 'TUN UDP refusal deadline'};$dnsResult=$dnsOutput.GetAwaiter().GetResult()|ConvertFrom-Json;$report.tunUdpNoFallback=[bool]($dnsProbe.ExitCode -eq 1 -and -not $dnsResult.success)}finally{$dnsProbe.Dispose()}
 }
 $report.success=[bool]($report.generationMatched -and $report.wrongSecretRefused -and $report.noSecretRefused -and $report.nativeHttps -and $report.routerChainHttps -and $report.expectedOwnerExit -and $report.noFallbackAfterOwnerStop)
 if($DisableRetentionLease -and ($LinkInterruption -or ($Tun -and $Migration -eq 'GuestIPv4'))){
  $report.expectedDefaultReset=[bool](-not $report.retention.client.success -and $report.retention.client.dialsAfter -gt $report.retention.client.dialsBefore -and $report.retention.after.dials -gt $report.retention.before.dials)
  $report.expectedDefaultReset=[bool]($report.expectedDefaultReset -and $report.interfaceReset.retained -eq 0 -and $report.interfaceReset.closed -gt 0)
  if(-not $report.expectedDefaultReset){$report.success=$false}
 }elseif($Supervised){
  if($OwnerStopRace){$report.supervisionPassed=[bool]($report.stopDuringBackoff -and $report.stopAck.state -eq 'Stopped' -and -not $report.retention.client.sameExit)}
  elseif($Coordinated){$report.supervisionPassed=[bool]$report.coordinatedPassed}else{$report.supervisionPassed=[bool]($report.oldStreamClosed -and $report.childRestarted -and $report.restartBudgetEnforced -and $report.freshAfterCrash.success -and $report.freshAfterCrash.egressHash -eq $reference.serverEgressHash -and $report.freshAfterCrash.tcpFromTun -and $report.freshAfterCrash.udpFromTun)}
  if($UDPLifetime -and -not $OwnerStopRace -and -not $Coordinated){$report.sameSocketUdpRecovered=[bool]($report.retention.client.udpBefore -eq 3 -and $report.retention.client.udpAfter -eq 3 -and -not $report.retention.client.dnsFailure);if(-not $report.sameSocketUdpRecovered){$report.supervisionPassed=$false}}
  if($UDPLifetime -and -not $report.directRetained){$report.supervisionPassed=$false}
  if(-not $report.supervisionPassed){$report.success=$false}
 }elseif($TunOwnerCrash){$report.expectedActiveTunClose=[bool](-not $report.retention.client.success -and -not $report.retention.client.sameExit -and $report.retention.client.udpBefore -eq 3 -and $report.retention.client.udpAfter -eq 3);if(-not $report.expectedActiveTunClose){$report.success=$false}
 }elseif($Retention -and -not $report.retention.success){$report.success=$false}
 if($CandidateCore -and $LinkInterruption -and -not $report.directResetObserved){$report.success=$false}
 if($ActiveOwnerStop -and -not $report.activeStreamClosed){$report.success=$false}
 if($Udp -and (-not $report.udpWrongSecretRefused -or -not $report.udpNoSecretRefused -or -not $report.nativeUdp.result.success -or -not $report.coreUdp.result.success)){$report.success=$false}
 if($Tun -and (-not $report.tunNoFallback -or -not $report.tunUdpNoFallback)){$report.success=$false}
 if($AutoRecovery -and -not $report.automaticRecovery){$report.success=$false}
 $report.candidateCore=[bool]$CandidateCore
 $report.retentionLeaseEnabled=[bool]($CandidateCore -and -not $DisableRetentionLease)
}catch{$report.failedStage=$stage;$report.errorType=$_.Exception.GetType().Name;if($AutoRecovery -and $owner -and -not $owner.HasExited){try{$report.recoveryAtFailure=Control 'Snapshot'}catch{}}}
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
 if($tunRescue){if(-not $tunRescue.HasExited){$tunRescue.Kill();$tunRescue.WaitForExit()};$tunRescue.Dispose()}
 $token=$null;$report.finishedUtc=[datetime]::UtcNow.ToString('o')
 # Delete just this run's credential-bearing file, never another run's folder.
 try{if($path -and (Test-Path -LiteralPath $path)){Remove-Item -LiteralPath $path -Force};$report.credentialFileRemoved=[bool](-not $path -or -not (Test-Path -LiteralPath $path))}catch{$report.credentialFileRemoved=$false}
 if(-not $report.credentialFileRemoved){$report.success=$false}
 $report.processesStopped=[bool](-not (Get-Process native-awg-lab,smart-router,native-core-lab,Cita.NativeAgentLab -ErrorAction SilentlyContinue))
 if(-not $report.processesStopped){$report.success=$false}
 if($before){$report.topologyUnchanged=[bool]($before -eq (Topology));if(-not $report.topologyUnchanged){$report.success=$false}}
 $report.finishedUtc=[datetime]::UtcNow.ToString('o')
 $file=if($Migration -ne 'None'){'proxy-migration.json'}elseif($Udp){'proxy-udp.json'}elseif($Retention){'proxy-retention.json'}else{'proxy-result.json'}
 $report|ConvertTo-Json -Depth 8|Set-Content (Join-Path $root $file) -Encoding UTF8
}
if(-not $report.success){exit 1}
