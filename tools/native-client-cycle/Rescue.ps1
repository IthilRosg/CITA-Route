[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunRoot,[ValidateSet('primary','backup')][string]$Role='primary')
$ErrorActionPreference='Stop'
if($Role -cnotin @('primary','backup')){throw 'Rescue role refused.'}
# Test-only exact-connection cleanup. The pinned single sender must be retired
# before ordered IPC can prove that an as-yet-unseen connection cannot start.
$ctx=$null;$lock=$null;$image=$null;$serviceProcess=$null
function Assert-CycleProtected([string]$path,[bool]$ancestor=$false){
 $item=Get-Item -LiteralPath $path -Force
 if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Protected path refused.'}
 $acl=Get-Acl -LiteralPath $path;$trusted=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
 if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted){throw 'Protected owner refused.'}
 $mutation=if($ancestor){0x500c0040}else{0x500d0156}
 foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
  if($rule.AccessControlType -eq 'Allow' -and -not ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and $rule.IdentityReference.Value -notin $trusted -and (([int64]$rule.FileSystemRights -band 0xffffffff) -band $mutation)){throw 'Protected ACL refused.'}
 }
}
function Save-CycleResult([string]$outcome,[string]$reason){
 $path=Join-Path $ctx.Root ('rescue-result-'+$Role+'.json');$temp=$path+'.'+[guid]::NewGuid().ToString('N')+'.new'
 $record=@{schema=1;run=$ctx.Run;role=$Role;connectionId=$ctx.ConnectionId;outcome=$outcome;reason=$reason}
 try{$bytes=[Text.Encoding]::UTF8.GetBytes(($record|ConvertTo-Json -Compress));$file=[IO.FileStream]::new($temp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough);try{$file.Write($bytes,0,$bytes.Length);$file.Flush($true)}finally{$file.Dispose()};[IO.File]::Move($temp,$path)}finally{if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}}
}
function Save-CycleArmed($plan,[DateTimeOffset]$deadline){
 if([DateTimeOffset]::UtcNow -ge $deadline){throw 'Cycle deadline elapsed before arming.'}
 $self=[Diagnostics.Process]::GetCurrentProcess()
 try{
  $path=Join-Path $ctx.Root ('rescue-armed-'+$Role+'.json');$temp=$path+'.'+[guid]::NewGuid().ToString('N')+'.new'
  $record=@{Run=$ctx.Run;Role=$Role;Pid=$self.Id;StartedUtcTicks=$self.StartTime.ToUniversalTime().Ticks;ConnectionId=$plan.ConnectionId;DeadlineUtc=$deadline.ToString('o')}
  try{$bytes=[Text.Encoding]::UTF8.GetBytes(($record|ConvertTo-Json -Compress));$file=[IO.FileStream]::new($temp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough);try{$file.Write($bytes,0,$bytes.Length);$file.Flush($true)}finally{$file.Dispose()};[IO.File]::Move($temp,$path)}finally{if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}}
 }finally{$self.Dispose()}
}
function Get-CycleAction($plan,$state,[string]$profile){
 if(!$state -or $state.Success -isnot [bool] -or !$state.Success -or !$state.Native -or $state.Native.desired -isnot [bool] -or ($state.Native.generation -isnot [int] -and $state.Native.generation -isnot [long])){throw 'Native status unavailable.'}
 if($state.Native.connectionId -cne $plan.ConnectionId){return 'NoSession'}
 if($profile -and $state.Native.profileId -cne $profile){return 'ProfileChanged'}
 if($state.Native.generation -le $plan.InitialGeneration){return 'GenerationNotAdvanced'}
 if($state.Native.desired){return 'Stop'}
 return 'Poll'
}
function Stop-CycleController($plan,[string]$root,[string]$id){
 $taskName='CitaNativeCycle-'+$id+'-Owner'
 if($plan.ControllerTask -cne $taskName -or !$plan.FilesSource -or $plan.FilesSource.'Controller.ps1' -cnotmatch '^[A-F0-9]{64}$'){throw 'Controller plan refused.'}
 $controllerPath=Join-Path $root 'Controller.ps1';Assert-CycleProtected $controllerPath
 if((Get-FileHash -LiteralPath $controllerPath -Algorithm SHA256).Hash -cne $plan.FilesSource.'Controller.ps1'){throw 'Controller digest differs.'}
 $serviceExe=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
 $task=Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
 $principal=[string]$task.Principal.UserId
 $sid=if($principal -match '^S-1-'){$principal}else{([Security.Principal.NTAccount]::new($principal)).Translate([Security.Principal.SecurityIdentifier]).Value}
 $configPath=Join-Path ([IO.Path]::GetDirectoryName($root)) 'agent-config.json';Assert-CycleProtected $configPath
 if((Get-Item -LiteralPath $configPath).Length -gt 16384){throw 'Agent config size refused.'}
 $config=[IO.File]::ReadAllText($configPath)|ConvertFrom-Json
 $arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$controllerPath+'" -RunRoot "'+$root+'"'
 if($sid -cne $config.UserSid -or [string]$task.Principal.LogonType -cne 'S4U' -or $task.Actions.Count -ne 1 -or $task.Actions[0].Execute -ine $serviceExe -or $task.Actions[0].Arguments -cne $arguments){throw 'Controller task identity differs.'}
 $descriptor=Join-Path $root 'controller.json';$held=$null
 try{
  if(Test-Path -LiteralPath $descriptor){
   Assert-CycleProtected $descriptor;$file=Get-Item -LiteralPath $descriptor
   if($file.Length -lt 1 -or $file.Length -gt 4096){throw 'Controller descriptor size refused.'}
   $identity=[IO.File]::ReadAllText($descriptor)|ConvertFrom-Json
   if($identity.Run -cne $id -or $identity.Pid -le 0 -or $identity.StartedUtcTicks -le 0 -or $identity.Image -ine $serviceExe){throw 'Controller descriptor differs.'}
   $held=Get-Process -Id $identity.Pid -ErrorAction SilentlyContinue
   if($held){$null=$held.Handle;if(!$held.HasExited -and ($held.StartTime.ToUniversalTime().Ticks -ne $identity.StartedUtcTicks -or $held.Path -ine $serviceExe)){throw 'Controller process identity differs.'}}
  }
  Disable-ScheduledTask -TaskName $taskName|Out-Null
  $disabled=Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
  if([string]$disabled.State -eq 'Running' -or ($held -and !$held.HasExited)){Stop-ScheduledTask -TaskName $taskName -ErrorAction Stop}
  if($held -and !$held.HasExited -and !$held.WaitForExit(5000)){throw 'Controller process exit unconfirmed.'}
  for($i=0;$i -lt 2;$i++){
   $current=Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
   $currentPrincipal=[string]$current.Principal.UserId
   $currentSid=if($currentPrincipal -match '^S-1-'){$currentPrincipal}else{([Security.Principal.NTAccount]::new($currentPrincipal)).Translate([Security.Principal.SecurityIdentifier]).Value}
   if([string]$current.State -cne 'Disabled' -or $currentSid -cne $config.UserSid -or [string]$current.Principal.LogonType -cne 'S4U' -or $current.Actions.Count -ne 1 -or $current.Actions[0].Execute -ine $serviceExe -or $current.Actions[0].Arguments -cne $arguments){throw 'Controller task retirement unconfirmed.'}
   $actors=@(Get-CimInstance Win32_Process|Where-Object {$_.ProcessId -ne $PID -and $_.Name -in @('powershell.exe','pwsh.exe') -and $_.CommandLine -and $_.CommandLine.IndexOf($controllerPath,[StringComparison]::OrdinalIgnoreCase) -ge 0})
   if($actors.Count){throw 'Cycle controller processes remain.'}
   if($i -eq 0){Start-Sleep -Milliseconds 200}
  }
 }finally{if($held){$held.Dispose()}}
}
function Read-CycleRpc([string]$command,[string]$id=''){
 if($command -cnotin @('native-capabilities','native-status','native-stop-session')){throw 'RPC scope refused.'}
 $service=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'" -ErrorAction Stop
 if(!$service -or $service.State -cne 'Running' -or $service.ProcessId -ne $ctx.ServicePid -or $service.StartName -cne 'LocalSystem' -or $service.ServiceType -cne 'Own Process' -or $service.PathName -ine ('"'+$ctx.Agent+'" --service') -or $serviceProcess.HasExited -or $serviceProcess.StartTime.ToUniversalTime().Ticks -ne $ctx.ServiceStart -or $serviceProcess.Path -ine $ctx.Agent){throw 'Service identity changed.'}
 $request=if($command -ceq 'native-stop-session'){@{Command=$command;ConnectionId=$id}}else{@{Command=$command}}
 $json=[Cita.ClientCyclePipe]::Exchange(($request|ConvertTo-Json -Compress),$ctx.ServicePid)
 $after=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'" -ErrorAction Stop
 if(!$after -or $after.State -cne 'Running' -or $after.ProcessId -ne $ctx.ServicePid -or $serviceProcess.HasExited -or $serviceProcess.StartTime.ToUniversalTime().Ticks -ne $ctx.ServiceStart){throw 'Service identity changed after RPC.'}
 return ($json|ConvertFrom-Json -ErrorAction Stop)
}
try{
 if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -cne 'S-1-5-18'){throw 'SYSTEM required.'}
 $agentRoot=[IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'RoutePilot\Agent')).TrimEnd('\')
 $root=[IO.Path]::GetFullPath($RunRoot).TrimEnd('\');$leaf=[IO.Path]::GetFileName($root)
 if([IO.Path]::GetDirectoryName($root) -ine $agentRoot -or $leaf -cnotmatch '^native-cycle-[a-f0-9]{32}$'){throw 'Cycle root refused.'}
 $id=$leaf.Substring('native-cycle-'.Length);Assert-CycleProtected $root
 for($ancestor=[IO.Directory]::GetParent($root);$ancestor;$ancestor=$ancestor.Parent){Assert-CycleProtected $ancestor.FullName $true}
 if([IO.Path]::GetFullPath($PSCommandPath) -ine (Join-Path $root 'Rescue.ps1')){throw 'Cycle script path refused.'};Assert-CycleProtected $PSCommandPath
 if((Get-CimInstance Win32_ComputerSystemProduct).UUID -cne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong station.'}
 $planPath=Join-Path $root 'plan.json';Assert-CycleProtected $planPath
 $planFile=[IO.FileStream]::new($planPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 try{if($planFile.Length -lt 1 -or $planFile.Length -gt 8192){throw 'Cycle plan size refused.'};$reader=[IO.StreamReader]::new($planFile);try{$plan=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}}finally{$planFile.Dispose()}
 $until=[DateTimeOffset]::MinValue
 if($plan.Schema -ne 1 -or $plan.Run -cne $id -or $plan.ConnectionId -cnotmatch '^[a-f0-9]{32}$' -or $plan.AgentSha -cnotmatch '^[A-F0-9]{64}$' -or $plan.InitialOperation -cnotmatch '^[a-f0-9]{32}$' -or ($plan.InitialGeneration -isnot [int] -and $plan.InitialGeneration -isnot [long]) -or $plan.InitialGeneration -lt 0 -or ![DateTimeOffset]::TryParse([string]$plan.DeadlineUtc,[ref]$until) -or $until.Offset -ne [TimeSpan]::Zero -or $until -gt [DateTimeOffset]::UtcNow.AddSeconds(120)){throw 'Cycle plan refused.'}
 $required=@('Controller.ps1','Rescue.ps1','NativeUpgradeTransaction.ps1','NativeUpgradeService.ps1');if($plan.GuiMode){$required+='Gui.ps1'}
 foreach($name in $required){
  $path=Join-Path $root $name;$pin=$plan.FilesSource.$name
  if($pin -cnotmatch '^[A-F0-9]{64}$'){throw 'Cycle source pin refused.'}
  Assert-CycleProtected $path;if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $pin){throw 'Cycle source differs.'}
 }
 if($plan.GuiMode){. (Join-Path $root 'Gui.ps1');Assert-GuiCyclePlan $plan $root}
 $profile='';if($plan.PSObject.Properties['ProfileSha']){$profile=[string]$plan.ProfileSha;if($profile -and $profile -cnotmatch '^[A-F0-9]{64}$'){throw 'Profile digest refused.'}}
 $ctx=[pscustomobject]@{Root=$root;Run=$id;ConnectionId=$plan.ConnectionId;ServicePid=0;ServiceStart=0;Agent=(Join-Path $agentRoot 'RoutePilot.Agent.exe')}
 if(Test-Path -LiteralPath (Join-Path $root ('rescue-result-'+$Role+'.json'))){return}
 Assert-CycleProtected $ctx.Agent;$image=[IO.FileStream]::new($ctx.Agent,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 $sha=[Security.Cryptography.SHA256]::Create();try{$digest=([BitConverter]::ToString($sha.ComputeHash($image))).Replace('-','')}finally{$sha.Dispose()};if($digest -cne $plan.AgentSha){throw 'Agent digest differs.'}
 $service=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'" -ErrorAction Stop
 if(!$service -or $service.State -cne 'Running' -or $service.ProcessId -le 0 -or $service.StartName -cne 'LocalSystem' -or $service.ServiceType -cne 'Own Process' -or $service.PathName -ine ('"'+$ctx.Agent+'" --service')){throw 'Service identity refused.'}
 $ctx.ServicePid=[int]$service.ProcessId;$serviceProcess=Get-Process -Id $ctx.ServicePid -ErrorAction Stop;$null=$serviceProcess.Handle
 if($serviceProcess.HasExited -or $serviceProcess.Path -ine $ctx.Agent){throw 'Service process refused.'};$ctx.ServiceStart=$serviceProcess.StartTime.ToUniversalTime().Ticks
 Add-Type -TypeDefinition @'
using System;using System.IO;using System.IO.Pipes;using System.Runtime.InteropServices;using System.Security.Principal;using System.Text;using System.Threading.Tasks;
namespace Cita { public static class ClientCyclePipe {
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetNamedPipeServerProcessId(IntPtr pipe,out uint pid);
 public static string Exchange(string request,int expected) {
  using(var pipe=new NamedPipeClientStream(".","RoutePilot.Rescue.v1",PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification)){
   pipe.Connect(5000);uint pid;if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out pid)||pid!=(uint)expected)throw new IOException("Pipe identity refused.");
   var task=Task.Run(async delegate {byte[] b=Encoding.UTF8.GetBytes(request+"\n");await pipe.WriteAsync(b,0,b.Length);await pipe.FlushAsync();using(var result=new MemoryStream()){byte[] one=new byte[1];while(true){if(await pipe.ReadAsync(one,0,1)!=1)throw new IOException("Pipe closed.");if(one[0]==10)return new UTF8Encoding(false,true).GetString(result.ToArray());if(result.Length>=65536)throw new IOException("Reply too large.");result.WriteByte(one[0]);}}});
   try{if(!task.Wait(5000))throw new IOException("Pipe deadline.");return task.GetAwaiter().GetResult();}finally{pipe.Dispose();if(!task.IsCompleted)task.ContinueWith(t=>{var error=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);}
  }
 }
}}
'@
 $caps=Read-CycleRpc 'native-capabilities'
 if($caps.Success -isnot [bool] -or !$caps.Success -or ($caps.Native.connectionProtocol -isnot [int] -and $caps.Native.connectionProtocol -isnot [long]) -or $caps.Native.connectionProtocol -ne 1){throw 'Connection protocol unavailable.'}
 Save-CycleArmed $plan $until
 while([DateTimeOffset]::UtcNow -lt $until){
  $request=Join-Path $root 'cleanup-request.json'
  if(Test-Path -LiteralPath $request){Assert-CycleProtected $request;if((Get-Item -LiteralPath $request).Length -gt 1024){throw 'Cleanup request too large.'};$value=[IO.File]::ReadAllText($request)|ConvertFrom-Json;if($value.Run -cne $id){throw 'Cleanup request differs.'};break}
  Start-Sleep -Milliseconds 200
 }
 $lockTimer=[Diagnostics.Stopwatch]::StartNew()
 while(!$lock){
  try{$lock=[IO.FileStream]::new((Join-Path $root 'rescue.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
  catch [IO.IOException]{if($lockTimer.Elapsed.TotalSeconds -ge 65){throw 'Rescue serialization deadline exceeded.'};Start-Sleep -Milliseconds 200}
 }
 Stop-CycleController $plan $root $id
 if($plan.GuiMode){Stop-GuiCycleSender $plan $root}
 $state=Read-CycleRpc 'native-status'
 if($plan.GuiMode){
  $guiId=Resolve-GuiCycleConnection $plan $root $state
  if(!$guiId){Save-CycleResult 'NoSession' 'GuiSenderRetiredAndStatusDrained';return}
  $plan.ConnectionId=$guiId;$ctx.ConnectionId=$guiId
 }
 $action=Get-CycleAction $plan $state $profile
 if($action -ceq 'NoSession'){Save-CycleResult 'NoSession' 'ControllerRetiredAndStatusDrained';return}
 if($action -notin @('Stop','Poll')){Save-CycleResult 'Attention' $action;return}
 if($action -ceq 'Stop'){
  $stopped=Read-CycleRpc 'native-stop-session' $plan.ConnectionId
  if($stopped.Success -isnot [bool] -or !$stopped.Success){throw 'Connection stop refused.'}
 }
 $timer=[Diagnostics.Stopwatch]::StartNew()
 while($timer.Elapsed.TotalSeconds -lt 35){
  $state=Read-CycleRpc 'native-status'
  if($timer.Elapsed.TotalSeconds -ge 45){throw 'Cleanup deadline exceeded.'}
  if($state.Success -isnot [bool] -or !$state.Success -or !$state.Native -or $state.Native.connectionId -cne $plan.ConnectionId -or ($profile -and $state.Native.profileId -cne $profile)){throw 'Connection changed during cleanup.'}
  if($state.Native.desired -isnot [bool]){throw 'Native desired state invalid.'}
  if(!$state.Native.desired -and $state.Native.phase -ceq 'Disconnected'){Save-CycleResult 'Stopped' 'ExactConnection';return}
  Start-Sleep -Milliseconds 200
 }
 Save-CycleResult 'Attention' 'StopUnconfirmed'
}catch{
 if($ctx){try{Save-CycleResult 'Attention' 'ValidationOrServiceFailure'}catch{}}
 exit 1
}finally{if($serviceProcess){$serviceProcess.Dispose()};if($image){$image.Dispose()};if($lock){$lock.Dispose()}}
