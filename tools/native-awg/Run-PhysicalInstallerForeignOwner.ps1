$ErrorActionPreference='Stop'
# Shared-service ownership and child cleanup must be independently fenced
# before this installer fixture can run against a live physical station.
throw 'Physical installer fixture disabled: shared-service fencing and child cleanup are not verified.'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'runtime/invoke-rm-lab.ps1')
$setup=Join-Path $workspace 'runtime/goal2-installer-fixture-current/CITA-Route-0.6.9-win-x64-isolated-test-setup.exe'
$f=Get-Item -LiteralPath $setup
if($f.VersionInfo.ProductName.Trim() -ne 'CITA Route Installer Test Isolated'){throw 'Wrong installer fixture'}
$hash=(Get-FileHash $setup).Hash;$id=[guid]::NewGuid().ToString('N')
$supervisor=Join-Path $workspace 'runtime/installer-supervisor-lab-build/Cita.InstallerSupervisorLab.exe';$supervisorHash=(Get-FileHash $supervisor).Hash
$runtimeCheck=Join-Path $workspace 'DesktopRuntime.ps1';$runtimeHash=(Get-FileHash $runtimeCheck).Hash
$fixtureReceipt=Get-Content (Join-Path $workspace 'runtime/goal2-installer-fixture-receipt.json') -Raw|ConvertFrom-Json
if($fixtureReceipt.fixtureSha256 -ne $hash -or $fixtureReceipt.runtimeSha256 -ne $runtimeHash -or $fixtureReceipt.stageManifestSha256 -ne (Get-FileHash (Join-Path $workspace 'dist/CITA-Route-0.6.9-win-x64-framework-17466f2d/manifest.json')).Hash){throw 'Installer fixture build provenance differs'}
& scp -q -i "$env:USERPROFILE\.ssh\cita_test_station_ed25519" -o "UserKnownHostsFile=$env:USERPROFILE\.ssh\cita_test_station_known_hosts" -o StrictHostKeyChecking=yes -o ConnectTimeout=10 -o ServerAliveInterval=5 -o ServerAliveCountMax=3 $setup "citastation@26.68.227.253:C:/CITA-Lab/installer-fixture-$id.exe"
if($LASTEXITCODE){throw 'Installer fixture transfer failed; inspect temporary files'}
foreach($item in @(@($supervisor,"C:/CITA-Lab/installer-fixture-$id.supervisor.exe"),@($runtimeCheck,"C:/CITA-Lab/installer-fixture-$id.runtime.ps1"))){
 & scp -q -i "$env:USERPROFILE\.ssh\cita_test_station_ed25519" -o "UserKnownHostsFile=$env:USERPROFILE\.ssh\cita_test_station_known_hosts" -o StrictHostKeyChecking=yes -o ConnectTimeout=10 -o ServerAliveInterval=5 -o ServerAliveCountMax=3 $item[0] "citastation@26.68.227.253:$($item[1])"
 if($LASTEXITCODE){throw 'Installer supervisor transfer failed; inspect temporary files'}
}
$code=@'
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong physical station'}
$image='C:\CITA-Lab\installer-fixture-__ID__.exe';$supervisor='C:\CITA-Lab\installer-fixture-__ID__.supervisor.exe';$runtimeScript='C:\CITA-Lab\installer-fixture-__ID__.runtime.ps1';$script='C:\CITA-Lab\installer-fixture-__ID__.ps1';$log='C:\CITA-Lab\installer-fixture-__ID__.log';$target='C:\CITA-Lab\installer-fixture-__ID__';$receipt='C:\CITA-Lab\installer-fixture-__ID__.json'
$serviceConfig='C:\Program Files\RoutePilot\Agent\native\service.json';$ownerConfig='C:\Program Files\RoutePilot\Agent\agent-config.json';$reg='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CITA.Route.InstallerFixture.Isolated_is1'
$process=$null;$ownerLease=$null;$serviceLease=$null;$helperLease=$null;$runtimeLease=$null;$success=$false;$clean=$false;$failure='';$reasonConfirmed=$false;$exit=-1
try{
 if((Get-FileHash -LiteralPath $image).Hash -ne '__HASH__'){throw 'Fixture image hash mismatch'}
 $helperLease=[IO.File]::Open($supervisor,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);$runtimeLease=[IO.File]::Open($runtimeScript,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 if((Get-FileHash -InputStream $helperLease).Hash -ne '__SUPHASH__' -or (Get-FileHash -InputStream $runtimeLease).Hash -ne '__RUNTIMEHASH__'){throw 'Fixture helper hash mismatch'}
 if(!(Test-Path $serviceConfig) -or !(Test-Path $ownerConfig) -or (Test-Path $target) -or (Test-Path $reg)){throw 'Fixture precondition refused'}
 foreach($path in @($serviceConfig,$ownerConfig)){$item=Get-Item -LiteralPath $path -Force;if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unexpected protected configuration file'}}
 for($part=(Get-Item -LiteralPath 'C:\CITA-Lab' -Force);$part -ne $null;$part=$part.Parent){if($part.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse fixture parent refused'}}
 $ownerLease=[IO.File]::Open($ownerConfig,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 $serviceLease=[IO.File]::Open($serviceConfig,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 $reader=[IO.StreamReader]::new($ownerLease,[Text.Encoding]::UTF8,$true,1024,$true);try{$owner=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()};$userSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
 if(!$owner.AppRoot -or !$owner.UserSid){throw 'Service owner identity incomplete'}
 if([IO.Path]::GetFullPath($owner.AppRoot).TrimEnd('\') -ieq [IO.Path]::GetFullPath((Join-Path $target 'client')).TrimEnd('\') -and $owner.UserSid -eq $userSid){throw 'Fixture unexpectedly owns the service'}
 . $runtimeScript;if(!(Test-MachineDesktopRuntime)){throw 'Required Windows Desktop runtime unavailable'}
 $beforeService=(Get-FileHash $serviceConfig).Hash;$beforeOwner=(Get-FileHash $ownerConfig).Hash;$beforeState=(Get-Service RoutePilotRescue).Status.ToString()
 $beforePid=(Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'").ProcessId;$beforeStart=if($beforePid){(Get-Process -Id $beforePid).StartTime.ToUniversalTime().Ticks}else{0}
 $lock=[IO.File]::Open($image,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 try{
  if((Get-FileHash -InputStream $lock).Hash -ne '__HASH__'){throw 'Fixture image changed'}
  $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$supervisor;$info.Arguments='"'+$image+'" "'+$target+'" "'+$log+'"';$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
  $process=[Diagnostics.Process]::Start($info);$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync();if(!$process.WaitForExit(70000)){throw 'Installer fixture deadline'}
  if($process.ExitCode -ne 0){throw 'Installer supervisor failed'};$report=$stdout.Result|ConvertFrom-Json;$null=$stderr.Result;if(!$report.finished -or !$report.jobEmpty){throw 'Installer job retirement unconfirmed'};$exit=$report.exit
  if(Test-Path $log){$text=[IO.File]::ReadAllText($log);$reasonConfirmed=$text.Contains('Нативная служба принадлежит другой установке')}
  $afterState=(Get-Service RoutePilotRescue).Status.ToString()
  $afterPid=(Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'").ProcessId;$afterStart=if($afterPid){(Get-Process -Id $afterPid).StartTime.ToUniversalTime().Ticks}else{0}
  $same=(Get-FileHash $serviceConfig).Hash -eq $beforeService -and (Get-FileHash $ownerConfig).Hash -eq $beforeOwner -and $afterState -eq $beforeState -and $afterPid -eq $beforePid -and $afterStart -eq $beforeStart
  $empty=!(Test-Path $target) -or @(Get-ChildItem -LiteralPath $target -Force -ErrorAction Stop).Count -eq 0
  if($exit -eq 0 -or !$reasonConfirmed -or !$same -or !$empty -or (Test-Path $reg)){throw 'Foreign-owner refusal or unchanged state unconfirmed'}
  $success=$true
 }finally{$lock.Dispose()}
}catch{$failure=$_.Exception.Message}
finally{
 if($process){try{if(!$process.HasExited){$process.Kill();if(!$process.WaitForExit(5000)){throw 'Installer process exit unconfirmed'}}}catch{$failure+='; '+$_.Exception.Message}finally{$process.Dispose()}}
 if($serviceLease){$serviceLease.Dispose()};if($ownerLease){$ownerLease.Dispose()};if($runtimeLease){$runtimeLease.Dispose()};if($helperLease){$helperLease.Dispose()}
 try{
  if(@(Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -ieq $image}).Count){throw 'Fixture process still active'}
  $guards=@(Get-CimInstance Win32_Process|Where-Object {$_.Name -match '^powershell(\.exe)?$' -and $_.CommandLine -and $_.CommandLine.IndexOf($target,[StringComparison]::OrdinalIgnoreCase) -ge 0 -and $_.CommandLine -match 'InstallationGuard\.ps1'})
  if($guards.Count){throw 'Owned installer guard still active; cleanup refused'}
  if(Test-Path $target){$d=Get-Item -LiteralPath $target -Force;if(!$d.PSIsContainer -or ($d.Attributes -band [IO.FileAttributes]::ReparsePoint) -or @(Get-ChildItem -LiteralPath $target -Force).Count){throw 'Non-empty or unsafe fixture directory retained'};Remove-Item -LiteralPath $target -Force}
  foreach($path in @($image,$supervisor,$runtimeScript,$script,$log)){if(Test-Path -LiteralPath $path){$item=Get-Item -LiteralPath $path -Force;if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unsafe fixture artifact retained'};Remove-Item -LiteralPath $path -Force}}
  $clean=$true
 }catch{$failure+='; cleanup: '+$_.Exception.Message}
}
$result=@{run='__ID__';imageHash='__HASH__';success=$success;cleanup=$clean;exit=$exit;reasonConfirmed=$reasonConfirmed;failure=$failure;scope='isolated installer foreign-owner refusal; not installation or update'}|ConvertTo-Json -Compress
[IO.File]::WriteAllText($receipt+'.new',$result,[Text.UTF8Encoding]::new($false));[IO.File]::Move($receipt+'.new',$receipt);$result
'@
$local=Join-Path $workspace "runtime/installer-fixture-$id.ps1";[IO.File]::WriteAllText($local,$code.Replace('__ID__',$id).Replace('__HASH__',$hash).Replace('__SUPHASH__',$supervisorHash).Replace('__RUNTIMEHASH__',$runtimeHash),[Text.UTF8Encoding]::new($true))
& scp -q -i "$env:USERPROFILE\.ssh\cita_test_station_ed25519" -o "UserKnownHostsFile=$env:USERPROFILE\.ssh\cita_test_station_known_hosts" -o StrictHostKeyChecking=yes -o ConnectTimeout=10 -o ServerAliveInterval=5 -o ServerAliveCountMax=3 $local "citastation@26.68.227.253:C:/CITA-Lab/installer-fixture-$id.ps1"
if($LASTEXITCODE){throw 'Installer fixture script transfer failed; inspect temporary files'}
$result=((Invoke-Rm "& ([ScriptBlock]::Create([IO.File]::ReadAllText('C:\CITA-Lab\installer-fixture-$id.ps1'))) ") -join "`n")|ConvertFrom-Json
Remove-Item -LiteralPath $local -Force
if($result.run -cne $id -or $result.imageHash -cne $hash){throw 'Fixture receipt identity mismatch'}
$result|ConvertTo-Json|Set-Content (Join-Path $workspace 'runtime/goal2-installer-foreign-owner-result.json') -Encoding utf8
if(!$result.success -or !$result.cleanup){throw 'Fixture result unconfirmed; receipts retained'}
$remove="`$p='C:\CITA-Lab\installer-fixture-$id.json';`$f=Get-Item -LiteralPath `$p -Force;if(`$f.PSIsContainer -or (`$f.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unexpected receipt'};Remove-Item -LiteralPath `$p -Force"
Invoke-Rm $remove|Out-Null
$result
