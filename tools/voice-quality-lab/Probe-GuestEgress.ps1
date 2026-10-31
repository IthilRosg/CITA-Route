$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong guest'}
$report=@{success=$false;stopConfirmed=$false};$operation='';$watchdog=$null
function Rpc([string]$command,[string]$id=''){
 $p=[IO.Pipes.NamedPipeClientStream]::new('.','RoutePilot.Rescue.v1',[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous,[Security.Principal.TokenImpersonationLevel]::Identification)
 try{$p.Connect(3000);$b=[Text.Encoding]::UTF8.GetBytes((@{Command=$command;RunId=$id}|ConvertTo-Json -Compress)+"`n");$p.Write($b,0,$b.Length);$p.Flush();$reader=[IO.StreamReader]::new($p);$r=$reader.ReadLineAsync();if(-not $r.Wait(5000)){throw 'RPC timeout'};$v=$r.Result|ConvertFrom-Json;if(-not $v.Success){throw 'RPC refused'};return $v}finally{$p.Dispose()}
}
function Wait-Phase([string]$desired){for($i=0;$i -lt 150;$i++){ $s=(Rpc 'native-status').Native;if($s.phase -eq $desired){return $s};if($s.phase -in @('RecoveryFailed','Unavailable')){throw 'Native failure'};Start-Sleep -Milliseconds 400 };throw 'Phase timeout'}
try{
 if((Rpc 'native-status').Native.phase -ne 'Disconnected'){throw 'Existing route not idle'}
 $reply=Rpc 'native-start';$operation=[string]$reply.Native.operation;if($operation -notmatch '^[a-f0-9]{32}$'){throw 'Unexpected operation identity'}
 $guard=@'
Start-Sleep -Seconds 150
$p=[IO.Pipes.NamedPipeClientStream]::new('.','RoutePilot.Rescue.v1',[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous,[Security.Principal.TokenImpersonationLevel]::Identification)
try{$p.Connect(3000);$b=[Text.Encoding]::UTF8.GetBytes((@{Command='native-stop';RunId='__OP__'}|ConvertTo-Json -Compress)+"`n");$p.Write($b,0,$b.Length);$p.Flush()}catch{}finally{$p.Dispose()}
'@.Replace('__OP__',$operation)
 $watchdog=Start-Process powershell.exe -ArgumentList @('-NoProfile','-NonInteractive','-EncodedCommand',[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($guard))) -WindowStyle Hidden -PassThru
 $null=Wait-Phase 'Connected'
 $r=Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 12;$ip=$null;if(-not [Net.IPAddress]::TryParse([string]$r,[ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork){throw 'Egress verification unavailable'}
 [IO.File]::WriteAllText('C:\CitaLab\voice-vpn-ip.txt',$ip.ToString());$direct=[IO.File]::ReadAllText('C:\CitaLab\voice-direct-ip.txt').Trim();$report.differentExit=($direct -ne $ip.ToString());$report.success=$true
}catch{$report.errorType=$_.Exception.GetType().Name}
finally{if($operation){try{$null=Rpc 'native-stop' $operation;$null=Wait-Phase 'Disconnected';$report.stopConfirmed=$true;if($watchdog -and -not $watchdog.HasExited){Stop-Process -Id $watchdog.Id -Force}}catch{$report.success=$false;$report.stopError=$_.Exception.GetType().Name}};$report|ConvertTo-Json -Compress|Set-Content C:\CitaLab\voice-egress-probe.json -Encoding UTF8}
