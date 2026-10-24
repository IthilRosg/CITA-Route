param([switch]$UnavailableEndpoint,[switch]$WrongPresharedKey)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$root='C:\CitaLab\NativeAwg';$exe=Join-Path $root 'native-awg-lab.exe'
$report=@{success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='Single native AWG/netstack HTTPS probe; no route, driver, DNS-setting or profile modification'}
function Topology{
 $text=@(Get-NetRoute -AddressFamily IPv4|ForEach-Object {'route|'+$_.InterfaceIndex+'|'+$_.DestinationPrefix+'|'+$_.NextHop+'|'+$_.RouteMetric})+@(Get-NetAdapter -IncludeHidden|ForEach-Object {'nic|'+$_.InterfaceGuid+'|'+$_.Name+'|'+$_.Status})
 $sha=[Security.Cryptography.SHA256]::Create();try{return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes((($text|Sort-Object)-join "`n"))))).Replace('-','')}finally{$sha.Dispose()}
}
$process=$null
try{
 if($UnavailableEndpoint -and $WrongPresharedKey){throw 'Choose one negative scenario'}
 if(Get-Process smart-router,vpn-bridge,RoutePilot,native-awg-lab -ErrorAction SilentlyContinue){throw 'Guest must be idle'}
 $cli='C:\Program Files\WireSock Secure Connect\command-line\wiresock-connect-cli.exe'
 . C:\CitaLab\PlatformNext\WireSockCli.ps1
 if((Get-WireSockState (Read-HelperOutput $cli 'status') 'SmartRouting-AWG2') -ne 'Disconnected'){throw 'WireSock must be independently disconnected'}
 $before=Topology
 $info=New-Object Diagnostics.ProcessStartInfo
 $info.FileName=$exe;$info.Arguments='--probe';$info.UseShellExecute=$false;$info.CreateNoWindow=$true
 $info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $process=[Diagnostics.Process]::Start($info)
 $output=$process.StandardOutput.ReadToEndAsync();$errorOutput=$process.StandardError.ReadToEndAsync()
 $config=[IO.File]::ReadAllText((Join-Path $root 'private\peer.conf'))
 if($WrongPresharedKey){
  $bytes=New-Object byte[] 32;$rng=[Security.Cryptography.RandomNumberGenerator]::Create();try{$rng.GetBytes($bytes)}finally{$rng.Dispose()}
  $key=[Convert]::ToBase64String($bytes);[Array]::Clear($bytes,0,$bytes.Length)
  $config=[regex]::Replace($config,'(?m)^PresharedKey\s*=.*\r?\n?','')
  $config=$config.Replace('[Peer]',('[Peer]'+"`n"+'PresharedKey = '+$key));$key=$null
 }
 if($UnavailableEndpoint){
  # Keep the same approved server IP, use a non-tunnel UDP port. No keys or
  # credentials are sent to a different host; no profile is modified on disk.
  if($config -notmatch '(?m)^Endpoint\s*=\s*(\d+\.\d+\.\d+\.\d+):\d+\s*$'){throw 'Literal endpoint required'}
  $replacement='Endpoint = '+$Matches[1]+':9'
  $config=[regex]::Replace($config,'(?m)^Endpoint\s*=\s*\d+\.\d+\.\d+\.\d+:\d+\s*$',$replacement)
 }
 $process.StandardInput.Write($config);$process.StandardInput.Close();$config=$null
 if(-not $process.WaitForExit(35000)){$process.Kill();$process.WaitForExit();throw 'Native probe deadline exceeded'}
 $native=$output.GetAwaiter().GetResult()|ConvertFrom-Json;$null=$errorOutput.GetAwaiter().GetResult()
 $reference=[IO.File]::ReadAllText('C:\CitaLab\server-egress-reference.json')|ConvertFrom-Json
 $report.exitCode=$process.ExitCode;$report.native=$native
 $report.expectedVpnExit=[bool]($native.success -and $native.egressHash -eq $reference.serverEgressHash)
 $report.topologyUnchanged=($before -eq (Topology))
 if($UnavailableEndpoint -or $WrongPresharedKey){
  $report.expectedFailureObserved=[bool]($process.ExitCode -eq 1 -and -not $native.success -and $native.stage -eq 'HandshakeAndHttps' -and -not $native.egressHash)
  $report.success=[bool]($report.expectedFailureObserved -and $report.topologyUnchanged)
  $report.scope=if($WrongPresharedKey){'Deliberately wrong in-memory PSK, original endpoint: HTTPS must fail; no profile or topology change.'}else{'Unavailable same-server UDP port: HTTPS must fail; no OS topology changes. Not packet-capture leak proof.'}
 }else{$report.success=[bool]($process.ExitCode -eq 0 -and $report.expectedVpnExit -and $report.topologyUnchanged)}
}catch{$report.error=$_.Exception.Message}
finally{
 if($process){if(-not $process.HasExited){$process.Kill();$process.WaitForExit()};$process.Dispose()}
 $name=if($WrongPresharedKey){'probe-wrong-key.json'}elseif($UnavailableEndpoint){'probe-unavailable.json'}else{'probe-result.json'}
 $report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root $name) -Encoding UTF8
}
if(-not $report.success){exit 1}
