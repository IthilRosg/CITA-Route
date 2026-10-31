param([Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$Run,[switch]$TcpOnly)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$root='C:\CitaLab\VoiceReliable';$rule='CitaCandidateVoice-'+$Run;$result=Join-Path $root ('candidate-result-'+$Run+'.json');$output=Join-Path $root ('candidate-sample-'+$Run+'.json');$report=@{success=$false;firewallRemoved=$false};$watchdog=$null;$client=$null
try {
 $parts=[IO.File]::ReadAllText((Join-Path $root 'metadata.txt')).Trim().Split('|');$ip=$null
 if($parts.Count -ne 2 -or -not [Net.IPAddress]::TryParse($parts[0],[ref]$ip) -or $ip.AddressFamily -ne 'InterNetwork' -or $parts[1] -notmatch '^[a-f0-9]{64}$'){throw 'Invalid laboratory metadata'}
 $expected=[IO.File]::ReadAllText('C:\CitaLab\voice-vpn-ip.txt').Trim();$actual=[string](Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 12)
 if($expected -ne $actual){throw 'VPN egress mismatch'}
 $report.egressMatched=$true;$report.routeInterface=(Find-NetRoute -RemoteIPAddress $ip.ToString()|Where-Object {$_.InterfaceAlias}|Select-Object -First 1 -ExpandProperty InterfaceAlias)
 if($report.routeInterface -ne 'Cita-NativeLab'){throw 'Laboratory TUN route required'}
 $cleanup="Start-Sleep -Seconds 240; if((Get-CimInstance Win32_ComputerSystemProduct).UUID -eq 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){Get-NetFirewallRule -Name '$rule' -ErrorAction SilentlyContinue | Remove-NetFirewallRule}"
 $watchdog=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @('-NoProfile','-NonInteractive','-EncodedCommand',[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cleanup))) -PassThru
 $image=Join-Path $root 'voice-quality-lab.exe'
 New-NetFirewallRule -Name $rule -DisplayName $rule -Program $image -Direction Inbound -Protocol UDP -RemoteAddress $ip.ToString() -Action Allow -Profile Any|Out-Null
 $arguments=@(('"'+(Join-Path $root 'metadata.txt')+'"'),('"'+$output+'"'));if($TcpOnly){$arguments+='--tcp-only'}
 $client=Start-Process -FilePath $image -ArgumentList $arguments -WindowStyle Hidden -PassThru
 $waitMs=if($TcpOnly){75000}else{200000};if(-not $client.WaitForExit($waitMs)){Stop-Process -Id $client.Id -Force;throw 'Measurement deadline'}
 if(Test-Path -LiteralPath $output){$report.metrics=Get-Content -LiteralPath $output -Raw|ConvertFrom-Json}
 $report.clientExit=$client.ExitCode
 if($client.ExitCode -ne 0 -or -not $report.metrics.success){throw 'Incomplete or insufficient measured load'}
 $report.success=$true
}catch{$report.errorType=$_.Exception.GetType().Name;$report.errorCode=$_.FullyQualifiedErrorId}
finally{
 if($client -and -not $client.HasExited){Stop-Process -Id $client.Id -Force}
 try{Remove-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue;$report.firewallRemoved=-not [bool](Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue)}catch{}
 if(-not $report.firewallRemoved){$report.success=$false}
 if($report.firewallRemoved -and $watchdog -and -not $watchdog.HasExited){Stop-Process -Id $watchdog.Id -Force}
 $report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $result -Encoding UTF8
}
