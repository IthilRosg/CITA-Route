param([switch]$HandshakeOnly,[switch]$FullRouteLab)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'));$run=[guid]::NewGuid().ToString('N')
$report=@{schema=1;success=$false;handshakeOnly=[bool]$HandshakeOnly;explicitLabFullRoute=[bool]$FullRouteLab;scope='Existing WG profile candidate absent from known server public-key inventory; no host route changes';startedUtc=[datetime]::UtcNow.ToString('o')}
$nativeHash=(Get-FileHash (Join-Path $root 'runtime/native-awg-lab.exe')).Hash
. (Join-Path $root 'runtime/vm-lab/W11-Calls.ps1')
try{
 W11-Call @('start',$labVmx,'nogui')|Out-Null
 $profile=if($FullRouteLab){'private/vm-lab/independent-server-fullroute-lab.conf'}else{'private/vm-lab/independent-server-pinned.conf'}
 foreach($pair in @(@('runtime/native-awg-lab.exe','native-awg-lab.exe'),@($profile,'private\independent-server.conf'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
 $code=@'
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
if(Get-Process native-awg-lab,native-core-lab,Cita.NativeAgentLab,smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'Guest must be idle'}
$report=@{success=$false;nativeSha256=(Get-FileHash C:\CitaLab\NativeAwg\native-awg-lab.exe).Hash};$p=$null
try{
 $i=[Diagnostics.ProcessStartInfo]::new('C:\CitaLab\NativeAwg\native-awg-lab.exe','--probe');$i.UseShellExecute=$false;$i.CreateNoWindow=$true;$i.RedirectStandardInput=$true;$i.RedirectStandardOutput=$true;$i.RedirectStandardError=$true
 $p=[Diagnostics.Process]::Start($i);$a=$p.StandardOutput.ReadToEndAsync();$b=$p.StandardError.ReadToEndAsync();$p.StandardInput.Write([IO.File]::ReadAllText('C:\CitaLab\NativeAwg\private\independent-server.conf'));$p.StandardInput.Close()
 if(-not $p.WaitForExit(35000)){$p.Kill();throw 'Independent probe deadline'}
 $report.probe=$a.GetAwaiter().GetResult()|ConvertFrom-Json;$report.success=[bool]($p.ExitCode -eq 0 -and $report.probe.success)
}catch{$report.errorType=$_.Exception.GetType().Name}
finally{if($p){if(-not $p.HasExited){$p.Kill();$p.WaitForExit()};$p.Dispose()};$report.processesStopped=[bool](-not(Get-Process native-awg-lab -ErrorAction SilentlyContinue));$report|ConvertTo-Json -Depth 5|Set-Content C:\CitaLab\NativeAwg\independent-result.json -Encoding UTF8}
'@
 if($HandshakeOnly){$code=$code.Replace("'--probe'","'--handshake-only-lab'")}
 W11-Code $code|Out-Null
 $file=Join-Path $root ('runtime/vm-lab/w11/native-independent-'+$run+'.json');W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\independent-result.json',$file)|Out-Null
 $r=Get-Content $file -Raw|ConvertFrom-Json;$report.probe=$r.probe;$report.errorType=$r.errorType;$report.hashesMatched=[bool]($r.nativeSha256 -eq $nativeHash);$report.clean=[bool]$r.processesStopped;$report.success=[bool]($r.success -and $report.clean -and $report.hashesMatched)
 if($report.success -and -not $HandshakeOnly){
  [IO.File]::WriteAllText((Join-Path $root 'private/vm-lab/independent-server-egress.txt'),$r.probe.egressHash)
  $baseline=(Get-ChildItem (Join-Path $root 'runtime/native-awg-startup-*.json')|Sort-Object LastWriteTime -Descending|Select-Object -First 1|Get-Content -Raw|ConvertFrom-Json).observations[0].literal.egressHash
  $report.distinctPublicExit=[bool]($baseline -and $baseline -ne $r.probe.egressHash)
 }
}catch{$report.errorType=$_.Exception.GetType().Name}
finally{$labCredential=$null;$labVmCredential=$null;$report.nativeSha256=$nativeHash;$report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root ('runtime/native-awg-independent-'+$run+'.json')) -Encoding UTF8}
$report|ConvertTo-Json -Depth 5 -Compress|Write-Output
if(-not $report.success){throw 'Independent candidate did not pass the probe; see preserved receipt'}
