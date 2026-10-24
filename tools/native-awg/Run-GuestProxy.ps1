param([switch]$CrashOwner,[switch]$Retention,[switch]$InboundLoss)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$root='C:\CitaLab\NativeAwg';$exe=Join-Path $root 'native-awg-lab.exe'
$report=@{success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='Authenticated TCP-only loopback AWG proxy and isolated CITA core chain; no installed session change'}
$owner=$null;$router=$null;$retaining=$null;$stage='Preconditions';$private=Join-Path $root ('private\proxy-'+[guid]::NewGuid().ToString('N'))
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
function Control([string]$action){
 $ack=$owner.StandardOutput.ReadLineAsync()
 $command=@{Session=$session;Generation=1;Action=$action};if($action -eq 'Pause'){$command.Milliseconds=3000;$command.IncomingOnly=[bool]$InboundLoss}
 $owner.StandardInput.WriteLine(($command|ConvertTo-Json -Compress));if(-not $ack.Wait(2000)){throw 'Control acknowledgement deadline'}
 $result=$ack.Result|ConvertFrom-Json;if($result.stage -ne $action -or $result.session -ne $session -or $result.generation -ne 1){throw 'Control acknowledgement mismatch'};return $result
}
try{
 if(($InboundLoss -and -not $Retention) -or ($CrashOwner -and $Retention)){throw 'Choose one supported scenario'}
 if(Get-Process smart-router,vpn-bridge,RoutePilot,native-awg-lab -ErrorAction SilentlyContinue){throw 'Guest must be idle'}
 . C:\CitaLab\PlatformNext\WireSockCli.ps1
 $cli='C:\Program Files\WireSock Secure Connect\command-line\wiresock-connect-cli.exe'
 if((Get-WireSockState (Read-HelperOutput $cli 'status') 'SmartRouting-AWG2') -ne 'Disconnected'){throw 'WireSock must be disconnected'}
 $before=Topology
 New-Item -ItemType Directory -Path $private|Out-Null
 $rng=[Security.Cryptography.RandomNumberGenerator]::Create();$bytes=New-Object byte[] 32;try{$rng.GetBytes($bytes);$token=([BitConverter]::ToString($bytes)).Replace('-','').ToLowerInvariant()}finally{$rng.Dispose();[Array]::Clear($bytes,0,32)}
 $session=[guid]::NewGuid().ToString('N');$stage='OwnerStart'
 $mode=if($Retention){'--serve-proxy-loss-lab'}else{'--serve-proxy'}
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
 $path=Join-Path $private 'core.json';[IO.File]::WriteAllText($path,($configuration|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false));$configuration=$null
 $core='C:\CitaLab\PlatformNext\runtime\smart-router.exe';$router=[Diagnostics.Process]::Start((ProcessInfo $core ('run -c "'+$path+'"')));$routerOutput=$router.StandardOutput.ReadToEndAsync();$routerErrors=$router.StandardError.ReadToEndAsync()
 Start-Sleep -Milliseconds 700;$chain=Client $port ''; $report.routerChainHttps=[bool]($chain.exitCode -eq 0 -and $chain.result.success -and $chain.result.egressHash -eq $reference.serverEgressHash)
 $owner.Refresh();$router.Refresh();$report.resourceSample=@{nativePrivateBytes=$owner.PrivateMemorySize64;nativeWorkingSetBytes=$owner.WorkingSet64;routerPrivateBytes=$router.PrivateMemorySize64;routerWorkingSetBytes=$router.WorkingSet64;scope='Single post-HTTPS process sample, not steady-state or a comparison with WireSock'}
 if($Retention){
  $stage='ChainRetention';$retaining=[Diagnostics.Process]::Start((ProcessInfo $exe '--proxy-retention-client'));$clientErrors=$retaining.StandardError.ReadToEndAsync();$clientReady=$retaining.StandardOutput.ReadLineAsync()
  $retaining.StandardInput.WriteLine((@{Port=$port}|ConvertTo-Json -Compress));if(-not $clientReady.Wait(18000)){throw 'Retention warmup deadline'}
  if(($clientReady.Result|ConvertFrom-Json).stage -ne 'RetentionClientReady'){throw 'Retention warmup failed'}
  $baseline=Control 'Snapshot';$pause=Control 'Pause'
  $retaining.StandardInput.WriteLine('{"Continue":true}');$retaining.StandardInput.Close();$clientOutput=$retaining.StandardOutput.ReadToEndAsync()
  if(-not $retaining.WaitForExit(18000)){throw 'Retention response deadline'}
  $client=$clientOutput.GetAwaiter().GetResult()|ConvertFrom-Json;$afterLoss=Control 'Snapshot'
  $dropped=if($InboundLoss){$afterLoss.droppedIncoming -gt $baseline.droppedIncoming}else{$afterLoss.droppedOutgoing -gt $baseline.droppedOutgoing}
  $report.retention=@{client=$client;before=$baseline;after=$afterLoss;faultMilliseconds=3000;incomingOnly=[bool]$InboundLoss}
  $report.retention.success=[bool]($retaining.ExitCode -eq 0 -and $client.success -and $client.egressHash -eq $reference.serverEgressHash -and $baseline.dials -eq $afterLoss.dials -and $baseline.bindOpens -eq 1 -and $afterLoss.bindOpens -eq 1 -and $dropped -and $client.requestMilliseconds -ge 2500)
 }
 $stage='OwnerEof';if($CrashOwner){$stage='OwnerCrash';$owner.Kill()}else{$owner.StandardInput.Close()};$report.ownerStopped=[bool]$owner.WaitForExit(6000);$report.ownerExitClean=[bool]($report.ownerStopped -and $owner.ExitCode -eq 0)
 $report.ownerScenario=if($CrashOwner){'KilledTransport'}else{'ClosedOwnerStdin'}
 $report.expectedOwnerExit=[bool]($report.ownerStopped -and (($CrashOwner -and $owner.ExitCode -ne 0) -or (-not $CrashOwner -and $report.ownerExitClean)))
 $after=Client $port '';$report.noFallbackAfterOwnerStop=[bool]($after.exitCode -eq 1 -and -not $after.result.success)
 $report.success=[bool]($report.generationMatched -and $report.wrongSecretRefused -and $report.noSecretRefused -and $report.nativeHttps -and $report.routerChainHttps -and $report.expectedOwnerExit -and $report.noFallbackAfterOwnerStop)
 if($Retention -and -not $report.retention.success){$report.success=$false}
}catch{$report.failedStage=$stage;$report.errorType=$_.Exception.GetType().Name}
finally{
 foreach($process in @($retaining,$router,$owner)){if($process){if(-not $process.HasExited){$process.Kill();$process.WaitForExit()};$process.Dispose()}}
 $token=$null;$report.finishedUtc=[datetime]::UtcNow.ToString('o')
 # Delete just this run's credential-bearing file, never another run's folder.
 try{if($path -and (Test-Path -LiteralPath $path)){Remove-Item -LiteralPath $path -Force};$report.credentialFileRemoved=[bool](-not $path -or -not (Test-Path -LiteralPath $path))}catch{$report.credentialFileRemoved=$false}
 if(-not $report.credentialFileRemoved){$report.success=$false}
 $report.processesStopped=[bool](-not (Get-Process native-awg-lab,smart-router -ErrorAction SilentlyContinue))
 if(-not $report.processesStopped){$report.success=$false}
 if($before){$report.topologyUnchanged=[bool]($before -eq (Topology));if(-not $report.topologyUnchanged){$report.success=$false}}
 $report.finishedUtc=[datetime]::UtcNow.ToString('o')
 $file=if($Retention){'proxy-retention.json'}else{'proxy-result.json'}
 $report|ConvertTo-Json -Depth 8|Set-Content (Join-Path $root $file) -Encoding UTF8
}
if(-not $report.success){exit 1}
