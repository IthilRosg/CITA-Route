$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell 7 is required for bounded SSH process arguments.'}
# Limited stdio-only test: child exits on EOF; on uncertain SSH outcome inspect
# this exact run before retrying. This is not a kill-on-close watchdog.
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
& (Join-Path $PSScriptRoot 'Verify-PhysicalProbeBuild.ps1')
. (Join-Path $PSScriptRoot 'Invoke-BoundedRm.ps1')
$uuid=(Invoke-BoundedRm '(Get-CimInstance Win32_ComputerSystemProduct).UUID').Trim()
if($uuid -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Physical station identity mismatch'}
$id=[guid]::NewGuid().ToString('N')
$image=Join-Path $workspace 'runtime/control-probe-publish/NativeControlProbe.exe'
$agent=Join-Path $workspace 'runtime/goal2-preflight-agent-build/RoutePilot.Agent.dll'
$imageHash=(Get-FileHash -LiteralPath $image).Hash;$agentHash=(Get-FileHash -LiteralPath $agent).Hash
$base="control-probe-$id"
$folder="cita-control-$id";$remoteFolder="C:\Program Files\RoutePilot\Agent\$folder"
$journal=Join-Path $workspace 'runtime/goal2-control-active.json'
if(Test-Path -LiteralPath $journal){throw 'An earlier run needs reconciliation; inspect runtime/goal2-control-active.json'}
$entry=@{run=$id;folder=$remoteFolder;agentHash=$agentHash;imageHash=$imageHash;state='MayCreate';createdUtc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json -Compress
$journalStream=[IO.File]::Open($journal,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
try{$bytes=[Text.Encoding]::UTF8.GetBytes($entry);$journalStream.Write($bytes,0,$bytes.Length);$journalStream.Flush($true)}finally{$journalStream.Dispose()}
# Use the same exclusively-created and SID-checked directory recipe as the
# completed transfer check, without executing its unrelated transfer/cleanup.
$recipePath=Join-Path $PSScriptRoot 'Test-PhysicalTransfer.ps1'
if((Get-FileHash -LiteralPath $recipePath -Algorithm SHA256).Hash -cne '42CB3AA03261C76905617CAE5EBFAFE3F927229E5672B3278B4EFC9A6758BECE'){throw 'Protected folder recipe changed after successful transfer test'}
$transferSource=Get-Content -LiteralPath $recipePath -Raw
$create=[regex]::Match($transferSource,"(?s)\`$create=@'\r?\n(.*?)\r?\n'@").Groups[1].Value
if(!$create){throw 'Protected folder recipe unavailable'}
$ready=(Invoke-BoundedRm $create.Replace('__DIR__',$remoteFolder)).Trim();if($ready -ne 'ready'){throw "Protected directory unconfirmed: $id"}
foreach($item in @(@($image,"$base.exe"),@($agent,"$base.agent.dll"))){
 Invoke-BoundedScp $item[0] "citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/$folder/$($item[1])"
}
$code=@'
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong physical station'}
$base='C:\Program Files\RoutePilot\Agent\cita-control-__ID__\control-probe-__ID__';$image=$base+'.exe';$agent=$base+'.agent.dll';$script=$base+'.ps1';$receipt=$base+'.json';$p=$null;$success=$false;$clean=$false;$failure='';$exit=-1;$tests=0;$leases=@()
try{
 $parent=Get-Item -LiteralPath (Split-Path -Parent $base) -Force;while($parent){if($parent.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse parent'};$parent=$parent.Parent}
 foreach($item in @(@($image,'__IMAGE__'),@($agent,'__AGENT__'))){$f=Get-Item -LiteralPath $item[0] -Force;if($f.PSIsContainer -or ($f.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unsafe probe file'};$lock=[IO.File]::Open($item[0],[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);$leases+= $lock;if((Get-FileHash -InputStream $lock).Hash -ne $item[1]){throw 'Probe file mismatch'}}
 $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$image;$info.Arguments='"'+$agent+'"';$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $p=[Diagnostics.Process]::Start($info);$out=$p.StandardOutput.ReadToEndAsync();$err=$p.StandardError.ReadToEndAsync()
 if(!$p.WaitForExit(60000)){throw 'Probe timeout'};$exit=$p.ExitCode
 if(![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($out,$err),5000)){throw 'Probe output drain timeout'}
 $output=$out.Result;$null=$err.Result
 $seen=@([regex]::Matches($output,'(?m)^([a-z-]+): PASS\s*$')|ForEach-Object {$_.Groups[1].Value});$expected=@('bad-policy','late-policy','rejected','bad-scope','bad-retirement','retirement-pending','bad-owner','bad-generation','bad-revision','missing-digest','bad-telemetry','concurrent','cancel-after-write','cancel-before-write','ok','calibration','calibration-lock','connector-ownership','zapret-authorization','connector-admission','connector-authorization-race','connector-replace','zapret-policy-gate','zapret-probe','zapret-manifest','policy-staging','atomic-process','core-recovery-failure');$tests=$seen.Count
 if($exit -ne 0 -or $tests -ne $expected.Count -or @(Compare-Object ($seen|Sort-Object) ($expected|Sort-Object)).Count){throw 'Probe incomplete'};$success=$true
}catch{$failure=$_.Exception.Message}
finally{
 if($p){try{if(!$p.HasExited){$p.Kill();if(!$p.WaitForExit(5000)){throw 'Probe exit unconfirmed'}}}catch{$failure+='; '+$_.Exception.Message}finally{$p.Dispose()}}
 foreach($lock in $leases){$lock.Dispose()}
 try{
  $deadline=[DateTime]::UtcNow.AddSeconds(12);do{$running=@(Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -ieq $image});if($running.Count){Start-Sleep -Milliseconds 200}}while($running.Count -and [DateTime]::UtcNow -lt $deadline)
  if($running.Count){throw 'Probe process still active; cleanup withheld'}
  foreach($path in @($image,$agent,$script)){$f=Get-Item -LiteralPath $path -Force;if($f.PSIsContainer -or ($f.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unsafe probe artifact'};Remove-Item -LiteralPath $path -Force};$clean=$true
 }catch{$failure+='; cleanup: '+$_.Exception.Message}
}
$result=@{run='__ID__';imageHash='__IMAGE__';agentHash='__AGENT__';success=$success;cleanup=$clean;exit=$exit;tests=$tests;failure=$failure;scope='stdio fake core and local named pipes; no firewall or route changes'}|ConvertTo-Json -Compress
[IO.File]::WriteAllText($receipt+'.new',$result,[Text.UTF8Encoding]::new($false));[IO.File]::Move($receipt+'.new',$receipt);$result
'@
$local=Join-Path $workspace "runtime/$base.ps1"
[IO.File]::WriteAllText($local,$code.Replace('__ID__',$id).Replace('__IMAGE__',$imageHash).Replace('__AGENT__',$agentHash),[Text.UTF8Encoding]::new($true))
Invoke-BoundedScp $local "citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/$folder/$base.ps1"
$result=(Invoke-BoundedRm "& ([ScriptBlock]::Create([IO.File]::ReadAllText('$remoteFolder\$base.ps1'))) " 100)|ConvertFrom-Json
Remove-Item -LiteralPath $local -Force
if($result.run -cne $id -or $result.imageHash -cne $imageHash -or $result.agentHash -cne $agentHash){throw 'Probe receipt mismatch'}
$result|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $workspace 'runtime/goal2-control-physical-result.json') -Encoding utf8
if(!$result.success -or !$result.cleanup){throw 'Probe or cleanup unconfirmed; receipt retained'}
& (Join-Path $PSScriptRoot 'Verify-PhysicalProbeBuild.ps1')
Invoke-BoundedRm "`$p='$remoteFolder\$base.json';`$f=Get-Item -LiteralPath `$p -Force;if(`$f.PSIsContainer -or (`$f.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unexpected receipt'};`$d=Get-Item -LiteralPath '$remoteFolder' -Force;if(!`$d.PSIsContainer -or (`$d.Attributes -band [IO.FileAttributes]::ReparsePoint) -or @(Get-ChildItem -LiteralPath `$d.FullName -Force).Count -ne 1){throw 'Unexpected folder contents'};Remove-Item -LiteralPath `$p -Force;Remove-Item -LiteralPath `$d.FullName -ErrorAction Stop"|Out-Null
Remove-Item -LiteralPath $journal -Force
$result
