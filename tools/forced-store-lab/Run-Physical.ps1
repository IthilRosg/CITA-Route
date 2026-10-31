$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'runtime/invoke-rm-lab.ps1')
$id=[guid]::NewGuid().ToString('N')
$image=Join-Path $workspace 'runtime/forced-store-lab-build/Cita.ForcedStoreLab.exe'
$hash=(Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash
$stage="C:/CITA-Lab/forced-store-$id.exe"
& scp -q -i "$env:USERPROFILE\.ssh\cita_test_station_ed25519" -o "UserKnownHostsFile=$env:USERPROFILE\.ssh\cita_test_station_known_hosts" -o StrictHostKeyChecking=yes -o ConnectTimeout=10 -o ServerAliveInterval=5 -o ServerAliveCountMax=3 $image "citastation@26.68.227.253:$stage"
if($LASTEXITCODE){throw 'Lab executable transfer failed'}
$code=@'
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong physical station'}
$stage='C:\CITA-Lab\forced-store-__ID__.exe';$scriptPath='C:\CITA-Lab\forced-store-__ID__.ps1';$root='C:\Program Files\RoutePilot\Agent\forced-store-lab-__ID__';$created=$false;$process=$null;$success=$false;$cleanup=$false;$output='';$failure=''
try{
 if((Get-FileHash -LiteralPath $stage -Algorithm SHA256).Hash -ne '__HASH__'){throw 'Lab image hash mismatch'}
 $parent=Get-Item -LiteralPath 'C:\Program Files\RoutePilot\Agent' -Force
 for($part=$parent;$part -ne $null;$part=$part.Parent){if($part.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse parent refused'}}
 if(Test-Path -LiteralPath $root){throw 'Lab root already exists'}
 New-Item -ItemType Directory -Path $root -ErrorAction Stop | Out-Null;$created=$true
 $lease=[IO.File]::Open($stage,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 try{
  if((Get-FileHash -InputStream $lease -Algorithm SHA256).Hash -ne '__HASH__'){throw 'Lab image changed before launch'}
  $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$stage;$info.Arguments='--run "'+$root+'"';$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
  $process=[Diagnostics.Process]::Start($info);$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
  if(!$process.WaitForExit(60000)){throw 'File lab deadline'}
  $output=$stdout.Result;if($process.ExitCode -ne 0){throw ('File lab failed: '+$stderr.Result)}
  $data=$output|ConvertFrom-Json;if(!$data.success -or !$data.weakAclRefused -or !$data.barrierConfirmed -or !$data.crashBeforePreserved -or !$data.crashAfterRecovered -or $data.concurrentWinner -ne 1 -or $data.concurrentStaleRefused -ne 1 -or $data.secondStatus -ne 4){throw 'File lab acceptance failed'};$success=$true
 }finally{$lease.Dispose()}
}catch{$failure=$_.Exception.Message}
finally{
 if($process){try{if(!$process.HasExited){$process.Kill();if(!$process.WaitForExit(5000)){throw 'Lab controller exit unconfirmed'}}}catch{$failure+='; '+$_.Exception.Message}finally{$process.Dispose()}}
 try{
  $alive=@(Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -ieq $stage})
  if($alive.Count){throw 'Owned lab process still active'}
  if($created){
   $resolved=(Get-Item -LiteralPath $root -Force);if($resolved.FullName -cne $root -or ($resolved.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Lab root identity mismatch'}
   foreach($name in @('forced-intent.json','forced-intent.lock','ready-one','ready-two','release','crash-ready-before','crash-ready-after')){$p=Join-Path $root $name;if(Test-Path -LiteralPath $p){$item=Get-Item -LiteralPath $p -Force;if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unexpected fixture file'};Remove-Item -LiteralPath $p -Force}}
   foreach($item in @(Get-ChildItem -LiteralPath $root -File -Force|Where-Object {$_.Name -cmatch '^forced-intent-[a-f0-9]{32}\.new$'})){if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Unexpected temporary fixture'};Remove-Item -LiteralPath $item.FullName -Force}
   $weak=Join-Path $root 'weak';if(Test-Path -LiteralPath $weak){$item=Get-Item -LiteralPath $weak -Force;if(!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unexpected weak fixture'};Remove-Item -LiteralPath $weak -Force}
   Remove-Item -LiteralPath $root -Force
  }
  Remove-Item -LiteralPath $stage -Force;Remove-Item -LiteralPath $scriptPath -Force;$cleanup=$true
 }catch{$failure+='; cleanup: '+$_.Exception.Message}
}
$receipt=@{run='__ID__';imageHash='__HASH__';success=$success;cleanup=$cleanup;failure=$failure;result=$output;scope='physical protected files only, no network rules'}|ConvertTo-Json -Compress
$receiptPath='C:\CITA-Lab\forced-store-__ID__.json';$receiptTemporary=$receiptPath+'.new'
[IO.File]::WriteAllText($receiptTemporary,$receipt,[Text.UTF8Encoding]::new($false));[IO.File]::Move($receiptTemporary,$receiptPath)
$receipt
'@
$code=$code.Replace('__ID__',$id).Replace('__HASH__',$hash)
$scriptFile=Join-Path $workspace "runtime/forced-store-$id.ps1"
[IO.File]::WriteAllText($scriptFile,$code,[Text.UTF8Encoding]::new($true))
& scp -q -i "$env:USERPROFILE\.ssh\cita_test_station_ed25519" -o "UserKnownHostsFile=$env:USERPROFILE\.ssh\cita_test_station_known_hosts" -o StrictHostKeyChecking=yes -o ConnectTimeout=10 -o ServerAliveInterval=5 -o ServerAliveCountMax=3 $scriptFile "citastation@26.68.227.253:C:/CITA-Lab/forced-store-$id.ps1"
if($LASTEXITCODE){throw 'Lab script transfer failed; stage cleanup requires inspection'}
$result=((Invoke-Rm "& ([ScriptBlock]::Create([IO.File]::ReadAllText('C:\CITA-Lab\forced-store-$id.ps1'))) ") -join "`n")|ConvertFrom-Json
Remove-Item -LiteralPath $scriptFile -Force
if($result.run -cne $id -or $result.imageHash -cne $hash){throw 'Physical file lab receipt identity mismatch'}
$result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $workspace 'runtime/goal2-forced-physical-result.json') -Encoding utf8
if(!$result.success -or !$result.cleanup){throw 'Physical file lab or cleanup unconfirmed; local and remote receipts retained'}
$receiptRemove="`$p='C:\CITA-Lab\forced-store-$id.json';`$f=Get-Item -LiteralPath `$p -Force;if(`$f.PSIsContainer -or (`$f.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unexpected receipt'};Remove-Item -LiteralPath `$p -Force"
Invoke-Rm $receiptRemove|Out-Null
$result
