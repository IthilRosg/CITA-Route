param([Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$Run,[switch]$ProbeOnly,[ValidateSet(0,1400,1420)][int]$LabMtu=0)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$name='CitaT'+$Run.Substring(0,16);$service='AmneziaWGTunnel$'+$name;$root='C:\CitaLab\VoiceReliable';$private='C:\CitaLab\NativeAwg\private';$source=Join-Path $private 'peer.conf';$config=Join-Path $private ($name+'.conf');$image='C:\Program Files\AmneziaWG\amneziawg.exe';$rule='CitaAwgCompare-'+$Run;$out=Join-Path $root ('awg-result-'+$Run+'.json');$sample=Join-Path $root ('awg-sample-'+$Run+'.json');$report=@{success=$false;cleanup=$false};$guard=$null;$client=$null
try{
 if(-not(Test-Path $image) -or -not(Test-Path $source) -or (Test-Path $config) -or (Get-Service -Name $service -ErrorAction SilentlyContinue)){throw 'Prerequisite or namespace invalid'}
 if(Get-NetAdapter -Name 'Cita-NativeLab' -ErrorAction SilentlyContinue){throw 'Candidate tunnel must be stopped'}
 $text=[IO.File]::ReadAllText($source);if($text -match '(?mi)^\s*(PreUp|PostUp|PreDown|PostDown)\s*='){throw 'Script directives refused'};if($text -notmatch '(?mi)^\s*AllowedIPs\s*=\s*0\.0\.0\.0/0\s*$'){throw 'Full IPv4 test profile required'}
 $mtu=[regex]::Match($text,'(?mi)^\s*MTU\s*=\s*(\d+)\s*$');$report.profileMtu=if($mtu.Success){[int]$mtu.Groups[1].Value}else{$null};$text=$null
 Copy-Item -LiteralPath $source -Destination $config;Set-Acl -LiteralPath $config -AclObject (Get-Acl -LiteralPath $source)
 if($LabMtu -ne 0){$candidateText=[IO.File]::ReadAllText($config);$pattern='(?mi)^[ \t]*MTU[ \t]*=[ \t]*\d+[ \t]*(?=\r?$)';if([regex]::Matches($candidateText,$pattern).Count -ne 1){throw 'One explicit MTU needed for lab comparison'};$candidateText=[regex]::Replace($candidateText,$pattern,('MTU = '+$LabMtu));[IO.File]::WriteAllText($config,$candidateText,[Text.UTF8Encoding]::new($false));$candidateText=$null;$report.labMtu=$LabMtu}
 $cleanup="Start-Sleep -Seconds 150; if((Get-CimInstance Win32_ComputerSystemProduct).UUID -eq 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){ & sc.exe stop '$service' | Out-Null; & sc.exe delete '$service' | Out-Null; Get-NetFirewallRule -Name '$rule' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; for(`$i=0;`$i -lt 20;`$i++){if(-not (Get-Service -Name '$service' -ErrorAction SilentlyContinue)){Remove-Item -LiteralPath '$config' -Force -ErrorAction SilentlyContinue;break};Start-Sleep -Milliseconds 500} }"
 $guard=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @('-NoProfile','-NonInteractive','-EncodedCommand',[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cleanup))) -PassThru
 $stderr=Join-Path $root ('awg-stderr-'+$Run+'.tmp');$stdout=Join-Path $root ('awg-stdout-'+$Run+'.tmp')
 $p=Start-Process -FilePath $image -ArgumentList @('/installtunnelservice',('"'+$config+'"')) -RedirectStandardError $stderr -RedirectStandardOutput $stdout -PassThru -WindowStyle Hidden
 if(-not $p.WaitForExit(20000)){Stop-Process -Id $p.Id -Force;$report.installTimedOut=$true}else{$p.Refresh();$report.installExit=$p.ExitCode}
 if($report.installTimedOut -or ($null -ne $report.installExit -and $report.installExit -ne 0)){$errorText=Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue;$report.installErrorCategory=if($errorText -match '(?i)name'){ 'Name' }elseif($errorText -match '(?i)access'){ 'Access' }else{'Other'}}
 for($i=0;$i -lt 50;$i++){if((Get-Service -Name $service -ErrorAction SilentlyContinue).Status -eq 'Running'){break};Start-Sleep -Milliseconds 400}
 if((Get-Service -Name $service -ErrorAction SilentlyContinue).Status -ne 'Running'){throw 'Tunnel did not start'}
 $report.adapterPresent=[bool](Get-NetAdapter -Name $name -ErrorAction SilentlyContinue)
 if($ProbeOnly){try{
  $trace=@(& curl.exe --noproxy '*' -fsS --connect-timeout 5 --max-time 15 'https://1.1.1.1/cdn-cgi/trace' 2>$null)
  if($LASTEXITCODE -ne 0){throw 'Known-address HTTPS failed'}
  $matchesIp=@($trace|Where-Object {$_ -match '^ip=(\d{1,3}\.){3}\d{1,3}$'});if($matchesIp.Count -ne 1){throw 'Exit proof unavailable'}
  $egress=($matchesIp[0] -split '=',2)[1]
 }catch{
  $awg=Join-Path (Split-Path $image) 'awg.exe';$handshake=@(& $awg show $name latest-handshakes 2>$null);$transfer=@(& $awg show $name transfer 2>$null)
  $report.handshakeObserved=[bool](@($handshake|Where-Object {$_ -match '\s[1-9]\d*$'}).Count -gt 0)
  $report.rxNonzero=[bool](@($transfer|Where-Object {$_ -match '\s[1-9]\d*\s+\d+\s*$'}).Count -gt 0)
  $report.txNonzero=[bool](@($transfer|Where-Object {$_ -match '\s\d+\s+[1-9]\d*\s*$'}).Count -gt 0)
  $report.dnsResolves=[bool](Resolve-DnsName -Name 'api.ipify.org' -Type A -ErrorAction SilentlyContinue)
  $report.publicRouteUsesTunnel=[bool]((Find-NetRoute -RemoteIPAddress '1.1.1.1'|Where-Object {$_.InterfaceAlias}|Select-Object -First 1 -ExpandProperty InterfaceAlias) -eq $name)
  & curl.exe --noproxy '*' -fsS -o NUL --connect-timeout 5 --max-time 10 'https://1.1.1.1/cdn-cgi/trace' 2>$null
  $report.knownAddressHttpsOk=($LASTEXITCODE -eq 0);$report.knownAddressCurlExit=$LASTEXITCODE
  throw 'Independent HTTPS check failed'
 }
 $candidate=[IO.File]::ReadAllText('C:\CitaLab\voice-vpn-ip.txt').Trim();if($egress -ne $candidate){throw 'Independent and candidate egress differ'};$report.egressMatched=$true
 }else{$report.egressProof='RequiresReceiverEvidence'}
 if(-not $ProbeOnly){
  $parts=[IO.File]::ReadAllText((Join-Path $root 'metadata.txt')).Trim().Split('|');$ip=$null;if($parts.Count -ne 2 -or -not [Net.IPAddress]::TryParse($parts[0],[ref]$ip) -or $parts[1] -notmatch '^[a-f0-9]{64}$'){throw 'Invalid fixture metadata'}
  for($i=0;$i -lt 30;$i++){
   try{$route=(Find-NetRoute -RemoteIPAddress $ip.ToString() -ErrorAction Stop|Where-Object {$_.InterfaceAlias}|Select-Object -First 1 -ExpandProperty InterfaceAlias);if($route -eq $name){$report.routeInterface=$route;break}}catch{}
   Start-Sleep -Milliseconds 500
  }
  if($report.routeInterface -ne $name){throw 'Independent tunnel route missing'}
  $exe=Join-Path $root 'voice-quality-lab.exe';New-NetFirewallRule -Name $rule -DisplayName $rule -Program $exe -Direction Inbound -Protocol UDP -RemoteAddress $ip.ToString() -Action Allow -Profile Any|Out-Null
  $client=Start-Process -FilePath $exe -ArgumentList @(('"'+(Join-Path $root 'metadata.txt')+'"'),('"'+$sample+'"'),'--tcp-only') -WindowStyle Hidden -PassThru
  if(-not $client.WaitForExit(75000)){Stop-Process -Id $client.Id -Force;throw 'Measurement deadline'};if(Test-Path $sample){$report.metrics=Get-Content -LiteralPath $sample -Raw|ConvertFrom-Json};$report.clientExit=$client.ExitCode;if($client.ExitCode -ne 0 -or -not $report.metrics.success){throw 'Load not confirmed'}
 }
 $report.success=$true
}catch{$report.errorType=$_.Exception.GetType().Name;$report.errorCode=$_.FullyQualifiedErrorId}
finally{
 if($client -and -not $client.HasExited){Stop-Process -Id $client.Id -Force}
 try{if(Get-Service -Name $service -ErrorAction SilentlyContinue){& sc.exe stop $service|Out-Null;& sc.exe delete $service|Out-Null;for($i=0;$i -lt 20;$i++){if(-not (Get-Service -Name $service -ErrorAction SilentlyContinue)){break};Start-Sleep -Milliseconds 500}}}catch{}
 Remove-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue
 if(-not (Get-Service -Name $service -ErrorAction SilentlyContinue)){Remove-Item -LiteralPath $config -Force -ErrorAction SilentlyContinue}
 if($stderr){Remove-Item -LiteralPath $stderr,$stdout -Force -ErrorAction SilentlyContinue}
 $report.cleanup=(-not (Get-Service -Name $service -ErrorAction SilentlyContinue) -and -not (Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue) -and -not (Test-Path -LiteralPath $config))
 if(-not $report.cleanup){$report.success=$false};if($report.cleanup -and $guard -and -not $guard.HasExited){Stop-Process -Id $guard.Id -Force}
 $report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $out -Encoding UTF8
}
