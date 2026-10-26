param([Parameter(Mandatory)][string]$Run,[switch]$Policy,[switch]$Automatic,[switch]$Performance,[switch]$DnsOutage,[switch]$DnsFallback,[switch]$Pages,[switch]$Coverage)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83' -or $Run -notmatch '^[a-f0-9]{32}$'){throw 'Wrong isolated test identity'}
$base='C:\CitaLab\NativeAwg';$folder=Join-Path $base ('DirectGuard-'+$Run);$rule='CitaDirectGuard-'+$Run;$rescue=$null;$report=@{success=$false};$created=$false
if(Get-Process native-core,native-owner,native-core-lab,native-awg-lab,smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'Guest native backend must be idle'}
try {
 New-Item -ItemType Directory -Path $folder -ErrorAction Stop|Out-Null;$created=$true
 foreach($pair in @(@('native-core-lab.exe','native-core.exe'),@('native-awg-lab.exe','native-owner.exe'),@('native-guard-lab.exe','native-guard.exe'))){Copy-Item -LiteralPath (Join-Path $base $pair[0]) -Destination (Join-Path $folder $pair[1])}
 $remote=@('0.0.0.0-126.255.255.255','128.0.0.0-255.255.255.255','::','::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff')
 New-NetFirewallRule -Name $rule -DisplayName $rule -Direction Inbound -Action Block -Program (Join-Path $folder 'native-core.exe') -RemoteAddress $remote -Profile Any|Out-Null
 $rescuePath=Join-Path $folder 'rescue.ps1'
 $code=@'
param([switch]$Delayed)
$ErrorActionPreference='Stop';if($Delayed){Start-Sleep -Seconds 130}
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){exit 1}
$folder='__FOLDER__';$run='__RUN__'
if(Test-Path (Join-Path $folder 'core-pid.json')){$id=Get-Content (Join-Path $folder 'core-pid.json') -Raw|ConvertFrom-Json;$p=Get-Process -Id $id.pid -ErrorAction SilentlyContinue;if($p -and $p.Path -eq (Join-Path $folder 'native-core.exe')){Stop-Process -Id $p.Id -Force}}
$info=[Diagnostics.ProcessStartInfo]::new((Join-Path $folder 'native-guard.exe'));$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;$process=[Diagnostics.Process]::Start($info)
try{$process.StandardInput.WriteLine((@{Action='Cleanup';Run=$run}|ConvertTo-Json -Compress));$process.StandardInput.Close();$out=$process.StandardOutput.ReadToEndAsync();$err=$process.StandardError.ReadToEndAsync();if(-not $process.WaitForExit(15000)){$process.Kill();throw 'Direct guard rescue deadline'};$reply=$out.GetAwaiter().GetResult()|ConvertFrom-Json;if(-not $reply.success -or $reply.rules -ne 0){throw 'Direct guard rescue not confirmed'}}finally{$process.Dispose()}
Remove-NetFirewallRule -Name ('CitaDirectGuard-'+$run) -ErrorAction SilentlyContinue
'@
 $code=$code.Replace('__FOLDER__',$folder).Replace('__RUN__',$Run);[IO.File]::WriteAllText($rescuePath,$code,[Text.UTF8Encoding]::new($false))
 $rescue=Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$rescuePath,'-Delayed')
 $mode=if($DnsOutage){'--native-policy-dns-outage-lab'}elseif($DnsFallback){'--native-policy-dns-fallback-lab'}elseif($Pages){'--native-policy-pages-lab'}elseif($Coverage){'--native-policy-coverage-lab'}elseif($Performance){'--native-policy-performance-lab'}elseif($Automatic){'--native-policy-auto-lab'}elseif($Policy){'--native-policy-session-lab'}else{'--direct-guard-lab'}
 $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $base 'AgentBackend\Cita.NativeAgentLab.exe'),($mode+' '+$folder+' '+$Run));$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;$process=[Diagnostics.Process]::Start($info)
 try{$output=$process.StandardOutput.ReadToEndAsync();$errorOutput=$process.StandardError.ReadToEndAsync();if(-not $process.WaitForExit(115000)){$process.Kill();throw 'Direct guard controller deadline'}}finally{$process.Dispose()}
 $loaded=Get-Content (Join-Path $folder 'result.json') -Raw|ConvertFrom-Json;$report=@{};foreach($property in $loaded.PSObject.Properties){$report[$property.Name]=$property.Value}
 $report.ownerSha256=(Get-FileHash (Join-Path $folder 'native-owner.exe')).Hash;$report.guardSha256=(Get-FileHash (Join-Path $folder 'native-guard.exe')).Hash;$report.coreSha256=(Get-FileHash (Join-Path $folder 'native-core.exe')).Hash;$report.controllerSha256=(Get-FileHash (Join-Path $base 'AgentBackend\Cita.NativeAgentLab.dll')).Hash
}catch{$report.success=$false;$report.runnerError=$_.Exception.GetType().Name;$report.runnerLine=$_.InvocationInfo.ScriptLineNumber}
finally{
 if($created){try{& (Join-Path $folder 'rescue.ps1');$report.independentCleanupConfirmed=$true;if($rescue -and -not $rescue.HasExited){Stop-Process -Id $rescue.Id -Force};$absolute=[IO.Path]::GetFullPath($folder);if([IO.Path]::GetDirectoryName($absolute) -ne $base -or [IO.Path]::GetFileName($absolute) -ne ('DirectGuard-'+$Run)){throw 'Cleanup containment'};Remove-Item -LiteralPath $absolute -Recurse -Force}catch{$report.success=$false;$report.cleanupError=$_.Exception.GetType().Name}}
 $report|ConvertTo-Json -Depth 9|Set-Content (Join-Path $base 'native-direct-result.json') -Encoding UTF8
}
