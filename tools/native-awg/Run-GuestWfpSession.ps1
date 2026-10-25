$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$root='C:\CitaLab\NativeAwg';$report=@{schema=1;success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='Compiled policy with persistent TUN-bound WFP guard and core recovery'};$p=$null;$rescue=$null;$guardGroup='CitaNativeSafety-'+[guid]::NewGuid().ToString('N')
$config=Join-Path $root ('private\binding-core-'+[guid]::NewGuid().ToString('N')+'.json');$pidFile=Join-Path $root 'private\binding-core-pid.json'
function Topology{
 $text=@(Get-NetRoute -AddressFamily IPv4|ForEach-Object {'route|'+$_.InterfaceIndex+'|'+$_.DestinationPrefix+'|'+$_.NextHop+'|'+$_.RouteMetric})+@(Get-NetAdapter -IncludeHidden|ForEach-Object {'nic|'+$_.InterfaceGuid+'|'+$_.Name+'|'+$_.Status})
 $sha=[Security.Cryptography.SHA256]::Create();try{return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes((($text|Sort-Object)-join "`n"))))).Replace('-','')}finally{$sha.Dispose()}
}
try{
 if(Get-Process native-awg-lab,native-core-lab,Cita.NativeAgentLab,smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'Guest must be idle'}
 if(Get-NetAdapter -Name Cita-NativeLab -IncludeHidden -ErrorAction SilentlyContinue){throw 'Existing lab TUN refused'}
 . C:\CitaLab\PlatformNext\WireSockCli.ps1
 if((Get-WireSockState (Read-HelperOutput 'C:\Program Files\WireSock Secure Connect\command-line\wiresock-connect-cli.exe' 'status') 'SmartRouting-AWG2') -ne 'Disconnected'){throw 'Guest WireSock must be disconnected'}
 $before=Topology;$physical=@(Get-NetIPConfiguration|Where-Object {$_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up'});if($physical.Count -ne 1){throw 'One physical gateway required'}
 $text=[IO.File]::ReadAllText((Join-Path $root 'private\peer.conf'));if($text -notmatch '(?m)^DNS\s*=\s*(\d+\.\d+\.\d+\.\d+)'){throw 'Literal DNS required'};$dns=$Matches[1];$text=$null
 if(@(Get-NetFirewallProfile|Where-Object {-not $_.Enabled}).Count){throw 'Guest firewall profiles must be enabled'}
 $owner=[guid]::NewGuid().ToString('N');$guardImage=Join-Path $root 'native-guard-lab.exe';$policy=@{Vpn=@('1.1.1.0/24','fdc1:ca7a:100::/64');Direct=@('1.1.1.254/32')};$plan=@{Action='Plan';Run=$owner;Policy=$policy}|ConvertTo-Json -Depth 5 -Compress|& $guardImage|ConvertFrom-Json;if(-not $plan.success){throw 'Cannot compile guard policy'}
 $options=@{log=@{disabled=$true};inbounds=@(@{type='tun';tag='native-tun';interface_name='Cita-NativeLab';address=@('172.30.127.1/30');route_address=@($plan.prefixes|Where-Object {$_ -notmatch ':'});auto_route=$true;strict_route=$false;dns_mode='disabled';stack='gvisor';mtu=1400},@{type='socks';tag='lab-direct';listen='127.0.0.1';listen_port=20939});outbounds=@(@{type='cita_native_lab';tag='native-vpn';owner=$owner},@{type='direct';tag='direct';bind_interface=$physical[0].NetAdapter.Name});route=@{final='native-vpn';rules=@(@{inbound=@('lab-direct');action='route';outbound='direct'})}}
 [IO.File]::WriteAllText($config,($options|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false));$options=$null
 if(Test-Path -LiteralPath $pidFile){Remove-Item -LiteralPath $pidFile -Force}
 $code="Start-Sleep -Seconds 180;if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){exit 1};if(Test-Path -LiteralPath '$pidFile'){`$id=Get-Content -LiteralPath '$pidFile' -Raw|ConvertFrom-Json;`$q=Get-Process -Id `$id.pid -ErrorAction SilentlyContinue;if(`$q -and `$q.StartTime.ToUniversalTime().Ticks -eq `$id.ticks -and `$q.Path -eq '$root\native-core-lab.exe'){Stop-Process -Id `$q.Id -Force}}"
 $code+=";@{Action='Cleanup';Run='$owner'}|ConvertTo-Json -Compress|& '$guardImage'"
 $rescue=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList ('-NoProfile -NonInteractive -EncodedCommand '+[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))) -PassThru
 $report.guardSha256=(Get-FileHash $guardImage).Hash
 [IO.File]::WriteAllText(($config+'.wfp'),(@{Run=$owner;Policy=$policy}|ConvertTo-Json -Depth 5))
 $report.nativeSha256=(Get-FileHash (Join-Path $root 'native-awg-lab.exe')).Hash;$report.coreSha256=(Get-FileHash (Join-Path $root 'native-core-lab.exe')).Hash;$report.agentBackendSha256=(Get-FileHash (Join-Path $root 'AgentBackend\Cita.NativeAgentLab.dll')).Hash
 $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $root 'AgentBackend\Cita.NativeAgentLab.exe'),('--wfp-session-lab '+$root+' '+$config+' '+$owner));$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $p=[Diagnostics.Process]::Start($info);$output=$p.StandardOutput.ReadToEndAsync();$errors=$p.StandardError.ReadToEndAsync();if(-not $p.WaitForExit(160000)){$p.Kill();$p.WaitForExit();throw 'Core binding lab deadline'}
 $report.probes=$output.GetAwaiter().GetResult()|ConvertFrom-Json;$report.success=[bool]($p.ExitCode -eq 0 -and $report.probes.success)
}catch{$report.errorType=$_.Exception.GetType().Name}
finally{
 if($p){if(-not $p.HasExited){$p.Kill();$p.WaitForExit()};$p.Dispose()}
 if(Test-Path -LiteralPath $pidFile){$identity=Get-Content -LiteralPath $pidFile -Raw|ConvertFrom-Json;$q=Get-Process -Id $identity.pid -ErrorAction SilentlyContinue;if($q -and $q.StartTime.ToUniversalTime().Ticks -eq $identity.ticks -and $q.Path -eq (Join-Path $root 'native-core-lab.exe')){$report.forcedCoreCleanup=$true;$report.success=$false;Stop-Process -Id $q.Id -Force};Remove-Item -LiteralPath $pidFile -Force}
 $clean=@{Action='Cleanup';Run=$owner}|ConvertTo-Json -Compress|& $guardImage|ConvertFrom-Json;$report.guardRemoved=[bool]($clean.success -and $clean.rules -eq 0);if(-not $report.guardRemoved){$report.success=$false}
 if($rescue){if(-not $rescue.HasExited){Stop-Process -Id $rescue.Id -Force};$rescue.Dispose()}
 if(Test-Path -LiteralPath ($config+'.wfp')){Remove-Item -LiteralPath ($config+'.wfp') -Force}
 if(Test-Path -LiteralPath $config){Remove-Item -LiteralPath $config -Force};$report.configRemoved=[bool](-not (Test-Path -LiteralPath $config))
 for($i=0;$i -lt 60;$i++){if(-not (Get-Process native-awg-lab,native-core-lab,Cita.NativeAgentLab -ErrorAction SilentlyContinue)){break};Start-Sleep -Milliseconds 100}
 $report.processesStopped=[bool](-not (Get-Process native-awg-lab,native-core-lab,Cita.NativeAgentLab -ErrorAction SilentlyContinue));$report.topologyUnchanged=[bool]($before -and $before -eq (Topology));if(-not $report.configRemoved -or -not $report.processesStopped -or -not $report.topologyUnchanged){$report.success=$false}
 $report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 7|Set-Content (Join-Path $root 'wfp-session-result.json') -Encoding UTF8
}
if(-not $report.success){exit 1}
