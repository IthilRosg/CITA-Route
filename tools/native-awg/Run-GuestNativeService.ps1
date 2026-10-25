$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$root='C:\CitaLab\NativeAwg';$run=[guid]::NewGuid().ToString('N');$folder=Join-Path ([Environment]::GetFolderPath('ProgramFiles')) ('CitaNativeService-'+$run);$native=Join-Path $folder 'native';$restore=Join-Path $root ('private\restore-service-'+$run+'.ps1');$report=@{schema=1;success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='Actual RoutePilotRescue SCM service with protected native snapshot; isolated W11'};$rescue=$null;$changed=$false;$created=$false;$old=$null;$phase='prepare'
function Rpc([string]$command,[string]$id='',[object]$profile=$null){
 $pipe=[IO.Pipes.NamedPipeClientStream]::new('.','RoutePilot.Rescue.v1',[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous,[Security.Principal.TokenImpersonationLevel]::Identification)
 try{$pipe.Connect(3000);$bytes=[Text.Encoding]::UTF8.GetBytes((@{Command=$command;RunId=$id;Profile=$profile}|ConvertTo-Json -Compress)+"`n");$pipe.Write($bytes,0,$bytes.Length);$pipe.Flush();$reader=[IO.StreamReader]::new($pipe,[Text.Encoding]::UTF8);$task=$reader.ReadLineAsync();if(-not $task.Wait(5000)){throw 'Native service reply deadline'};return ($task.GetAwaiter().GetResult()|ConvertFrom-Json)}finally{$pipe.Dispose()}
}
function Wait-Native([string]$state){
 for($i=0;$i -lt 150;$i++){try{$r=Rpc 'native-status';if($r.Native.phase -eq $state){return $r.Native};if($r.Native.phase -in @('RecoveryFailed','Unavailable')){$report.nativeFailure=$r.Native.errorCode;$report.nativeRuntime=$r.Native.runtime;$report.importStage=$r.Native.importStage;throw 'Native service failed'}}catch{if($report.nativeFailure){throw}};Start-Sleep -Milliseconds 400};throw ('Native state deadline: '+$state)
}
function Probe-Tun{
 $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $root 'AgentBackend\Cita.NativeAgentLab.exe'),('--wfp-os-probe '+$root));$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;$p=[Diagnostics.Process]::Start($info)
 try{$out=$p.StandardOutput.ReadToEndAsync();$err=$p.StandardError.ReadToEndAsync();if(-not $p.WaitForExit(15000)){$p.Kill();$p.WaitForExit();throw 'OS probe deadline'};$r=$out.GetAwaiter().GetResult()|ConvertFrom-Json;if($p.ExitCode -ne 0 -or -not $r.success -or $r.egressHash -ne $expected){throw 'Service OS proof failed'};return $true}finally{$p.Dispose()}
}
function Harden([string]$path){
 $admin=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544');$system=[Security.Principal.SecurityIdentifier]::new('S-1-5-18');$items=@(Get-Item -LiteralPath $path)+@(Get-ChildItem -LiteralPath $path -Recurse -Force)
 foreach($item in $items){if($item.PSIsContainer){$acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false);$acl.SetOwner($admin);foreach($sid in @($admin,$system)){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}}else{$acl=[Security.AccessControl.FileSecurity]::new();$acl.SetAccessRuleProtection($true,$false);$acl.SetOwner($admin);foreach($sid in @($admin,$system)){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','Allow'))}};Set-Acl -LiteralPath $item.FullName -AclObject $acl}
}
try{
 if(Get-Process native-awg-lab,native-core-lab,Cita.NativeAgentLab,smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'Guest must be idle'}
 if(Get-NetAdapter -Name Cita-NativeLab -IncludeHidden -ErrorAction SilentlyContinue){throw 'Existing lab TUN refused'}
 $old=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'";if(-not $old -or $old.StartName -ne 'LocalSystem'){throw 'Expected guest service missing'}
 $oldPath=$old.PathName;$oldRunning=$old.State -eq 'Running'
 $snapshot=if($oldRunning){Rpc 'status'}else{$null};if($snapshot.State.Active -or $snapshot.State.Phase -eq 5){throw 'Old rescue run must be idle'}
 New-Item -ItemType Directory -Path $native -ErrorAction Stop|Out-Null;$created=$true
 foreach($name in @('RoutePilot.Agent.exe','RoutePilot.Agent.dll','RoutePilot.Agent.deps.json','RoutePilot.Agent.runtimeconfig.json')){Copy-Item -LiteralPath (Join-Path $root ('ServiceCandidate\'+$name)) -Destination (Join-Path $folder $name)}
 foreach($pair in @(@('native-awg-lab.exe','native-owner.exe'),@('native-core-lab.exe','native-core.exe'),@('native-guard-lab.exe','native-guard.exe'))){Copy-Item -LiteralPath (Join-Path $root $pair[0]) -Destination (Join-Path $native $pair[1])}
 Copy-Item -LiteralPath (Join-Path $root 'private\peer.conf') -Destination (Join-Path $native 'peer.conf')
 $expected=(Get-Content 'C:\CitaLab\server-egress-reference.json' -Raw|ConvertFrom-Json).serverEgressHash
 $owner=[guid]::NewGuid().ToString('N');$profileText=Get-Content (Join-Path $native 'peer.conf') -Raw;if($profileText -notmatch '(?m)^Endpoint\s*=\s*([^\r\n]+)'){throw 'Pinned endpoint missing'};$endpointText=$Matches[1].Trim();if($endpointText -notmatch '^([0-9.]+):([0-9]+)$'){throw 'IPv4 endpoint required'};$peerAddress=[Net.IPAddress]::Parse($Matches[1]).ToString();$profileText=$null;$policy=@{Vpn=@('0.0.0.0/0','::/0');Direct=@('10.0.0.0/8','172.16.0.0/12','192.168.0.0/16','100.64.0.0/10','fc00::/7','fe80::/10','ff00::/8')};$policy|ConvertTo-Json -Depth 4|Set-Content (Join-Path $native 'policy.json') -Encoding UTF8
 $plan=@{Action='Plan';Run=$owner;Policy=$policy}|ConvertTo-Json -Depth 5 -Compress|& (Join-Path $native 'native-guard.exe')|ConvertFrom-Json;if(-not $plan.success){throw 'Policy compile failed'}
 $physical=@(Get-NetIPConfiguration|Where-Object {$_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up'});if($physical.Count -ne 1){throw 'One physical gateway required'}
 $core=@{log=@{disabled=$true};inbounds=@(@{type='tun';tag='native-tun';interface_name='Cita-NativeLab';address=@('172.30.127.1/30');route_address=@($plan.prefixes|Where-Object {$_ -notmatch ':'});route_exclude_address=@($peerAddress+'/32');auto_route=$true;strict_route=$false;dns_mode='disabled';stack='gvisor';mtu=1400});outbounds=@(@{type='cita_native_lab';tag='native-vpn';owner=$owner});route=@{final='native-vpn'}}
 [IO.File]::WriteAllText((Join-Path $native 'core.json'),($core|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
 function Entry([string]$name){return @{Name=$name;Sha256=(Get-FileHash (Join-Path $native $name)).Hash}}
 @{Schema=1;Owner=(Entry 'native-owner.exe');Core=(Entry 'native-core.exe');Guard=(Entry 'native-guard.exe');Policy=(Entry 'policy.json');Profile=(Entry 'peer.conf');CoreConfig=(Entry 'core.json');ExpectedExitHash=$expected}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $native 'native-catalog.json') -Encoding UTF8
 @{Enabled=$true;CatalogSha256=(Get-FileHash (Join-Path $native 'native-catalog.json')).Hash}|ConvertTo-Json|Set-Content (Join-Path $native 'service.json') -Encoding UTF8
 @{UserSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;UserProfile=$env:USERPROFILE;DataDirectory='C:\ProgramData\RoutePilot';AppRoot=$root}|ConvertTo-Json|Set-Content (Join-Path $folder 'agent-config.json') -Encoding UTF8
 Harden $folder
 $escapedOld=$oldPath.Replace("'","''");$escapedFolder=$folder.Replace("'","''");$wasRunning=if($oldRunning){'$true'}else{'$false'}
 $undo=@'
param([switch]$Delayed)
$ErrorActionPreference='Stop'
if($Delayed){Start-Sleep -Seconds 240}
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){exit 1}
$folder='__FOLDER__';$oldPath='__OLD__'
$service=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'"
if($service.PathName.Contains($folder)){
 Stop-Service RoutePilotRescue -Force -ErrorAction SilentlyContinue
 (Get-Service RoutePilotRescue).WaitForStatus('Stopped',[timespan]::FromSeconds(30))
 $intent=Join-Path $folder 'native\intent.json'
 if(Test-Path -LiteralPath $intent){$id=Get-Content -LiteralPath $intent -Raw|ConvertFrom-Json;if($id.Run -notmatch '^[a-f0-9]{32}$'){throw 'Invalid owned guard namespace'};$result=@{Action='Cleanup';Run=$id.Run}|ConvertTo-Json -Compress|& (Join-Path $folder 'native\native-guard.exe')|ConvertFrom-Json;if(-not $result.success){throw 'Owned WFP cleanup failed'}}
 $current=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'";$result=Invoke-CimMethod -InputObject $current -MethodName Change -Arguments @{PathName=$oldPath};if($result.ReturnValue -ne 0){throw 'Restore service path failed'}
 if(__RUNNING__){Start-Service RoutePilotRescue}
}
'@
 $undo=$undo.Replace('__FOLDER__',$escapedFolder).Replace('__OLD__',$escapedOld).Replace('__RUNNING__',$wasRunning);[IO.File]::WriteAllText($restore,$undo,[Text.UTF8Encoding]::new($true))
 $rescue=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$restore+'" -Delayed') -PassThru
 Stop-Service RoutePilotRescue -Force;(Get-Service RoutePilotRescue).WaitForStatus('Stopped',[timespan]::FromSeconds(20))
 $newPath='"'+(Join-Path $folder 'RoutePilot.Agent.exe')+'" --service';$result=Invoke-CimMethod -InputObject $old -MethodName Change -Arguments @{PathName=$newPath};if($result.ReturnValue -ne 0){throw 'Candidate service path failed'};$changed=$true
 Start-Service RoutePilotRescue;(Get-Service RoutePilotRescue).WaitForStatus('Running',[timespan]::FromSeconds(20))
 $phase='connect';$null=Rpc 'native-start';$active=Wait-Native 'Connected';$report.firstTraffic=Probe-Tun
 $phase='ui-ipc';$uiInfo=[Diagnostics.ProcessStartInfo]::new((Join-Path $root 'NativeUiLab\Cita.NativeUiLab.exe'),'--service-status C:\CitaLab\NativeAwg');$uiInfo.UseShellExecute=$false;$uiInfo.CreateNoWindow=$true;$uiInfo.RedirectStandardOutput=$true;$uiInfo.RedirectStandardError=$true;$ui=[Diagnostics.Process]::Start($uiInfo)
 try{$uiOut=$ui.StandardOutput.ReadToEndAsync();$uiErr=$ui.StandardError.ReadToEndAsync();if(-not $ui.WaitForExit(15000)){$ui.Kill();$ui.WaitForExit();throw 'Desktop IPC probe timeout'};if($ui.ExitCode -ne 0 -or -not ($uiOut.GetAwaiter().GetResult()|ConvertFrom-Json).success){throw 'Desktop IPC metadata probe failed'};$report.desktopIpcVerified=$true;$report.desktopClientSha256=(Get-FileHash (Join-Path $root 'NativeUiLab\RoutePilot.dll')).Hash}finally{$ui.Dispose()}
 $phase='core-recovery';$pid1=$active.runtime.corePid;$process=Get-Process -Id $pid1;if($process.Path -ne (Join-Path $native 'native-core.exe')){throw 'Unexpected core identity'};Stop-Process -Id $pid1 -Force
 for($i=0;$i -lt 75;$i++){$state=(Rpc 'native-status').Native;if($state.phase -eq 'Connected' -and $state.runtime.corePid -ne $pid1){break};Start-Sleep -Milliseconds 400}
 if($state.phase -ne 'Connected' -or $state.runtime.corePid -eq $pid1){throw 'Core recovery deadline'};$report.coreRecoveredTraffic=Probe-Tun
 $phase='service-restart';$oldOperation=$state.operation;$generation=$state.generation;$service=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'";$sp=Get-Process -Id $service.ProcessId;if($sp.Path -ne (Join-Path $folder 'RoutePilot.Agent.exe')){throw 'Unexpected service identity'}
 Stop-Process -Id $sp.Id -Force;Start-Sleep -Milliseconds 700;if((Get-Service RoutePilotRescue).Status -eq 'Stopped'){Start-Service RoutePilotRescue}
 $again=Wait-Native 'Connected';if($again.generation -le $generation -or $again.operation -eq $oldOperation){throw 'Service intent was not resumed'};$report.afterServiceRestartTraffic=Probe-Tun
 $phase='transport-owner-recovery';$previousOwner=$again.runtime.profile.processId;$process=Get-Process -Id $previousOwner;if($process.Path -ne (Join-Path $native 'native-owner.exe')){throw 'Unexpected transport owner'};$oldGeneration=$again.generation;Stop-Process -Id $previousOwner -Force;for($i=0;$i -lt 100;$i++){$candidate=(Rpc 'native-status').Native;if($candidate.phase -eq 'Connected' -and $candidate.generation -gt $oldGeneration){$again=$candidate;break};Start-Sleep -Milliseconds 400};if($again.generation -le $oldGeneration){throw 'Transport owner recovery deadline'};$report.transportOwnerRecoveredTraffic=Probe-Tun;$stale=Rpc 'native-stop' $oldOperation;if($stale.Success){throw 'Stale service Stop accepted'};$report.staleStopRefused=$true
 $phase='maintenance';. (Join-Path $root 'NativeMaintenance.ps1');Stop-NativeForMaintenance -NativeRoot $native
 $null=Wait-Native 'Disconnected';$report.maintenanceDisconnected=$true
 $lease=Rpc 'begin-operation';if(-not $lease.Success){throw 'Maintenance reservation refused'}
 try{if((Rpc 'native-start').Success){throw 'Start raced maintenance reservation'};$report.maintenanceBlocksReconnect=$true}finally{$null=Rpc 'end-operation'}
 $phase='disconnect';$report.disconnected=$true;Restart-Service RoutePilotRescue -Force;$disconnected=Wait-Native 'Disconnected';if($disconnected.desired){throw 'Manual disconnect lost after SCM restart'};$report.manualDisconnectRetainedAfterRestart=$true;$report.globalVpnPolicy=$true
 $phase='profile-import';. (Join-Path $root 'NativeImportAcceptance.ps1');Test-NativeImport
 if(Get-Process native-owner,native-core -ErrorAction SilentlyContinue){throw 'Native children remain after disconnect'}
 $report.ownerSha256=(Get-FileHash (Join-Path $native 'native-owner.exe')).Hash;$report.coreSha256=(Get-FileHash (Join-Path $native 'native-core.exe')).Hash;$report.agentSha256=(Get-FileHash (Join-Path $folder 'RoutePilot.Agent.dll')).Hash;$report.guardSha256=(Get-FileHash (Join-Path $native 'native-guard.exe')).Hash;$report.success=$true
}catch{$report.errorType=$_.Exception.GetType().Name;$report.failedPhase=$phase;$report.failedLine=$_.InvocationInfo.ScriptLineNumber;$report.errorId=$_.FullyQualifiedErrorId}
finally{
 if($changed){try{& $restore;$current=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'";$report.serviceRestored=[bool]($current.PathName -eq $oldPath)}catch{$report.serviceRestored=$false;$report.restoreErrorType=$_.Exception.GetType().Name;$report.success=$false}}
 else{$report.serviceRestored=$true}
 if($report.serviceRestored -and $rescue){if(-not $rescue.HasExited){Stop-Process -Id $rescue.Id -Force};$rescue.Dispose()}
 if($created -and $report.serviceRestored){$absolute=[IO.Path]::GetFullPath($folder);if([IO.Path]::GetDirectoryName($absolute) -ne [Environment]::GetFolderPath('ProgramFiles') -or [IO.Path]::GetFileName($absolute) -notmatch '^CitaNativeService-[a-f0-9]{32}$'){throw 'Candidate cleanup containment'};Remove-Item -LiteralPath $absolute -Recurse -Force;$report.candidateRemoved=[bool](-not (Test-Path -LiteralPath $absolute))}
 if(-not $report.serviceRestored -or -not $report.candidateRemoved){$report.success=$false}
 $report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root 'native-service-result.json') -Encoding UTF8
}
if(-not $report.success){exit 1}
