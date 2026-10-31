param([Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$Run,[ValidateSet(0,1280,1400,1420)][int]$LabMtu=0)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$root='C:\CitaLab\VoiceReliable';$private='C:\CitaLab\NativeAwg\private';$profile=Join-Path $private 'peer.conf';$owner='C:\CitaLab\NativeAwg\native-awg-lab.exe';$meta=Join-Path $private ('proxy-meta-'+$Run+'.tmp');$out=Join-Path $root ('proxy-sample-'+$Run+'.json');$receipt=Join-Path $root ('proxy-result-'+$Run+'.json');$report=@{success=$false;cleanup=$false;scope='AWG userspace SOCKS proxy without system TUN'};$p=$null;$client=$null;$guard=$null
try{
 if(-not(Test-Path $owner) -or -not(Test-Path $profile) -or (Test-Path $meta)){throw 'Invalid fixture state'}
 $report.ownerSha256=(Get-FileHash -LiteralPath $owner -Algorithm SHA256).Hash
 if(Get-NetAdapter -Name 'Cita-NativeLab','Cita-Native' -ErrorAction SilentlyContinue){throw 'Guest native tunnel must be idle'}
 if(@(Get-CimInstance Win32_Service|Where-Object {$_.Name -like 'AmneziaWGTunnel$*' -and $_.State -ne 'Stopped'}).Count -gt 0 -or (Get-Process native-awg-lab -ErrorAction SilentlyContinue)){throw 'Another AWG test must be stopped'}
 $report.noConflictingGuestTunnels=$true
 $server=[IO.File]::ReadAllText((Join-Path $root 'metadata.txt')).Trim().Split('|');$ip=$null
 if($server.Count -ne 2 -or -not [Net.IPAddress]::TryParse($server[0],[ref]$ip) -or $ip.AddressFamily -ne 'InterNetwork' -or $server[1] -notmatch '^[a-f0-9]{64}$'){throw 'Invalid fixture metadata'}
 $bytes=New-Object byte[] 32;[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes);$token=([BitConverter]::ToString($bytes)).Replace('-','').ToLowerInvariant();$id=[guid]::NewGuid().ToString('N')
 $psi=New-Object Diagnostics.ProcessStartInfo;$psi.FileName=$owner;$psi.Arguments='--serve-proxy';$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.RedirectStandardInput=$true;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
 $p=[Diagnostics.Process]::Start($psi);$ticks=$p.StartTime.ToUniversalTime().Ticks
 $cleanup="Start-Sleep -Seconds 140; if((Get-CimInstance Win32_ComputerSystemProduct).UUID -eq 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){`$p=Get-Process -Id $($p.Id) -ErrorAction SilentlyContinue;if(`$p -and `$p.StartTime.ToUniversalTime().Ticks -eq $ticks -and `$p.Path -eq '$owner'){Stop-Process -Id `$p.Id -Force};Remove-Item -LiteralPath '$meta' -Force -ErrorAction SilentlyContinue}"
 $guard=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @('-NoProfile','-NonInteractive','-EncodedCommand',[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cleanup))) -PassThru
 $err=$p.StandardError.ReadToEndAsync();$configText=[IO.File]::ReadAllText($profile)
 if($LabMtu -ne 0){$pattern='(?mi)^[ \t]*MTU[ \t]*=[ \t]*\d+[ \t]*(?=\r?$)';if([regex]::Matches($configText,$pattern).Count -ne 1){throw 'One explicit MTU required'};$configText=[regex]::Replace($configText,$pattern,('MTU = '+$LabMtu));$report.labMtu=$LabMtu}
 $spec=@{Config=$configText;Token=$token;Session=$id;Generation=1}|ConvertTo-Json -Compress;$configText=$null;$p.StandardInput.WriteLine($spec);$p.StandardInput.Flush();$spec=$null
 $line=$p.StandardOutput.ReadLineAsync();if(-not $line.Wait(15000)){throw 'Proxy readiness deadline'};$ready=$line.Result|ConvertFrom-Json
 if($ready.stage -ne 'ListeningUnverified' -or $ready.session -ne $id -or $ready.port -lt 1 -or $ready.port -gt 65535){throw 'Proxy identity mismatch'}
 [IO.File]::WriteAllText($meta,($server[0]+'|'+$server[1]+'|'+$ready.port+'|'+$token));Set-Acl -LiteralPath $meta -AclObject (Get-Acl -LiteralPath $profile)
 $exe=Join-Path $root 'voice-quality-lab.exe';$client=Start-Process -FilePath $exe -ArgumentList @(('"'+$meta+'"'),('"'+$out+'"'),'--proxy-tcp-only') -WindowStyle Hidden -PassThru
 if(-not $client.WaitForExit(75000)){Stop-Process -Id $client.Id -Force;throw 'Measurement deadline'};if(Test-Path $out){$report.metrics=Get-Content -LiteralPath $out -Raw|ConvertFrom-Json};$report.clientExit=$client.ExitCode;$report.success=($client.ExitCode -eq 0 -and $report.metrics.success)
}catch{$report.errorType=$_.Exception.GetType().Name;$report.errorCode=$_.FullyQualifiedErrorId}
finally{
 if($client -and -not $client.HasExited){Stop-Process -Id $client.Id -Force}
 if($p){try{$p.StandardInput.Close();if(-not $p.WaitForExit(10000)){$p.Kill();$p.WaitForExit()}}catch{};$report.proxyStopped=$p.HasExited;$p.Dispose()}else{$report.proxyStopped=$true}
 Remove-Item -LiteralPath $meta -Force -ErrorAction SilentlyContinue;$report.cleanup=($report.proxyStopped -and -not(Test-Path $meta));if(-not $report.cleanup){$report.success=$false}
 if($report.cleanup -and $guard -and -not $guard.HasExited){Stop-Process -Id $guard.Id -Force}
 $report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $receipt -Encoding UTF8
}
