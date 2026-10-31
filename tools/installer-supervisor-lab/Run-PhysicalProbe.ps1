$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'runtime/invoke-rm-lab.ps1')
$id=[guid]::NewGuid().ToString('N');$image=Join-Path $workspace 'runtime/installer-supervisor-lab-build/Cita.InstallerSupervisorLab.exe';$hash=(Get-FileHash $image).Hash
$station=((Invoke-Rm "(Get-CimInstance Win32_ComputerSystemProduct).UUID") -join '').Trim()
if($station -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong or unreachable physical station'}
& scp -q -i "$env:USERPROFILE\.ssh\cita_test_station_ed25519" -o "UserKnownHostsFile=$env:USERPROFILE\.ssh\cita_test_station_known_hosts" -o StrictHostKeyChecking=yes -o ConnectTimeout=10 -o ServerAliveInterval=5 -o ServerAliveCountMax=3 $image "citastation@26.68.227.253:C:/CITA-Lab/supervisor-probe-$id.exe"
if($LASTEXITCODE){throw 'Supervisor probe transfer failed; inspect temporary files'}
$code=@'
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong physical station'}
$image='C:\CITA-Lab\supervisor-probe-__ID__.exe';$script='C:\CITA-Lab\supervisor-probe-__ID__.ps1';$root='C:\Program Files\RoutePilot\Agent\supervisor-probe-__ID__';$receipt='C:\CITA-Lab\supervisor-probe-__ID__.json';$process=$null;$child=$null;$created=$false;$success=$false;$clean=$false;$failure=''
try{
 $lease=[IO.File]::Open($image,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 try{
  if((Get-FileHash -InputStream $lease).Hash -ne '__HASH__'){throw 'Probe image differs'}
  for($part=(Get-Item -LiteralPath 'C:\Program Files\RoutePilot\Agent' -Force);$part -ne $null;$part=$part.Parent){if($part.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse probe parent'}}
  if(Test-Path -LiteralPath $root){throw 'Probe root already exists'};New-Item -ItemType Directory -Path $root -ErrorAction Stop|Out-Null;$created=$true
  $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$image;$info.Arguments='--probe-parent "'+$root+'"';$info.UseShellExecute=$false;$info.CreateNoWindow=$true
  $process=[Diagnostics.Process]::Start($info);$marker=Join-Path $root 'ready.json';$deadline=[DateTime]::UtcNow.AddSeconds(15)
  while(!(Test-Path -LiteralPath $marker)){if($process.HasExited -or [DateTime]::UtcNow -ge $deadline){throw 'Probe child did not start'};Start-Sleep -Milliseconds 50}
  $ready=[IO.File]::ReadAllText($marker)|ConvertFrom-Json;$candidate=Get-Process -Id $ready.pid -ErrorAction Stop
  try{
   $details=Get-CimInstance Win32_Process -Filter ('ProcessId='+[int]$ready.pid)
   if($candidate.StartTime.ToUniversalTime().Ticks -ne $ready.started -or $candidate.Path -ine $image -or $details.ParentProcessId -ne $process.Id){throw 'Probe child identity mismatch'}
   $child=$candidate;$candidate=$null
  }finally{if($candidate){$candidate.Dispose()}}
  $process.Kill();if(!$process.WaitForExit(5000) -or !$child.WaitForExit(5000)){throw 'Job did not retire both processes'};$success=$true
 }finally{$lease.Dispose()}
}catch{$failure=$_.Exception.Message}
finally{
 foreach($p in @($child,$process)){if($p){try{if(!$p.HasExited){$p.Kill();if(!$p.WaitForExit(5000)){throw 'Owned probe did not exit'}}}catch{$failure+='; '+$_.Exception.Message}finally{$p.Dispose()}}}
 try{
  if(@(Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -ieq $image}).Count){throw 'Owned probe process still active'}
  if($created){$d=Get-Item -LiteralPath $root -Force;if($d.FullName -cne $root -or ($d.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Probe root identity mismatch'};foreach($name in @('ready.json','ready.json.new')){$marker=Join-Path $root $name;if(Test-Path -LiteralPath $marker){$f=Get-Item -LiteralPath $marker -Force;if($f.PSIsContainer -or ($f.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unsafe marker'};Remove-Item -LiteralPath $marker -Force}};Remove-Item -LiteralPath $root -Force}
  foreach($path in @($image,$script)){$f=Get-Item -LiteralPath $path -Force;if($f.PSIsContainer -or ($f.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unsafe probe artifact'};Remove-Item -LiteralPath $path -Force};$clean=$true
 }catch{$failure+='; cleanup: '+$_.Exception.Message}
}
$result=@{run='__ID__';imageHash='__HASH__';success=$success;cleanup=$clean;failure=$failure;scope='owned supervisor/job probe; no installer, firewall or route changes'}|ConvertTo-Json -Compress
[IO.File]::WriteAllText($receipt+'.new',$result,[Text.UTF8Encoding]::new($false));[IO.File]::Move($receipt+'.new',$receipt);$result
'@
$local=Join-Path $workspace "runtime/supervisor-probe-$id.ps1";[IO.File]::WriteAllText($local,$code.Replace('__ID__',$id).Replace('__HASH__',$hash),[Text.UTF8Encoding]::new($true))
& scp -q -i "$env:USERPROFILE\.ssh\cita_test_station_ed25519" -o "UserKnownHostsFile=$env:USERPROFILE\.ssh\cita_test_station_known_hosts" -o StrictHostKeyChecking=yes -o ConnectTimeout=10 -o ServerAliveInterval=5 -o ServerAliveCountMax=3 $local "citastation@26.68.227.253:C:/CITA-Lab/supervisor-probe-$id.ps1"
if($LASTEXITCODE){throw 'Supervisor probe script transfer failed; inspect temporary files'}
$result=((Invoke-Rm "& ([ScriptBlock]::Create([IO.File]::ReadAllText('C:\CITA-Lab\supervisor-probe-$id.ps1'))) ") -join "`n")|ConvertFrom-Json
Remove-Item -LiteralPath $local -Force
if($result.run -cne $id -or $result.imageHash -cne $hash){throw 'Supervisor probe receipt mismatch'}
$result|ConvertTo-Json|Set-Content (Join-Path $workspace 'runtime/goal2-installer-supervisor-probe-result.json') -Encoding utf8
if(!$result.success -or !$result.cleanup){throw 'Supervisor probe unconfirmed; remote receipt retained'}
$remove="`$p='C:\CITA-Lab\supervisor-probe-$id.json';`$f=Get-Item -LiteralPath `$p -Force;if(`$f.PSIsContainer -or (`$f.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unexpected receipt'};Remove-Item -LiteralPath `$p -Force"
Invoke-Rm $remove|Out-Null
$result
