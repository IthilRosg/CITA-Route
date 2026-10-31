param([Parameter(Mandatory)][string]$Installer)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
$image=[IO.Path]::GetFullPath($Installer);$private=[IO.Path]::GetFullPath((Join-Path $workspace 'runtime/physical-acceptance'))
if(!$image.StartsWith($private+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Private acceptance image required'}
if((Get-Item -LiteralPath $image).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked image refused'}
$hash=(Get-FileHash -LiteralPath $image).Hash;$id=[guid]::NewGuid().ToString('N')
$remote="C:\Program Files\RoutePilot\Agent\cita-transfer-$id"
$journal=Join-Path $workspace 'runtime/installer-target-active.json'
$file=[IO.File]::Open($journal,'CreateNew','Write','Read')
try{$bytes=[Text.Encoding]::UTF8.GetBytes((@{id=$id;folder=$remote;imageHash=$hash;state='MayCreate'}|ConvertTo-Json -Compress));$file.Write($bytes,0,$bytes.Length);$file.Flush($true)}finally{$file.Dispose()}
$recipe=Join-Path $workspace 'tools/native-control-probe/Test-PhysicalTransfer.ps1'
if((Get-FileHash -LiteralPath $recipe).Hash -ne '42CB3AA03261C76905617CAE5EBFAFE3F927229E5672B3278B4EFC9A6758BECE'){throw 'Transfer recipe differs'}
$create=[regex]::Match([IO.File]::ReadAllText($recipe),"(?s)\`$create=@'\r?\n(.*?)\r?\n'@").Groups[1].Value
if(!$create -or (Invoke-BoundedRm $create.Replace('__DIR__',$remote)).Trim() -ne 'ready'){throw 'Protected transfer directory unconfirmed'}
Invoke-BoundedScp $image "citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/cita-transfer-$id/Installer.exe" 180
$code=@'
$ErrorActionPreference='Stop';$root='__ROOT__';$image=Join-Path $root 'Installer.exe';$log=Join-Path $root 'target.log'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong station'}
$intent='C:\Program Files\RoutePilot\Agent\native\intent.json';$agent='C:\Program Files\RoutePilot\Agent\RoutePilot.Agent.exe'
$key='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CITA.Route.Client_is1'
function State {
 $registry=if(Test-Path -LiteralPath $key){Get-ItemProperty -LiteralPath $key|Select-Object DisplayVersion,InstallLocation,UninstallString|ConvertTo-Json -Compress}else{'absent'}
 [ordered]@{registry=$registry;intentHash=(Get-FileHash -LiteralPath $intent).Hash;agentHash=(Get-FileHash -LiteralPath $agent).Hash;service=(Get-Service RoutePilotRescue).Status.ToString()}|ConvertTo-Json -Compress
}
$before=State;$lease=[IO.File]::Open($image,'Open','Read','Read')
try{
 if((Get-FileHash -InputStream $lease).Hash -ne '__HASH__'){throw 'Installer identity differs'}
 $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$image;$info.UseShellExecute=$false;$info.CreateNoWindow=$true
 $info.Arguments='/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CHECKPHYSICALTARGET /LOG="'+$log+'"'
 $p=[Diagnostics.Process]::Start($info);if(!$p){throw 'Target check did not start'}
 try{if(!$p.WaitForExit(30000)){try{$p.Kill();[void]$p.WaitForExit(5000)}catch{};throw 'Installer target observation expired; reconcile descendants before cleanup'};$exit=$p.ExitCode;$pidValue=$p.Id}finally{$p.Dispose()}
}finally{$lease.Dispose()}
$after=State;$text=[IO.File]::ReadAllText($log)
if($exit -ne 1 -or !$text.Contains('CITA_PHYSICAL_TARGET_VERIFIED_NO_INSTALL') -or $text.Contains('CITA_PHYSICAL_TARGET_REFUSED_NO_INSTALL') -or $before -cne $after){throw 'Verified no-install target proof failed'}
$active=@(Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -eq $image -or $_.ParentProcessId -eq $pidValue -or $_.CommandLine -like ('*'+$root+'*')})
if($active.Count){throw 'Installer descendants unconfirmed; cleanup withheld'}
$logHash=(Get-FileHash -LiteralPath $log).Hash
$folder=Get-Item -LiteralPath $root -Force
if(!$folder.PSIsContainer -or $folder.Attributes -band [IO.FileAttributes]::ReparsePoint -or [IO.Path]::GetDirectoryName($folder.FullName) -ne 'C:\Program Files\RoutePilot\Agent'){throw 'Cleanup directory differs'}
$files=@(Get-ChildItem -LiteralPath $root -Force)
if($files.Count -ne 3 -or @($files|Where-Object {$_.PSIsContainer -or $_.Name -notin @('Installer.exe','check.ps1','target.log') -or $_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Unexpected target check files'}
foreach($file in $files){Remove-Item -LiteralPath $file.FullName -ErrorAction Stop};Remove-Item -LiteralPath $root -ErrorAction Stop
[pscustomobject]@{run='__ID__';installerSha256='__HASH__';exit=$exit;verifiedNoInstall=$true;serviceIntentAgentRegistryUnchanged=$true;logSha256=$logHash;folderRemaining=(Test-Path -LiteralPath $root);processes=0;scope='Always-cancel target check; bootstrap temp/log files permitted, no installation requested'}|ConvertTo-Json -Compress
'@
$code=$code.Replace('__ROOT__',$remote).Replace('__HASH__',$hash).Replace('__ID__',$id)
$script=Join-Path $workspace "runtime/installer-target-$id.ps1"
[IO.File]::WriteAllText($script,$code,[Text.UTF8Encoding]::new($true));$scriptHash=(Get-FileHash -LiteralPath $script).Hash
Invoke-BoundedScp $script "citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/cita-transfer-$id/check.ps1"
$reply=(Invoke-BoundedRm "if((Get-FileHash -LiteralPath '$remote\check.ps1').Hash -ne '$scriptHash'){throw 'Target script differs'};& ([ScriptBlock]::Create([IO.File]::ReadAllText('$remote\check.ps1')))" 50)|ConvertFrom-Json
if($reply.run -ne $id -or $reply.installerSha256 -ne $hash -or !$reply.verifiedNoInstall -or $reply.folderRemaining -or $reply.processes -ne 0){throw 'Target receipt differs'}
$reply|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path (Split-Path $image) 'rm-target-check.json') -Encoding utf8
Remove-Item -LiteralPath $journal -ErrorAction Stop;Remove-Item -LiteralPath $script -ErrorAction Stop
$reply
