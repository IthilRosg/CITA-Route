$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$root='C:\CitaLab\NativeAwg';$exe=Join-Path $root 'native-awg-lab.exe'
$report=@{schema=1;success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='Owner crash closes child stdin lease; wrong session/generation fails closed, no TUN or production session'}
$script:leaseOwners=@();$script:leaseChildren=@()
function StartOwner([int]$generation){
 $info=[Diagnostics.ProcessStartInfo]::new($exe,'--supervise-proxy-lab');$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $p=[Diagnostics.Process]::Start($info);$script:leaseOwners+=,$p;$errors=$p.StandardError.ReadToEndAsync();$ready=$p.StandardOutput.ReadLineAsync()
 $session=[guid]::NewGuid().ToString('N');$token=([guid]::NewGuid().ToString('N')+[guid]::NewGuid().ToString('N'))
 $p.StandardInput.WriteLine((@{Config=[IO.File]::ReadAllText((Join-Path $root 'private\peer.conf'));Token=$token;Session=$session;Generation=$generation}|ConvertTo-Json -Compress));$token=$null
 if(-not $ready.Wait(10000)){throw 'Owner readiness deadline'};$r=$ready.Result|ConvertFrom-Json
 if($r.stage -ne 'ListeningUnverified' -or $r.session -ne $session -or $r.generation -ne $generation){throw 'Owner readiness mismatch'}
 $p.StandardInput.WriteLine((@{Session=$session;Generation=$generation;Action='Snapshot'}|ConvertTo-Json -Compress));$line=$p.StandardOutput.ReadLineAsync();if(-not $line.Wait(2000)){throw 'Owner snapshot deadline'};$s=$line.Result|ConvertFrom-Json
 if($s.epoch -ne 1 -or $s.childPid -le 0){throw 'Missing owned child'}
 $child=Get-Process -Id $s.childPid;$script:leaseChildren+=,@{pid=$child.Id;ticks=$child.StartTime.ToUniversalTime().Ticks};$child.Dispose()
 return @{process=$p;session=$session;generation=$generation}
}
function WaitChildrenClosed{
 for($i=0;$i -lt 60;$i++){
  $remaining=@($script:leaseChildren|Where-Object {$p=Get-Process -Id $_.pid -ErrorAction SilentlyContinue;$p -and $p.StartTime.ToUniversalTime().Ticks -eq $_.ticks})
  if($remaining.Count -eq 0){return $true};Start-Sleep -Milliseconds 100
 };return $false
}
try{
 if(Get-Process native-awg-lab,native-core-lab,smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'Guest must be idle'}
 $report.nativeSha256=(Get-FileHash $exe).Hash
 $one=StartOwner 1;$one.process.Kill();$one.process.WaitForExit();$report.orphanLeaseClosed=WaitChildrenClosed
 if(-not $report.orphanLeaseClosed){throw 'Child survived owner crash'}
 foreach($mode in @('generation','session')){
  $two=StartOwner 2
  $command=@{Session=$two.session;Generation=2;Action='CrashChild'}
  if($mode -eq 'generation'){$command.Generation=1}else{$command.Session=$one.session}
  $two.process.StandardInput.WriteLine(($command|ConvertTo-Json -Compress))
  if(-not $two.process.WaitForExit(6000)){throw 'Stale owner command deadline'}
  $closed=WaitChildrenClosed;$report[($mode+'Fenced')]=[bool]($two.process.ExitCode -eq 1 -and $closed)
 }
 $report.success=[bool]($report.orphanLeaseClosed -and $report.generationFenced -and $report.sessionFenced)
}catch{$report.errorType=$_.Exception.GetType().Name}
finally{
 foreach($p in $script:leaseOwners){if(-not $p.HasExited){$p.Kill();$p.WaitForExit()};$p.Dispose()}
 if(-not (WaitChildrenClosed)){
  $report.success=$false;$report.forcedOrphanCleanup=$true
  foreach($identity in $script:leaseChildren){$p=Get-Process -Id $identity.pid -ErrorAction SilentlyContinue;if($p -and $p.StartTime.ToUniversalTime().Ticks -eq $identity.ticks -and $p.Path -eq $exe){$p.Kill();$p.WaitForExit()}}
 }
 $report.processesStopped=[bool](-not (Get-Process native-awg-lab -ErrorAction SilentlyContinue));if(-not $report.processesStopped){$report.success=$false}
 $report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 4|Set-Content (Join-Path $root 'owner-lease.json') -Encoding UTF8
}
if(-not $report.success){exit 1}
