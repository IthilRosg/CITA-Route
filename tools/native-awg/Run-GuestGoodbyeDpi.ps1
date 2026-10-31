param([Parameter(Mandatory)][string]$Run,[switch]$Media)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83' -or $Run -notmatch '^[a-f0-9]{32}$'){throw 'Wrong isolated guest'}
$base='C:\CitaLab\GoodbyeDpi';$image=Join-Path $base 'goodbyedpi.exe';$fixture=Join-Path $base 'dpi-compare-lab.exe';$rule='CitaDpiCompare-'+$Run;$report=@{success=$false;run=$Run;cases=@();scope='VM-only anonymous HTTPS HEAD and native-equivalent GET; guest CITA native processes idle; host VPN state not controlled';cleanupConfirmed=$false};$owned=$null;$rescue=$null
function Cleanup-Owned {
 if($owned -and -not $owned.HasExited){if($owned.Path -ne $image){throw 'Unexpected process identity'};Stop-Process -Id $owned.Id -Force;[void]$owned.WaitForExit(5000)}
 foreach($driver in @(Get-CimInstance Win32_SystemDriver | Where-Object {$_.Name -match '^WinDivert'})){
  if($driver.PathName -notlike ('*'+$base+'*')){throw 'Refusing unrelated driver'}
  if($driver.State -eq 'Running'){& sc.exe stop $driver.Name | Out-Null}
  & sc.exe delete $driver.Name | Out-Null
 }
 if(Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue){Remove-NetFirewallRule -Name $rule}
}
function Probe([string]$hostName,[string]$address){
 $info=[Diagnostics.ProcessStartInfo]::new('C:\Windows\System32\curl.exe');$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 if($hostName -notin @('www.youtube.com','www.wikipedia.org','telegram.org')){throw 'Unexpected probe host'}
 $ip=$null;if(-not [Net.IPAddress]::TryParse($address,[ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork){throw 'Unexpected DNS address'}
 $info.Arguments='--noproxy * --http1.1 --head --silent --show-error --max-time 6 --output NUL --write-out "%{http_code} %{time_total}" --resolve '+$hostName+':443:'+$ip.ToString()+' https://'+$hostName+'/'
 $p=[Diagnostics.Process]::Start($info);try{$out=$p.StandardOutput.ReadToEndAsync();$err=$p.StandardError.ReadToEndAsync();if(-not $p.WaitForExit(9000)){$p.Kill();throw 'Probe deadline'};$value=$out.GetAwaiter().GetResult();$null=$err.GetAwaiter().GetResult();$parts=$value -split ' ';$curl=@{domain=$hostName;exitCode=$p.ExitCode;httpStatus=if($parts[0] -match '^\d{3}$'){[int]$parts[0]}else{0};seconds=if($parts.Count -gt 1){$parts[1]}else{''}}}finally{$p.Dispose()}
 if($hostName -eq 'www.youtube.com'){
  $get=[Diagnostics.ProcessStartInfo]::new('C:\Windows\System32\curl.exe');$get.UseShellExecute=$false;$get.CreateNoWindow=$true;$get.RedirectStandardOutput=$true;$get.RedirectStandardError=$true
  $get.Arguments='--noproxy * --http1.1 --silent --show-error --max-time 6 --output NUL --write-out "%{http_code} %{size_header}" --user-agent CITA-Route-Connectivity/1 --resolve '+$hostName+':443:'+$ip.ToString()+' https://'+$hostName+'/'
  $p=[Diagnostics.Process]::Start($get);try{$out=$p.StandardOutput.ReadToEndAsync();$err=$p.StandardError.ReadToEndAsync();if(-not $p.WaitForExit(9000)){$p.Kill();throw 'GET probe deadline'};$value=$out.GetAwaiter().GetResult();$null=$err.GetAwaiter().GetResult();$parts=$value -split ' ';$curl.curlGet=@{exitCode=$p.ExitCode;httpStatus=if($parts[0] -match '^\d{3}$'){[int]$parts[0]}else{0};headerBytes=if($parts.Count -gt 1 -and $parts[1] -match '^\d+$'){[int]$parts[1]}else{0}}}finally{$p.Dispose()}
 }
 $native=[Diagnostics.ProcessStartInfo]::new($fixture);$native.UseShellExecute=$false;$native.CreateNoWindow=$true;$native.RedirectStandardOutput=$true;$native.RedirectStandardError=$true;$native.Arguments=$hostName+' '+$ip.ToString()
 $p=[Diagnostics.Process]::Start($native);try{$out=$p.StandardOutput.ReadToEndAsync();$err=$p.StandardError.ReadToEndAsync();if(-not $p.WaitForExit(6000)){$p.Kill();throw 'Native-equivalent probe deadline'};$text=$out.GetAwaiter().GetResult();$null=$err.GetAwaiter().GetResult();if($p.ExitCode -ne 0){throw 'Native-equivalent probe failed'};$curl.nativeEquivalent=$text|ConvertFrom-Json}finally{$p.Dispose()}
 if($hostName -eq 'www.youtube.com'){
  $curl.tlsVariants=@();$variants=if($mode -eq 'direct'){@('alpn','tls12','both','accept','keepalive','headers','larger')}else{@('larger')};foreach($variant in $variants){
   $native.Arguments=$hostName+' '+$ip.ToString()+' '+$variant;$p=[Diagnostics.Process]::Start($native)
   try{$out=$p.StandardOutput.ReadToEndAsync();$err=$p.StandardError.ReadToEndAsync();if(-not $p.WaitForExit(6000)){$p.Kill();throw 'TLS variant deadline'};$text=$out.GetAwaiter().GetResult();$null=$err.GetAwaiter().GetResult();if($p.ExitCode -ne 0){throw 'TLS variant failed'};$curl.tlsVariants+=,($text|ConvertFrom-Json)}finally{$p.Dispose()}
  }
 }
 return $curl
}
try{
 if(@(Get-CimInstance Win32_SystemDriver|Where-Object {$_.Name -match '^WinDivert'}).Count -or (Get-Process goodbyedpi,winws,winws2,native-core,native-owner -ErrorAction SilentlyContinue)){throw 'Competing process or driver present'}
 $signature=Get-AuthenticodeSignature -LiteralPath (Join-Path $base 'WinDivert64.sys');if($signature.Status -ne 'Valid'){throw 'Driver signature invalid'}
 $report.helperSha256=(Get-FileHash -LiteralPath $fixture).Hash;$report.executableSha256=(Get-FileHash -LiteralPath $image).Hash;$report.driverSha256=(Get-FileHash -LiteralPath (Join-Path $base 'WinDivert64.sys')).Hash
 if($Media){$report.controllerSha256=(Get-FileHash -LiteralPath (Join-Path $base 'AgentBackend\Cita.NativeAgentLab.dll')).Hash;$report.scope='VM-only anonymous YouTube playback and Discord page; no Discord voice; guest CITA idle; host VPN state not controlled'}
 $addresses=@{};foreach($hostName in @('www.youtube.com','www.wikipedia.org','telegram.org')){
  $address=@([Net.Dns]::GetHostAddresses($hostName)|Where-Object {$_.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork}|Select-Object -First 1);if($address.Count -ne 1){throw 'DNS fixture unavailable'};$addresses[$hostName]=$address[0].ToString()
 }
 $rescueCode=@'
Start-Sleep -Seconds 150
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){exit 1}
$base='C:\CitaLab\GoodbyeDpi';$image=Join-Path $base 'goodbyedpi.exe'
Get-Process goodbyedpi -ErrorAction SilentlyContinue|Where-Object {$_.Path -eq $image}|Stop-Process -Force
foreach($driver in @(Get-CimInstance Win32_SystemDriver|Where-Object {$_.Name -match '^WinDivert' -and $_.PathName -like ('*'+$base+'*')})){if($driver.State -eq 'Running'){& sc.exe stop $driver.Name|Out-Null};& sc.exe delete $driver.Name|Out-Null}
Get-NetFirewallRule -Name '__RULE__' -ErrorAction SilentlyContinue|Remove-NetFirewallRule
'@
 $rescueCode=$rescueCode.Replace('__RULE__',$rule)
 $rescue=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @('-NoProfile','-NonInteractive','-EncodedCommand',[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($rescueCode))) -PassThru
 New-NetFirewallRule -Name $rule -DisplayName $rule -Direction Inbound -Action Block -Program $fixture -RemoteAddress @('0.0.0.0-126.255.255.255','128.0.0.0-255.255.255.255','::','::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff') -Profile Any|Out-Null
 foreach($mode in @('direct','-6','-9')){
  if($mode -ne 'direct'){$owned=Start-Process -FilePath $image -ArgumentList @($mode) -WorkingDirectory $base -WindowStyle Hidden -PassThru;Start-Sleep -Seconds 2;if($owned.HasExited){throw ('GoodbyeDPI failed to start '+$mode)};if(-not @(Get-CimInstance Win32_SystemDriver|Where-Object {$_.Name -match '^WinDivert' -and $_.State -eq 'Running'}).Count){throw 'WinDivert was not active'}}
  if($Media){
   $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $base 'AgentBackend\Cita.NativeAgentLab.exe'));$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;$info.Arguments='--media-browser-standalone "'+$base+'"'
   $p=[Diagnostics.Process]::Start($info);try{$out=$p.StandardOutput.ReadToEndAsync();$err=$p.StandardError.ReadToEndAsync();if(-not $p.WaitForExit(45000)){$p.Kill();throw 'Browser comparison deadline'};$value=$out.GetAwaiter().GetResult();$stderr=$err.GetAwaiter().GetResult();if($p.ExitCode -ne 0){$report.browserErrorType=if($stderr -match 'Unhandled exception\. ([A-Za-z0-9.]+)'){$Matches[1]}else{'unclassified'};throw 'Browser comparison failed'};$mediaResult=$value|ConvertFrom-Json}finally{$p.Dispose()}
   if($owned -and ($owned.HasExited -or -not @(Get-CimInstance Win32_SystemDriver|Where-Object {$_.Name -match '^WinDivert' -and $_.State -eq 'Running'}).Count)){throw 'GoodbyeDPI stopped during browser comparison'}
   $report.cases+=@{mode=$mode;media=$mediaResult}
  }else{$results=@();foreach($hostName in @('www.youtube.com','www.wikipedia.org','telegram.org')){$results+=,(Probe $hostName $addresses[$hostName])};$report.cases+=@{mode=$mode;results=$results}}
  if($owned){
   if(-not $owned.HasExited){Stop-Process -Id $owned.Id -Force;[void]$owned.WaitForExit(5000)};$owned.Dispose();$owned=$null
   foreach($driver in @(Get-CimInstance Win32_SystemDriver|Where-Object {$_.Name -match '^WinDivert'})){if($driver.PathName -notlike ('*'+$base+'*')){throw 'Refusing unrelated driver'};if($driver.State -eq 'Running'){& sc.exe stop $driver.Name|Out-Null};& sc.exe delete $driver.Name|Out-Null}
   Start-Sleep -Milliseconds 500
  }
 }
 $report.success=$true
}catch{$report.errorType=$_.Exception.GetType().Name;$report.errorId=$_.FullyQualifiedErrorId;$report.errorLine=$_.InvocationInfo.ScriptLineNumber;$report.failedStage=if($mode){$mode}else{'preflight'}}
finally{
 try{Cleanup-Owned;if(@(Get-CimInstance Win32_SystemDriver|Where-Object {$_.Name -match '^WinDivert'}).Count -or (Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue)){throw 'Driver or fixture rule cleanup not confirmed'};$report.cleanupConfirmed=$true;if($rescue -and -not $rescue.HasExited){Stop-Process -Id $rescue.Id -Force}}catch{$report.success=$false;$report.cleanupError=$_.Exception.GetType().Name}
 $report|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $base 'result.json') -Encoding UTF8
}
