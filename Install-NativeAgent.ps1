param([Parameter(Mandatory)][string]$UserSid,[Parameter(Mandatory)][string]$UserProfile)
$ErrorActionPreference='Stop'
if(-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Protected agent setup needs Windows administrator approval'}
$null=[Security.Principal.SecurityIdentifier]::new($UserSid)
. (Join-Path $PSScriptRoot 'FileStorage.ps1');. (Join-Path $PSScriptRoot 'AgentPackage.ps1');. (Join-Path $PSScriptRoot 'AgentLease.ps1');. (Join-Path $PSScriptRoot 'NativeMaintenance.ps1')
Assert-AgentRuntimeReady $PSScriptRoot;Assert-NoAgentRun
if(Get-Process smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'Disconnect CITA routing before preparing the native service'}
$root=Join-Path $env:ProgramFiles 'RoutePilot\Agent';$data=Join-Path $env:ProgramData 'RoutePilot';$source=Join-Path $PSScriptRoot 'runtime\agent-build\RoutePilot.Agent.exe';if(-not(Test-Path -LiteralPath $source)){throw 'Verified agent image is missing'}
foreach($path in @((Split-Path $root),$root,$data)){for($part=$path;$part;$part=[IO.Path]::GetDirectoryName($part)){if((Test-Path -LiteralPath $part) -and (Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Protected service path is linked'}}}
$service=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'"
if($service){
 if($service.PathName -ine ('"'+(Join-Path $root 'RoutePilot.Agent.exe')+'" --service')){throw 'The existing service belongs to another protected installation'}
 $oldConfig=Read-JsonFile (Join-Path $root 'agent-config.json');if($oldConfig.UserSid -ne $UserSid -or [IO.Path]::GetFullPath($oldConfig.AppRoot).TrimEnd('\') -ine [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')){throw 'The service belongs to another Windows user or client installation'}
 try{$cap=Invoke-NativeMaintenanceRpc 'native-capabilities';if($cap.Native.setupProtocol -eq 1 -and (Get-FileHash (Join-Path $root 'RoutePilot.Agent.exe')).Hash -eq (Get-FileHash $source).Hash){return}}catch{}
 $boot=Join-Path $root 'startup-settings.json'
 if((Test-Path $boot) -and (Read-JsonFile $boot).Enabled){
  $retired=Invoke-NativeMaintenanceRpc 'boot-disable'
  if(-not $retired.Success -or (Read-JsonFile $boot).Enabled){throw 'Disconnect the previous boot route before native setup; the active route was not stopped.'}
 }
 Stop-NativeForMaintenance (Join-Path $root 'native')
 Invoke-AgentCoordinator 'begin-operation';$backup=Join-Path $root ('agent-native-backup-'+[guid]::NewGuid().ToString('N')+'.exe');$temporary=Join-Path $root ('agent-native-stage-'+[guid]::NewGuid().ToString('N')+'.exe');$saved=$false;$promoted=$false
 try{
  Copy-Item -LiteralPath $source -Destination $temporary;if((Get-FileHash $temporary).Hash -ne (Get-FileHash $source).Hash){throw 'Agent staging digest mismatch'}
  Stop-Service RoutePilotRescue;(Get-Service RoutePilotRescue).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(45))
  $previous=Get-Process -Id $service.ProcessId -ErrorAction SilentlyContinue;if($previous){try{if($previous.Path -ine (Join-Path $root 'RoutePilot.Agent.exe') -or -not $previous.WaitForExit(10000)){throw 'Previous protected agent has not exited'}}finally{$previous.Dispose()}}
  foreach($path in @($backup,$temporary,(Join-Path $root 'RoutePilot.Agent.exe'))){if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -ine $root){throw 'Agent transaction path escaped root'}}
  Move-Item -LiteralPath (Join-Path $root 'RoutePilot.Agent.exe') -Destination $backup;$saved=$true;Move-Item -LiteralPath $temporary -Destination (Join-Path $root 'RoutePilot.Agent.exe');$promoted=$true
  Start-Service RoutePilotRescue;(Get-Service RoutePilotRescue).WaitForStatus('Running',[TimeSpan]::FromSeconds(20));$cap=Invoke-NativeMaintenanceRpc 'native-capabilities';if($cap.Native.setupProtocol -ne 1){throw 'Updated agent does not advertise native setup'}
 }catch{if($saved){Stop-Service RoutePilotRescue -ErrorAction SilentlyContinue;(Get-Service RoutePilotRescue).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30));if($promoted){Remove-Item -LiteralPath (Join-Path $root 'RoutePilot.Agent.exe') -Force};Move-Item -LiteralPath $backup -Destination (Join-Path $root 'RoutePilot.Agent.exe')};if((Get-Service RoutePilotRescue).Status -eq 'Stopped'){Start-Service RoutePilotRescue};throw}
 finally{try{Invoke-AgentCoordinator 'end-operation'}catch{}}
 return
}
if(Test-Path -LiteralPath $root){if(@(Get-ChildItem -LiteralPath $root -Force).Count){throw 'A previous protected installation must be recovered before first use'}}
# Existing shared directories are inspected, never re-permissioned. Only a newly
# created RoutePilot data directory receives its own private security descriptor.
foreach($existing in @((Split-Path $root),$data)){
 if(-not(Test-Path -LiteralPath $existing)){continue};$acl=Get-Acl -LiteralPath $existing
 foreach($rule in $acl.Access){if($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly){continue};$sid=$rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value;$mutation=[Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership;if($rule.AccessControlType -eq 'Allow' -and ($rule.FileSystemRights -band $mutation) -and $sid -notin @('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')){throw 'An existing service directory is writable by an untrusted principal; permissions were not changed'}}
}
if(-not(Test-Path -LiteralPath (Split-Path $root))){New-Item -ItemType Directory -Path (Split-Path $root)|Out-Null}
if(-not(Test-Path -LiteralPath $data)){
 New-Item -ItemType Directory -Path $data|Out-Null;$acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false);$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'));foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))};$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($UserSid),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'));Set-Acl -LiteralPath $data -AclObject $acl
}
$config=Join-Path $PSScriptRoot ('runtime\native-agent-config-'+[guid]::NewGuid().ToString('N')+'.json');Write-JsonFile $config @{UserSid=$UserSid;UserProfile=[IO.Path]::GetFullPath($UserProfile);DataDirectory=$data;AppRoot=$PSScriptRoot}
try{Publish-AgentPackage @(@{Source=$source;Name='RoutePilot.Agent.exe'},@{Source=$config;Name='agent-config.json'}) $root {} {
 New-Service -Name RoutePilotRescue -BinaryPathName ('"'+(Join-Path $root 'RoutePilot.Agent.exe')+'" --service') -DisplayName 'CITA Route protected agent' -StartupType Automatic|Out-Null
 & (Join-Path $root 'RoutePilot.Agent.exe') --configure-service-recovery|Out-Null;if($LASTEXITCODE -ne 0){throw 'Service recovery configuration failed'};Start-Service RoutePilotRescue;(Get-Service RoutePilotRescue).WaitForStatus('Running',[TimeSpan]::FromSeconds(20))
 } -Restore { $current=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'";if($current -and $current.PathName -ieq ('"'+(Join-Path $root 'RoutePilot.Agent.exe')+'" --service')){Stop-Service RoutePilotRescue -ErrorAction SilentlyContinue;& sc.exe delete RoutePilotRescue|Out-Null} }
}finally{Remove-Item -LiteralPath $config -Force -ErrorAction SilentlyContinue}
