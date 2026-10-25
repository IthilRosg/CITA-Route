param([switch]$CrashController,[switch]$ResetLink)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$root='C:\CitaLab\NativeAwg';$image=Join-Path $root 'native-guard-lab.exe';$run=[guid]::NewGuid().ToString('N');$address='fdc1:ca7a:100::55';$group='CitaGuardV6-'+$run
$report=@{schema=1;success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='VM-only persistent IPv4/IPv6 prefix guard; no production selection'};$arm=$null;$rescue=$null;$listener=$null;$udpListener=$null;$added=$false;$clean=$false
function Invoke-Guard([string]$action){
 $info=[Diagnostics.ProcessStartInfo]::new($image);$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $child=[Diagnostics.Process]::Start($info)
 try{$child.StandardInput.WriteLine((@{Action=$action;Run=$run}|ConvertTo-Json -Compress));$child.StandardInput.Close();$output=$child.StandardOutput.ReadToEndAsync();$errors=$child.StandardError.ReadToEndAsync();if(-not $child.WaitForExit(15000)){$child.Kill();$child.WaitForExit();throw 'Guard helper deadline'};$r=$output.GetAwaiter().GetResult()|ConvertFrom-Json;if(-not $r.success){$report.guardFailureStage=$r.stage;throw 'Guard command refused'};return $r}finally{$child.Dispose()}
}
function Connect-Test([string]$server,[int]$port){$family=if($server.Contains(':')){[Net.Sockets.AddressFamily]::InterNetworkV6}else{[Net.Sockets.AddressFamily]::InterNetwork};$client=[Net.Sockets.TcpClient]::new($family);try{$task=$client.ConnectAsync([Net.IPAddress]::Parse($server),$port);if(-not $task.Wait(1800)){return $false};return [bool]$client.Connected}catch{return $false}finally{$client.Dispose()}}
function Datagram-Test{
 $client=[Net.Sockets.UdpClient]::new([Net.Sockets.AddressFamily]::InterNetworkV6)
 try{$nonce=[guid]::NewGuid().ToByteArray();$null=$client.Send($nonce,$nonce.Length,$udpListener.Client.LocalEndPoint);$remote=[Net.IPEndPoint]::new([Net.IPAddress]::IPv6Any,0);$received=$udpListener.Receive([ref]$remote);return [bool]([Convert]::ToBase64String($received) -eq [Convert]::ToBase64String($nonce) -and $remote.Port -eq $client.Client.LocalEndPoint.Port)}catch{return $false}finally{$client.Dispose()}
}
try{
 $report.imageSha256=(Get-FileHash -LiteralPath $image).Hash
 $physical=@(Get-NetIPConfiguration|Where-Object {$_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up'});if($physical.Count -ne 1){throw 'One guest uplink required'};$index=$physical[0].InterfaceIndex
 if(Get-NetIPAddress -IPAddress $address -ErrorAction SilentlyContinue){throw 'IPv6 fixture address already exists'}
 # Arm a separate, hidden rollback process BEFORE any persistent WFP object.
 $code="Start-Sleep -Seconds 120;if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){exit 1};@{Action='Cleanup';Run='$run'}|ConvertTo-Json -Compress|& '$image';Get-NetIPAddress -InterfaceIndex $index -IPAddress '$address' -ErrorAction SilentlyContinue|Remove-NetIPAddress -Confirm:`$false;Get-NetFirewallRule -Group '$group' -ErrorAction SilentlyContinue|Remove-NetFirewallRule"
 if($ResetLink){$guid=$physical[0].NetAdapter.InterfaceGuid.ToString();$code+=";Get-NetAdapter -IncludeHidden|Where-Object {`$_.InterfaceGuid.ToString() -eq '$guid'}|Enable-NetAdapter -Confirm:`$false"}
 if($CrashController){$code=$code.Replace('Start-Sleep -Seconds 120','Start-Sleep -Seconds 30')}
 $code+=";`$left=@{Action='Inspect';Run='$run'}|ConvertTo-Json -Compress|& '$image'|ConvertFrom-Json;@{schema=1;run='$run';rules=`$left.rules;success=[bool](`$left.success -and `$left.rules -eq 0 -and -not (Get-NetIPAddress -IPAddress '$address' -ErrorAction SilentlyContinue) -and -not (Get-NetFirewallRule -Group '$group' -ErrorAction SilentlyContinue));checkedUtc=[datetime]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content '$root\native-wfp-rescue-$run.json' -Encoding UTF8"
 $rescue=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList ('-NoProfile -NonInteractive -EncodedCommand '+[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))) -PassThru
 New-NetIPAddress -InterfaceIndex $index -IPAddress $address -PrefixLength 64 -SkipAsSource $true|Out-Null;$added=$true
 for($i=0;$i -lt 30;$i++){if((Get-NetIPAddress -InterfaceIndex $index -IPAddress $address).AddressState -eq 'Preferred'){break};Start-Sleep -Milliseconds 100}
 New-NetFirewallRule -DisplayName $group -Group $group -Direction Inbound -Action Allow -Protocol TCP -LocalAddress $address -RemoteAddress $address -Profile Any -Enabled True|Out-Null
 New-NetFirewallRule -DisplayName ($group+'-UDP') -Group $group -Direction Inbound -Action Allow -Protocol UDP -LocalAddress $address -RemoteAddress $address -Profile Any -Enabled True|Out-Null
 $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Parse($address),0);$listener.Start();$port=$listener.LocalEndpoint.Port
 $udpListener=[Net.Sockets.UdpClient]::new([Net.Sockets.AddressFamily]::InterNetworkV6);$udpListener.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Parse($address),0));$udpListener.Client.ReceiveTimeout=1200
 $report.baselineV4=Connect-Test '1.1.1.1' 443;$report.baselineV6=Connect-Test $address $port;$report.directBaseline=Connect-Test '1.0.0.1' 443
 $report.baselineUdpV6=Datagram-Test
 if(-not $report.baselineV4 -or -not $report.baselineV6 -or -not $report.directBaseline -or -not $report.baselineUdpV6){throw 'Baseline unavailable; block result would be ambiguous'}
 $info=[Diagnostics.ProcessStartInfo]::new($image);$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $arm=[Diagnostics.Process]::Start($info);$arm.StandardInput.WriteLine((@{Action='Arm';Run=$run;Prefixes=@('1.1.1.0/24','fdc1:ca7a:100::/64')}|ConvertTo-Json -Compress))
 $line=$arm.StandardOutput.ReadLineAsync();if(-not $line.Wait(15000)){throw 'Guard readiness deadline'};$ready=$line.GetAwaiter().GetResult()|ConvertFrom-Json
 if(-not $ready.success -or $ready.stage -ne 'Armed' -or $ready.rules -ne 2){$report.guardFailureStage=$ready.stage;throw 'Guard did not arm'}
 $report.blockedV4=-not (Connect-Test '1.1.1.1' 443);$report.blockedV6=-not (Connect-Test $address $port)
 $report.blockedUdpV6=-not (Datagram-Test)
 if($CrashController){$report.fixtureRun=$run;$report.intentionallyInterrupted=$true;$report|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root 'native-wfp-result.json') -Encoding UTF8;[Environment]::Exit(73)}
 $arm.Kill();$arm.WaitForExit();$report.afterKill=Invoke-Guard 'Inspect'
 if($ResetLink){
  try{Disable-NetAdapter -InputObject $physical[0].NetAdapter -Confirm:$false;Start-Sleep -Milliseconds 400}finally{Enable-NetAdapter -Name $physical[0].NetAdapter.Name -Confirm:$false}
  for($i=0;$i -lt 50;$i++){if((Get-NetIPAddress -InterfaceIndex $index -IPAddress $address).AddressState -eq 'Preferred' -and (Connect-Test '1.0.0.1' 443)){break};Start-Sleep -Milliseconds 200}
  $report.linkReset=$true;$report.afterLinkReset=Invoke-Guard 'Inspect'
 }
 $report.blockedAfterKillV4=-not (Connect-Test '1.1.1.1' 443);$report.blockedAfterKillV6=-not (Connect-Test $address $port);$report.directRetained=Connect-Test '1.0.0.1' 443
 $report.blockedAfterKillUdpV6=-not (Datagram-Test)
 $null=Invoke-Guard 'Cleanup';$clean=$true;$report.remaining=(Invoke-Guard 'Inspect').rules
 $report.restoredV4=Connect-Test '1.1.1.1' 443;$report.restoredV6=Connect-Test $address $port
 $report.restoredUdpV6=Datagram-Test
 $report.success=[bool]($report.blockedV4 -and $report.blockedV6 -and $report.blockedUdpV6 -and $report.blockedAfterKillV4 -and $report.blockedAfterKillV6 -and $report.blockedAfterKillUdpV6 -and $report.afterKill.rules -eq 2 -and $report.directRetained -and $report.restoredV4 -and $report.restoredV6 -and $report.restoredUdpV6 -and $report.remaining -eq 0)
}catch{$report.errorType=$_.Exception.GetType().Name}
finally{
 if($ResetLink -and $physical){$physical[0].NetAdapter|Enable-NetAdapter -Confirm:$false}
 if($arm){if(-not $arm.HasExited){$arm.Kill();$arm.WaitForExit()};$arm.Dispose()}
 if(-not $clean){try{$null=Invoke-Guard 'Cleanup';$clean=$true}catch{$report.cleanupFailed=$true}}
 if($listener){$listener.Stop()}
 if($udpListener){$udpListener.Dispose()}
 if($added){Get-NetIPAddress -InterfaceIndex $index -IPAddress $address -ErrorAction SilentlyContinue|Remove-NetIPAddress -Confirm:$false}
 Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue|Remove-NetFirewallRule
 $report.cleaned=[bool]($clean -and -not (Get-NetIPAddress -IPAddress $address -ErrorAction SilentlyContinue) -and -not (Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue))
 if($report.cleaned -and $rescue){if(-not $rescue.HasExited){Stop-Process -Id $rescue.Id -Force};$rescue.Dispose()}
 if(-not $report.cleaned){$report.success=$false}
 $report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root 'native-wfp-result.json') -Encoding UTF8
}
if(-not $report.success){exit 1}
