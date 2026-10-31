param([Parameter(Mandatory)][ValidateSet('direct','vpn')][string]$Mode,[Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$Run,[switch]$TcpOnly)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong guest'}
$root='C:\CitaLab\VoiceReliable';$rule='CitaVoiceReliable-'+$Run;$state=Join-Path $root ('state-'+$Run+'.json');$report=@{success=$false;mode=$Mode;run=$Run};$operation='';$guard=$null;$client=$null
function Rpc([string]$command,[string]$id=''){
 $p=[IO.Pipes.NamedPipeClientStream]::new('.','RoutePilot.Rescue.v1',[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous,[Security.Principal.TokenImpersonationLevel]::Identification)
 try{$p.Connect(3000);$b=[Text.Encoding]::UTF8.GetBytes((@{Command=$command;RunId=$id}|ConvertTo-Json -Compress)+"`n");$p.Write($b,0,$b.Length);$p.Flush();$reader=[IO.StreamReader]::new($p);$task=$reader.ReadLineAsync();if(-not $task.Wait(5000)){throw 'RPC timeout'};$r=$task.Result|ConvertFrom-Json;if(-not $r.Success){throw 'RPC refused'};return $r}finally{$p.Dispose()}
}
function Wait-Phase([string]$wanted){for($i=0;$i -lt 150;$i++){ $s=(Rpc 'native-status').Native;if($s.phase -eq $wanted){return $s};if($s.phase -in @('RecoveryFailed','Unavailable')){$report.nativeErrorCode=[string]$s.errorCode;$report.nativePhase=[string]$s.phase;$report.nativeValidation=$s.runtime.validation;throw 'Native failed'};Start-Sleep -Milliseconds 400 };throw 'Native deadline'}
try{
 if((Rpc 'native-status').Native.phase -ne 'Disconnected'){throw 'Native route must be idle'}
 $fields=[IO.File]::ReadAllText((Join-Path $root 'metadata.txt')).Trim().Split('|');$ip=$null;if($fields.Count -ne 2 -or -not [Net.IPAddress]::TryParse($fields[0],[ref]$ip) -or $ip.AddressFamily -ne 'InterNetwork' -or $fields[1] -notmatch '^[a-f0-9]{64}$'){throw 'Invalid fixture metadata'}
 $rescue=@'
Start-Sleep -Seconds 240
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){exit 1}
$p='__STATE__';if(Test-Path -LiteralPath $p){try{$v=Get-Content -LiteralPath $p -Raw|ConvertFrom-Json;if($v.operation -match '^[a-f0-9]{32}$'){$pipe=[IO.Pipes.NamedPipeClientStream]::new('.','RoutePilot.Rescue.v1',[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous,[Security.Principal.TokenImpersonationLevel]::Identification);try{$pipe.Connect(3000);$b=[Text.Encoding]::UTF8.GetBytes((@{Command='native-stop';RunId=$v.operation}|ConvertTo-Json -Compress)+"`n");$pipe.Write($b,0,$b.Length);$pipe.Flush()}finally{$pipe.Dispose()}}}catch{}}
Get-NetFirewallRule -Name '__RULE__' -ErrorAction SilentlyContinue|Remove-NetFirewallRule
'@
 $rescue=$rescue.Replace('__STATE__',$state).Replace('__RULE__',$rule);$guard=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @('-NoProfile','-NonInteractive','-EncodedCommand',[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($rescue))) -PassThru
 if($Mode -eq 'vpn'){$reply=Rpc 'native-start';$operation=[string]$reply.Native.operation;if($operation -notmatch '^[a-f0-9]{32}$'){throw 'Bad operation'};@{operation=$operation}|ConvertTo-Json -Compress|Set-Content -LiteralPath $state -Encoding UTF8;$null=Wait-Phase 'Connected'}
 $actual=Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 12;$expected=[IO.File]::ReadAllText((Join-Path 'C:\CitaLab' ('voice-'+$Mode+'-ip.txt'))).Trim();if([string]$actual -ne $expected){throw 'Egress changed'};$report.egressMatched=$true
 $report.routeInterface=(Find-NetRoute -RemoteIPAddress $ip.ToString()|Where-Object {$_.InterfaceAlias}|Select-Object -First 1 -ExpandProperty InterfaceAlias)
 $image=Join-Path $root 'voice-quality-lab.exe';New-NetFirewallRule -Name $rule -DisplayName $rule -Program $image -Direction Inbound -Protocol UDP -RemoteAddress $ip.ToString() -Action Allow -Profile Any|Out-Null
 $out=Join-Path $root ('sample-'+$Run+'.json');$arguments=@(('"'+(Join-Path $root 'metadata.txt')+'"'),('"'+$out+'"'));if($TcpOnly){$arguments+='--tcp-only'};$client=Start-Process -FilePath $image -ArgumentList $arguments -WindowStyle Hidden -PassThru
 $waitMs=if($TcpOnly){75000}else{200000};if(-not $client.WaitForExit($waitMs)){Stop-Process -Id $client.Id -Force;throw 'Measurement deadline'};if(Test-Path -LiteralPath $out){$report.metrics=Get-Content -LiteralPath $out -Raw|ConvertFrom-Json};$report.clientExit=$client.ExitCode
 if($client.ExitCode -ne 0 -or -not $report.metrics.success){throw 'Insufficient measured load or incomplete test'}
 if($Mode -eq 'vpn'){$report.connectedAfter=((Rpc 'native-status').Native.phase -eq 'Connected');if(-not $report.connectedAfter){throw 'VPN not retained'}}
 $report.success=$true
}catch{$report.errorType=$_.Exception.GetType().Name;$report.errorCode=$_.FullyQualifiedErrorId}
finally{
 if($client -and -not $client.HasExited){Stop-Process -Id $client.Id -Force}
 $report.firewallRemoved=$false;try{Remove-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue;$report.firewallRemoved=-not [bool](Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue)}catch{}
 if($operation){try{$null=Rpc 'native-stop' $operation;$null=Wait-Phase 'Disconnected';$report.stopConfirmed=$true}catch{$report.stopConfirmed=$false;$report.success=$false}}else{$report.stopConfirmed=$true}
 if(-not $report.firewallRemoved){$report.success=$false};if($guard -and -not $guard.HasExited -and $report.stopConfirmed -and $report.firewallRemoved){Stop-Process -Id $guard.Id -Force};if($report.stopConfirmed -and (Test-Path -LiteralPath $state)){Remove-Item -LiteralPath $state -Force}
 $report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $root ('result-'+$Run+'.json')) -Encoding UTF8
}
