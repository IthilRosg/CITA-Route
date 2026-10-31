param()
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
$journal=Join-Path $workspace 'runtime/native-preparation-active.json'
if(Test-Path -LiteralPath $journal){throw 'Existing preparation must be reconciled before continuing.'}
$id=[guid]::NewGuid().ToString('N');$root='C:\Program Files\RoutePilot\Agent\cita-prepare-'+$id
$source=[ordered]@{
 'Install-NativeComponents.ps1'=(Join-Path $workspace 'Install-NativeComponents.ps1')
 'NativeMaintenance.ps1'=(Join-Path $workspace 'NativeMaintenance.ps1')
 'AgentLease.ps1'=(Join-Path $workspace 'AgentLease.ps1')
 'installer/NativeUpgradeTransaction.ps1'=(Join-Path $workspace 'installer/NativeUpgradeTransaction.ps1')
}
$hashes=[ordered]@{};foreach($key in $source.Keys){$hashes[$key]=(Get-FileHash -LiteralPath $source[$key]).Hash}
$record=@{schema=1;id=$id;root=$root;stage=('C:\Program Files\RoutePilot\Agent\native-stage-'+$id);hashes=$hashes;state='MayCreate';utc=[DateTime]::UtcNow.ToString('o')}
$f=[IO.File]::Open($journal,'CreateNew','Write','Read');try{$b=[Text.Encoding]::UTF8.GetBytes(($record|ConvertTo-Json -Depth 5 -Compress));$f.Write($b,0,$b.Length);$f.Flush($true)}finally{$f.Dispose()}
$recipe=Join-Path $workspace 'tools/native-control-probe/Test-PhysicalTransfer.ps1'
if((Get-FileHash -LiteralPath $recipe).Hash -ne '42CB3AA03261C76905617CAE5EBFAFE3F927229E5672B3278B4EFC9A6758BECE'){throw 'Protected directory recipe differs.'}
$create=[regex]::Match([IO.File]::ReadAllText($recipe),"(?s)\`$create=@'\r?\n(.*?)\r?\n'@").Groups[1].Value
if(!$create -or (Invoke-BoundedRm $create.Replace('__DIR__',$root)).Trim() -ne 'ready'){throw 'Protected preparation root unconfirmed.'}
Invoke-BoundedRm "[void][IO.Directory]::CreateDirectory('$root\installer');'ready'"|Out-Null
foreach($key in $source.Keys){Invoke-BoundedScp $source[$key] ('citastation@26.68.227.253:'+($root.Replace('\','/'))+'/'+$key) 60}
$actor=@'
$ErrorActionPreference='Stop'
$root='__ROOT__';$id='__ID__';$agent='C:\Program Files\RoutePilot\Agent';$client='C:\Users\CITASTATION\AppData\Local\Programs\CITA Route\client'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Physical machine differs'}
$config=[IO.File]::ReadAllText((Join-Path $agent 'agent-config.json'))|ConvertFrom-Json
if($config.UserSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or $config.AppRoot -ine $client){throw 'Physical installation owner differs'}
$pins=@{'Install-NativeComponents.ps1'='__INSTALL__';'NativeMaintenance.ps1'='__MAINT__';'AgentLease.ps1'='__LEASE__';'installer/NativeUpgradeTransaction.ps1'='__JOURNAL__'}
$held=New-Object 'System.Collections.Generic.List[System.IDisposable]';$reserved=$false
try{
 foreach($name in $pins.Keys){$path=Join-Path $root $name;$file=[IO.File]::Open($path,'Open','Read','Read');$held.Add($file);$sha=[Security.Cryptography.SHA256]::Create();try{$digest=([BitConverter]::ToString($sha.ComputeHash($file))).Replace('-','')}finally{$sha.Dispose()};if($digest -ne $pins[$name]){throw 'Preparation source differs'}}
 foreach($name in @('connection-operation.lock','setup.lock','upgrade.lock','network-control.lock','installation-maintenance.lock')){$path=Join-Path $client ('runtime\'+$name);if((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked client lock refused'};$held.Add([IO.File]::Open($path,'OpenOrCreate','ReadWrite','None'))}
 . (Join-Path $root 'NativeMaintenance.ps1');. (Join-Path $root 'AgentLease.ps1');. (Join-Path $root 'installer/NativeUpgradeTransaction.ps1')
 Invoke-AgentCoordinator 'begin-operation';$reserved=$true
 Assert-NativePreparationState ((Invoke-NativeMaintenanceRpc 'status').Native)
 $before=Get-NativeUpgradeInventoryFromDirectory (Join-Path $agent 'native');$beforeJson=ConvertTo-Json -InputObject @($before) -Depth 6 -Compress
 $agentHash=(Get-FileHash -LiteralPath (Join-Path $agent 'RoutePilot.Agent.exe')).Hash;$servicePid=(Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'").ProcessId
 $package=Join-Path $client 'runtime/native-components.zip';$zipHash='6E9248D57B4C9296B49788D6244A772C0EA7A02723D3DE16ECFD58E192180083'
 $prepared=& (Join-Path $root 'Install-NativeComponents.ps1') -Package $package -Sha256 $zipHash -PrepareOnly -TransactionId $id
 if(@($prepared).Count -ne 1 -or $prepared.transactionId -cne $id -or $prepared.stage -ine (Join-Path $agent ('native-stage-'+$id)) -or $prepared.archiveSha -ne $zipHash){throw 'Prepared stage receipt differs'}
 $after=Get-NativeUpgradeInventoryFromDirectory (Join-Path $agent 'native');$afterJson=ConvertTo-Json -InputObject @($after) -Depth 6 -Compress
 $svc=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'"
 if($beforeJson -cne $afterJson -or (Get-FileHash -LiteralPath (Join-Path $agent 'RoutePilot.Agent.exe')).Hash -ne $agentHash -or $svc.ProcessId -ne $servicePid -or $svc.State -ne 'Running'){throw 'Existing native installation changed'}
 Assert-NativePreparationState ((Invoke-NativeMaintenanceRpc 'status').Native)
 $inventory=Get-NativeUpgradeInventoryFromDirectory $prepared.stage
 $receipt=@{schema=1;id=$id;stage=$prepared.stage;archiveSha=$zipHash;catalogSha=$prepared.catalogSha;configured=[bool]$prepared.configured;inventory=@($inventory);existingNativeUnchanged=$true;protectedAgentUnchanged=$true;servicePidUnchanged=$true;utc=[DateTime]::UtcNow.ToString('o');scope='Prepared files only; no promotion, service restart or connection'}
 [IO.File]::WriteAllText((Join-Path $root 'result.json'),($receipt|ConvertTo-Json -Depth 8 -Compress),[Text.UTF8Encoding]::new($false))
}catch{
 $report=@{id=$id;errorType=$_.Exception.GetType().Name;line=$_.InvocationInfo.ScriptLineNumber;utc=[DateTime]::UtcNow.ToString('o')}
 [IO.File]::WriteAllText((Join-Path $root 'failure.json'),($report|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false));exit 1
}finally{if($reserved){try{Invoke-AgentCoordinator 'end-operation'}catch{}};for($i=$held.Count-1;$i -ge 0;$i--){$held[$i].Dispose()}}
'prepared'
'@
foreach($pair in @(@('__ROOT__',$root),@('__ID__',$id),@('__INSTALL__',$hashes['Install-NativeComponents.ps1']),@('__MAINT__',$hashes['NativeMaintenance.ps1']),@('__LEASE__',$hashes['AgentLease.ps1']),@('__JOURNAL__',$hashes['installer/NativeUpgradeTransaction.ps1']))){$actor=$actor.Replace($pair[0],$pair[1])}
$actorPath=Join-Path $workspace ('runtime/native-prepare-'+$id+'.ps1');[IO.File]::WriteAllText($actorPath,$actor,[Text.UTF8Encoding]::new($true));$actorHash=(Get-FileHash -LiteralPath $actorPath).Hash
Invoke-BoundedScp $actorPath ('citastation@26.68.227.253:'+($root.Replace('\','/'))+'/prepare.ps1')
$launch=@'
$ErrorActionPreference='Stop';$root='__ROOT__';$id='__ID__'
if((Get-FileHash -LiteralPath "$root\prepare.ps1").Hash -ne '__ACTOR__'){throw 'Preparation actor differs'}
$info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe';$info.Arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$root+'\prepare.ps1"';$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
$child=[Diagnostics.Process]::Start($info);if(!$child){throw 'Preparation actor not started'}
try{
 [IO.File]::WriteAllText("$root\actor.json",(@{id=$id;pid=$child.Id;startedTicks=$child.StartTime.ToUniversalTime().Ticks;image=$info.FileName}|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
 $stdout=$child.StandardOutput.ReadToEndAsync();$stderr=$child.StandardError.ReadToEndAsync()
 if(!$child.WaitForExit(65000)){throw 'Preparation actor still running; observe the existing handle before further actions'}
 if(!$stdout.Wait(5000) -or !$stderr.Wait(5000)){throw 'Preparation actor output not complete'}
 if($child.ExitCode -ne 0){throw 'Preparation failed; inspect retained failure receipt'}
 $receipt=[IO.File]::ReadAllText("$root\result.json")|ConvertFrom-Json
 if($receipt.id -cne $id -or !$receipt.existingNativeUnchanged){throw 'Preparation not confirmed'}
 $receipt|ConvertTo-Json -Depth 8 -Compress
}finally{$child.Dispose()}
'@
$launch=$launch.Replace('__ROOT__',$root).Replace('__ID__',$id).Replace('__ACTOR__',$actorHash)
$result=(Invoke-BoundedRm $launch 80)|ConvertFrom-Json
if($result.id -cne $id -or !$result.existingNativeUnchanged -or !$result.protectedAgentUnchanged -or !$result.servicePidUnchanged){throw 'Preparation acceptance not confirmed'}
$result|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $workspace 'runtime/native-preparation-result.json') -Encoding utf8
$record.state='VerifiedRetained';$record|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $journal -Encoding utf8
Remove-Item -LiteralPath $actorPath -Force
[pscustomobject]@{id=$id;stage=$result.stage;files=@($result.inventory).Count;existingNativeUnchanged=$true;servicePidUnchanged=$true;scope=$result.scope}
