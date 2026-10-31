param([switch]$RacesOnly)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$root='C:\CitaLab\NativeAwg';$report=@{schema=1;success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='Backend profile retirement/publication fencing; no live core/TUN rebinding'};$process=$null
function Topology{
 $text=@(Get-NetRoute -AddressFamily IPv4|ForEach-Object {'route|'+$_.InterfaceIndex+'|'+$_.DestinationPrefix+'|'+$_.NextHop+'|'+$_.RouteMetric})+@(Get-NetAdapter -IncludeHidden|ForEach-Object {'nic|'+$_.InterfaceGuid+'|'+$_.Name+'|'+$_.Status})
 $sha=[Security.Cryptography.SHA256]::Create();try{return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes((($text|Sort-Object)-join "`n"))))).Replace('-','')}finally{$sha.Dispose()}
}
try{
 if(Get-Process native-awg-lab,native-core-lab,Cita.NativeAgentLab,smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'Guest must be idle'}
 $wireSockCli='C:\Program Files\WireSock Secure Connect\command-line\wiresock-connect-cli.exe'
 if(Test-Path -LiteralPath $wireSockCli){
  . C:\CitaLab\PlatformNext\WireSockCli.ps1
  if((Get-WireSockState (Read-HelperOutput $wireSockCli 'status') 'SmartRouting-AWG2') -ne 'Disconnected'){throw 'Guest WireSock must be disconnected'}
 }
 $before=Topology;$report.nativeSha256=(Get-FileHash (Join-Path $root 'native-awg-lab.exe')).Hash;$report.agentBackendSha256=(Get-FileHash (Join-Path $root 'AgentBackend\Cita.NativeAgentLab.dll')).Hash
 $mode=if($RacesOnly){'--profiles-races-lab'}else{'--profiles-lab'}
 $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $root 'AgentBackend\Cita.NativeAgentLab.exe'),($mode+' C:\CitaLab\NativeAwg'));$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $process=[Diagnostics.Process]::Start($info);$output=$process.StandardOutput.ReadToEndAsync();$errors=$process.StandardError.ReadToEndAsync()
 if(-not $process.WaitForExit(125000)){$process.Kill();$process.WaitForExit();throw 'Profile lab deadline'}
 $report.probes=$output.GetAwaiter().GetResult()|ConvertFrom-Json;$expectedCases=if($RacesOnly){2}else{3};$report.success=[bool]($process.ExitCode -eq 0 -and $report.probes.success -and $report.probes.cases.Count -eq $expectedCases)
}catch{$report.errorType=$_.Exception.GetType().Name}
finally{
 if($process){if(-not $process.HasExited){$process.Kill();$process.WaitForExit()};$process.Dispose()}
 for($i=0;$i -lt 60;$i++){if(-not (Get-Process native-awg-lab,Cita.NativeAgentLab -ErrorAction SilentlyContinue)){break};Start-Sleep -Milliseconds 100}
 $report.processesStopped=[bool](-not (Get-Process native-awg-lab,Cita.NativeAgentLab -ErrorAction SilentlyContinue));$report.topologyUnchanged=[bool]($before -and $before -eq (Topology));if(-not $report.processesStopped -or -not $report.topologyUnchanged){$report.success=$false}
 $report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root 'profiles-result.json') -Encoding UTF8
}
if(-not $report.success){exit 1}
