# Physical acceptance only. The installed application and service are unchanged.
function Open-GuiFixtureFile([string]$Path,[bool]$Create){
 if(!('Cita.GuiFixtureFile' -as [type])){Add-Type -TypeDefinition @'
using System;using System.IO;using System.ComponentModel;using System.Runtime.InteropServices;using Microsoft.Win32.SafeHandles;
namespace Cita { public static class GuiFixtureFile {
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetFileInformationByHandle(SafeFileHandle file,int kind,ref byte value,uint size);
 public static FileStream Open(string path,bool create){var h=CreateFileW(path,0xC0010000,0,IntPtr.Zero,create?1u:3u,0x80,IntPtr.Zero);if(h.IsInvalid){int e=Marshal.GetLastWin32Error();h.Dispose();throw new Win32Exception(e);}try{return new FileStream(h,FileAccess.ReadWrite);}catch{h.Dispose();throw;}}
 public static void Delete(FileStream file){byte value=1;if(!SetFileInformationByHandle(file.SafeFileHandle,4,ref value,1))throw new Win32Exception(Marshal.GetLastWin32Error());}
}}
'@}
 return [Cita.GuiFixtureFile]::Open($Path,$Create)
}
function Read-GuiFixtureBytes($File){
 if($File.Length -gt 16384){throw 'Preference fixture too large.'};$File.Position=0;$bytes=New-Object byte[] ([int]$File.Length);$used=0
 while($used -lt $bytes.Length){$read=$File.Read($bytes,$used,$bytes.Length-$used);if(!$read){throw 'Preference fixture read incomplete.'};$used+=$read}
 return [Convert]::ToBase64String($bytes)
}
function Write-GuiFixtureBytes($File,[string]$Base64){$bytes=[Convert]::FromBase64String($Base64);$File.Position=0;$File.SetLength(0);$File.Write($bytes,0,$bytes.Length);$File.Flush($true)}
function Get-GuiPreferenceRollback($Entry,[string]$Current){
 if($Current -ceq $Entry.ProposedBase64 -or ($Entry.Existed -and $Current -ceq $Entry.OriginalBase64)){return @{Remove=(!$Entry.Existed);Base64=$Entry.OriginalBase64;Merged=$false}}
 $currentValue=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Current)).TrimStart([char]0xfeff)|ConvertFrom-Json
 $proposed=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Entry.ProposedBase64))|ConvertFrom-Json
 $original=if($Entry.Existed){[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Entry.OriginalBase64)).TrimStart([char]0xfeff)|ConvertFrom-Json}else{[pscustomobject]@{}}
 if($currentValue -isnot [pscustomobject]){throw 'Preference object changed.'}
 $keys=if($Entry.Name -ceq 'preferences.json'){@('ConnectOnLaunch','StartupBehaviorVersion')}else{@('Selected','ConfirmedProfile')}
 foreach($key in $keys){
  $c=$currentValue.PSObject.Properties[$key];$o=$original.PSObject.Properties[$key];$p=$proposed.PSObject.Properties[$key]
  $actual=$c.Value|ConvertTo-Json -Compress;$expected=$p.Value|ConvertTo-Json -Compress;$prior=$o.Value|ConvertTo-Json -Compress
  if($c -and $actual -ceq $expected){if($o){$currentValue|Add-Member -NotePropertyName $key -NotePropertyValue $o.Value -Force}else{$currentValue.PSObject.Properties.Remove($key)}}
  elseif(!(($o -and $c -and $actual -ceq $prior) -or (!$o -and !$c))){throw 'A test-owned preference changed outside the fixture.'}
 }
 $remove=!$Entry.Existed -and @($currentValue.PSObject.Properties).Count -eq 0
 return @{Remove=$remove;Base64=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($currentValue|ConvertTo-Json -Depth 6 -Compress)));Merged=$true}
}
function Save-GuiCycleRecord([string]$Root,[string]$Name,$Value){
 $path=Join-Path $Root $Name;$temp=$path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
 try{
  $bytes=[Text.Encoding]::UTF8.GetBytes(($Value|ConvertTo-Json -Depth 6 -Compress));$f=[IO.FileStream]::new($temp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough);try{$f.Write($bytes,0,$bytes.Length);$f.Flush($true)}finally{$f.Dispose()}
  $acl=[Security.AccessControl.FileSecurity]::new();$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'));$acl.SetAccessRuleProtection($true,$false)
  foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','Allow'))};Set-Acl -LiteralPath $temp -AclObject $acl
  if([IO.File]::Exists($path)){[IO.File]::Replace($temp,$path,[NullString]::Value)}else{[IO.File]::Move($temp,$path)}
 }finally{if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}}
}
function Get-GuiCycleActors($Plan){
 $image=Join-Path $Plan.AppRoot 'RoutePilot.exe';$tag='--cita-lab-id='+$Plan.Run
 return @(Get-CimInstance Win32_Process -Filter "Name='RoutePilot.exe'"|Where-Object {$_.ExecutablePath -ieq $image -and $_.CommandLine -and $_.CommandLine.EndsWith($tag,[StringComparison]::Ordinal)})
}
function Assert-GuiCyclePlan($Plan,[string]$Root){
 if($Plan.Run -cnotmatch '^[a-f0-9]{32}$' -or $Plan.AppRoot -cne 'C:\Users\CITASTATION\AppData\Local\Programs\CITA Route\client' -or $Plan.GuiSession -ne 2 -or $Plan.AppSha -cnotmatch '^[A-F0-9]{64}$'){throw 'GUI plan refused.'}
 if((Get-FileHash -LiteralPath (Join-Path $Plan.AppRoot 'RoutePilot.exe')).Hash -cne $Plan.AppSha){throw 'GUI image differs.'}
 if(@(Get-CimInstance Win32_Process -Filter "Name='RoutePilot.exe'").Count){throw 'An existing GUI must not be disturbed.'}
 if(!(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'"|Where-Object {$_.SessionId -eq $Plan.GuiSession})){throw 'Interactive desktop unavailable.'}
}
function Save-GuiCycleActor($Plan,[string]$Root,$Actor){
 if((Get-FileHash -LiteralPath (Join-Path $Plan.AppRoot 'RoutePilot.exe')).Hash -cne $Plan.AppSha){throw 'GUI sender image changed.'}
 $p=Get-Process -Id $Actor.ProcessId -ErrorAction Stop
 try{
  $null=$p.Handle;if($p.HasExited -or $p.Path -ine (Join-Path $Plan.AppRoot 'RoutePilot.exe') -or $p.SessionId -ne $Plan.GuiSession){throw 'GUI process changed.'}
  $value=@{Run=$Plan.Run;Pid=$p.Id;StartedUtcTicks=$p.StartTime.ToUniversalTime().Ticks;ImageSha=$Plan.AppSha;Session=$p.SessionId}
  $path=Join-Path $Root 'gui-process.json'
  if(Test-Path -LiteralPath $path){
   $old=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
   if($old.Run -cne $value.Run -or $old.ImageSha -cne $value.ImageSha){throw 'GUI descriptor changed.'}
   if($old.Pid -eq $value.Pid -and $old.StartedUtcTicks -eq $value.StartedUtcTicks){return}
   $previous=Get-Process -Id $old.Pid -ErrorAction SilentlyContinue
   try{if($previous -and !$previous.HasExited -and $previous.StartTime.ToUniversalTime().Ticks -eq $old.StartedUtcTicks){throw 'Previous GUI sender still lives.'}}finally{if($previous){$previous.Dispose()}}
   $historyPath=Join-Path $Root 'gui-senders.json';[object[]]$senders=if(Test-Path -LiteralPath $historyPath){@((Get-Content -LiteralPath $historyPath -Raw|ConvertFrom-Json).Senders)}else{@($old)}
   if($senders.Count -ge 2){throw 'GUI relaunch budget exceeded.'};$senders+=,$value
   Save-GuiCycleRecord $Root 'gui-senders.json' @{Run=$Plan.Run;Senders=$senders}
  }
  Save-GuiCycleRecord $Root 'gui-process.json' $value
 }finally{$p.Dispose()}
}
function Set-GuiCyclePreferences($Plan,[string]$Root){
 $entries=@()
 foreach($name in @('preferences.json','transport-choice.json')){
  $path=Join-Path $Plan.AppRoot ('data\'+$name);$exists=[IO.File]::Exists($path)
  $beforeBase64=if($exists){$file=Open-GuiFixtureFile $path $false;try{Read-GuiFixtureBytes $file}finally{$file.Dispose()}}else{''}
  $before=if($exists){[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($beforeBase64)).TrimStart([char]0xfeff)}else{$null}
  $value=if($before){$before|ConvertFrom-Json}else{[pscustomobject]@{}}
  if($name -ceq 'preferences.json'){$value|Add-Member -NotePropertyName ConnectOnLaunch -NotePropertyValue $true -Force;$value|Add-Member -NotePropertyName StartupBehaviorVersion -NotePropertyValue 1 -Force}
  else{$value|Add-Member -NotePropertyName Selected -NotePropertyValue $true -Force;$value|Add-Member -NotePropertyName ConfirmedProfile -NotePropertyValue $Plan.ProfileSha -Force}
  $after=$value|ConvertTo-Json -Depth 6 -Compress
  $entries+=@{Name=$name;Existed=$exists;OriginalBase64=$beforeBase64;ProposedBase64=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($after))}
 }
 Save-GuiCycleRecord $Root 'gui-preferences.json' @{Run=$Plan.Run;Entries=$entries}
 foreach($entry in $entries){
  $path=Join-Path $Plan.AppRoot ('data\'+$entry.Name);$file=Open-GuiFixtureFile $path (!$entry.Existed)
  try{if((Read-GuiFixtureBytes $file) -cne $entry.OriginalBase64){throw 'Preference changed before fixture installation.'};Write-GuiFixtureBytes $file $entry.ProposedBase64}finally{$file.Dispose()}
 }
}
function Restore-GuiCyclePreferences($Plan,[string]$Root){
 $path=Join-Path $Root 'gui-preferences.json';if(!(Test-Path -LiteralPath $path)){return}
 $record=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json;if($record.Run -cne $Plan.Run -or @($record.Entries).Count -ne 2){throw 'Preference record refused.'}
 $held=[Collections.Generic.List[object]]::new()
 try{
  foreach($entry in $record.Entries){
   if($entry.Name -cnotin @('preferences.json','transport-choice.json')){throw 'Preference path refused.'}
   $target=Join-Path $Plan.AppRoot ('data\'+$entry.Name)
   if(!$entry.Existed -and ![IO.File]::Exists($target)){continue}
   $file=Open-GuiFixtureFile $target $false;$item=@{File=$file;Entry=$entry};$held.Add($item);$current=Read-GuiFixtureBytes $file
   $item.Rollback=Get-GuiPreferenceRollback $entry $current
  }
  foreach($item in $held){if($item.Rollback.Remove){[Cita.GuiFixtureFile]::Delete($item.File)}else{Write-GuiFixtureBytes $item.File $item.Rollback.Base64}}
 }finally{foreach($item in $held){$item.File.Dispose()}}
 Save-GuiCycleRecord $Root 'gui-preferences-restored.json' @{Run=$Plan.Run;Restored=$true;UnrelatedFieldsPreserved=$true;Merged=@($held|Where-Object {$_.Rollback.Merged}).Count}
}
function Stop-GuiCycleSender($Plan,[string]$Root){
 $name='CitaNativeCycle-'+$Plan.Run+'-Gui';$task=Get-ScheduledTask -TaskName $name -ErrorAction Stop;$sid=[string]$task.Principal.UserId
 if($sid -notmatch '^S-1-'){$sid=([Security.Principal.NTAccount]::new($sid)).Translate([Security.Principal.SecurityIdentifier]).Value}
 if($sid -cne 'S-1-5-21-226018510-1244436870-2661740948-1001' -or [string]$task.Principal.LogonType -cne 'Interactive' -or [string]$task.Principal.RunLevel -cne 'Limited' -or $task.Actions.Count -ne 1 -or $task.Actions[0].Execute -ine (Join-Path $Plan.AppRoot 'RoutePilot.exe') -or $task.Actions[0].Arguments -cne ('--cita-lab-id='+$Plan.Run)){throw 'GUI task identity differs.'}
 $actors=@(Get-GuiCycleActors $Plan);if($actors.Count -gt 1){throw 'Multiple GUI senders.'};foreach($actor in $actors){Save-GuiCycleActor $Plan $Root $actor}
 Disable-ScheduledTask -TaskName $name|Out-Null
 for($i=0;$i -lt 50;$i++){
  $actors=@(Get-GuiCycleActors $Plan)
  foreach($actor in $actors){Save-GuiCycleActor $Plan $Root $actor;$p=Get-Process -Id $actor.ProcessId -ErrorAction Stop;try{$null=$p.Handle;if(!$p.HasExited){$p.Kill();$null=$p.WaitForExit(5000)}}finally{$p.Dispose()}}
  Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
  if(!(Get-GuiCycleActors $Plan).Count -and [string](Get-ScheduledTask -TaskName $name).State -ceq 'Disabled'){Start-Sleep -Milliseconds 200;if(!(Get-GuiCycleActors $Plan).Count){return}}
  Start-Sleep -Milliseconds 100
 };throw 'GUI sender retirement unconfirmed.'
}
function Resolve-GuiCycleConnection($Plan,[string]$Root,$State){
 $descriptor=Join-Path $Root 'gui-process.json';$path=Join-Path $Plan.AppRoot 'runtime\native-launch-request.json'
 if(!(Test-Path -LiteralPath $descriptor) -or !(Test-Path -LiteralPath $path)){
  throw 'GUI request identity unknown.'
 }
 if((Get-Item -LiteralPath $path).Length -gt 4096){throw 'GUI request receipt too large.'}
 $senders=@(Get-Content -LiteralPath $descriptor -Raw|ConvertFrom-Json);$historyPath=Join-Path $Root 'gui-senders.json'
 if(Test-Path -LiteralPath $historyPath){$history=Get-Content -LiteralPath $historyPath -Raw|ConvertFrom-Json;if($history.Run -cne $Plan.Run -or @($history.Senders).Count -gt 2){throw 'GUI sender history differs.'};$senders=@($history.Senders)}
 $receipt=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
 $matching=@($senders|Where-Object {$_.Pid -eq $receipt.processId -and $_.StartedUtcTicks -eq $receipt.processStartedUtcTicks});if($matching.Count -ne 1){throw 'GUI request sender not found.'};$sender=$matching[0]
 if($sender.Run -cne $Plan.Run -or $sender.ImageSha -cne $Plan.AppSha -or $receipt.schema -ne 1 -or $receipt.processId -ne $sender.Pid -or $receipt.processStartedUtcTicks -ne $sender.StartedUtcTicks -or $receipt.initialOperation -cne $Plan.InitialOperation -or $receipt.initialGeneration -ne $Plan.InitialGeneration -or $receipt.profileId -cne $Plan.ProfileSha -or $receipt.connectionId -cnotmatch '^[a-f0-9]{32}$'){throw 'GUI request receipt differs.'}
 Save-GuiCycleRecord $Root 'gui-connection.json' @{Run=$Plan.Run;ConnectionId=$receipt.connectionId;Pid=$sender.Pid;StartedUtcTicks=$sender.StartedUtcTicks;ProfileSha=$Plan.ProfileSha;InitialOperation=$Plan.InitialOperation;InitialGeneration=$Plan.InitialGeneration}
 return [string]$receipt.connectionId
}
function Invoke-GuiCycle($Context,$Rescues){
 $plan=$Context.Plan;$root=$Context.Root
 Set-GuiCyclePreferences $plan $root
 Start-ScheduledTask -TaskName ('CitaNativeCycle-'+$plan.Run+'-Gui')
 $clock=[Diagnostics.Stopwatch]::StartNew();$n=$null
 while($clock.Elapsed.TotalSeconds -lt 60){
  if(@($Rescues|Where-Object {$_.HasExited}).Count){throw 'GUI rescue lost.'}
  $actors=@(Get-GuiCycleActors $plan);if($actors.Count -gt 1){throw 'Multiple GUI senders.'};foreach($actor in $actors){Save-GuiCycleActor $plan $root $actor}
  $reply=Call-CycleAgent 'native-status';$n=$reply.Native
  if($n.phase -ceq 'Connected' -and $n.runtime.coreState -ceq 'Active' -and $n.desired){$cid=Resolve-GuiCycleConnection $plan $root $reply;if(!$cid -or $n.connectionId -cne $cid -or $n.profileId -cne $plan.ProfileSha -or $n.generation -le $plan.InitialGeneration){throw 'GUI session is not attributable.'};break}
  if($n.desired -and $n.phase -ceq 'RecoveryFailed'){throw 'GUI connection failed.'}
  Start-Sleep -Milliseconds 200
 }
 if(!$n -or $n.phase -cne 'Connected'){throw 'GUI autoconnect deadline.'}
 $launch=Join-Path $plan.AppRoot 'runtime\launch-connect.json';$value=Get-Content -LiteralPath $launch -Raw|ConvertFrom-Json
 for($i=0;$i -lt 100 -and $value.phase -cne 'Running';$i++){Start-Sleep -Milliseconds 200;$value=Get-Content -LiteralPath $launch -Raw|ConvertFrom-Json}
 Write-ControllerRecord 'gui-startup-observation.json' @{Run=$plan.Run;ConnectionId=$cid;ServicePhase=$n.phase;CoreState=$n.runtime.coreState;LaunchPhase=$value.phase;ElapsedMs=$clock.ElapsedMilliseconds}
 if($value.phase -cne 'Running'){throw 'GUI did not confirm automatic initialization.'}
 $connectMs=$clock.ElapsedMilliseconds;$firstGeneration=$n.generation
 $requestPath=Join-Path $plan.AppRoot 'runtime\native-launch-request.json';$requestSha=(Get-FileHash -LiteralPath $requestPath).Hash
 # Close the ordinary window process while leaving its service-owned route.
 # Reopening with ConnectOnLaunch must attach to it, never turn it into Stop.
 Stop-GuiCycleSender $plan $root
 $state=Call-CycleAgent 'native-status';if(!$state.Native.desired -or $state.Native.connectionId -cne $cid){throw 'Closing the GUI changed its route.'}
 $task='CitaNativeCycle-'+$plan.Run+'-Gui';Enable-ScheduledTask -TaskName $task|Out-Null;Start-ScheduledTask -TaskName $task
 $relaunch=[Diagnostics.Stopwatch]::StartNew();$fresh=$false
 while($relaunch.Elapsed.TotalSeconds -lt 30){
  if(@($Rescues|Where-Object {$_.HasExited}).Count){throw 'GUI rescue lost during relaunch.'}
  $actors=@(Get-GuiCycleActors $plan);foreach($actor in $actors){Save-GuiCycleActor $plan $root $actor}
  if($actors.Count -eq 1){
   $sender=Get-Content -LiteralPath (Join-Path $root 'gui-process.json') -Raw|ConvertFrom-Json
   $value=Get-Content -LiteralPath $launch -Raw|ConvertFrom-Json
   if($value.phase -ceq 'Running' -and ([DateTimeOffset]::Parse([string]$value.updatedUtc)).UtcDateTime.Ticks -ge $sender.StartedUtcTicks){$fresh=$true;break}
  }
  Start-Sleep -Milliseconds 200
 }
 $state=Call-CycleAgent 'native-status'
 if(!$fresh -or !$state.Native.desired -or $state.Native.phase -cne 'Connected' -or $state.Native.runtime.coreState -cne 'Active' -or $state.Native.connectionId -cne $cid -or (Get-FileHash -LiteralPath $requestPath).Hash -cne $requestSha){throw 'GUI relaunch changed the connection or its request.'}
 Write-ControllerRecord 'connected-result.json' @{Run=$plan.Run;ConnectionId=$cid;Generation=$state.Native.generation;FirstGeneration=$firstGeneration;GuiAutoconnect=$true;NormalWindowLaunch=$true;LaunchPhase=$value.phase;ConnectMs=$connectMs;ProfilePreserved=$true;RelaunchSameConnection=$true;RelaunchRequestUnchanged=$true;RelaunchMs=$relaunch.ElapsedMilliseconds}
}
