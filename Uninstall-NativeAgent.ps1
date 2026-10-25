param([Parameter(Mandatory)][string]$AppRoot,[Parameter(Mandatory)][string]$UserSid,[string]$AgentRoot=(Join-Path $env:ProgramFiles 'RoutePilot\Agent'))
$ErrorActionPreference='Stop'
if(-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Protected service removal requires Windows administrator approval'}
$AgentRoot=[IO.Path]::GetFullPath($AgentRoot).TrimEnd('\');$normal=Join-Path $env:ProgramFiles 'RoutePilot\Agent'
if($AgentRoot -ine $normal -and ([IO.Path]::GetDirectoryName($AgentRoot) -ine $env:ProgramFiles -or [IO.Path]::GetFileName($AgentRoot) -notmatch '^CitaNativeService-[a-f0-9]{32}$' -or (Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83')){throw 'Unexpected removal root'}
for($part=$AgentRoot;$part;$part=[IO.Path]::GetDirectoryName($part)){if((Test-Path -LiteralPath $part) -and (Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Removal refuses linked service paths'}}
$service=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'"
if(-not $service){return}
if($service.PathName -ine ('"'+(Join-Path $AgentRoot 'RoutePilot.Agent.exe')+'" --service')){throw 'Another service installation owns this name'}
$owner=Get-Content (Join-Path $AgentRoot 'agent-config.json') -Raw|ConvertFrom-Json
if($owner.UserSid -ne $UserSid -or [IO.Path]::GetFullPath($owner.AppRoot).TrimEnd('\') -ine [IO.Path]::GetFullPath($AppRoot).TrimEnd('\')){throw 'Another client owns the protected service'}
$native=Join-Path $AgentRoot 'native';if(-not(Test-Path -LiteralPath (Join-Path $native 'service.json'))){throw 'This remover is limited to native installations'}
$settings=Get-Content (Join-Path $native 'service.json') -Raw|ConvertFrom-Json;if($settings.Enabled -isnot [bool] -or -not $settings.Enabled){throw 'Repair the disabled native installation before protected removal'}
$ownedDirectories=@($native)+@(Get-ChildItem -LiteralPath $AgentRoot -Directory -Force|Where-Object Name -match '^native-(backup|failed|stage)-[a-f0-9]{32}$'|ForEach-Object FullName)
$ownedFiles=@(Get-ChildItem -LiteralPath $AgentRoot -File -Force|Where-Object Name -match '^agent-native-(backup|stage)-[a-f0-9]{32}\.exe$'|ForEach-Object FullName)
foreach($path in $ownedDirectories+$ownedFiles){if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -ine $AgentRoot){throw 'Owned removal path escaped agent root'};if((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Native removal path contains a link'}}
foreach($path in $ownedDirectories){foreach($item in @(Get-ChildItem -LiteralPath $path -Force -Recurse)){if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Native removal tree contains a link'}}}
$boot=Join-Path $AgentRoot 'startup-settings.json';if((Test-Path $boot) -and (Get-Content $boot -Raw|ConvertFrom-Json).Enabled){throw 'Disable the previous boot route before removal'}
. (Join-Path $PSScriptRoot 'NativeMaintenance.ps1');Stop-NativeForMaintenance $native
$null=Invoke-NativeMaintenanceRpc 'begin-operation';$deleted=$false
try{
 Stop-Service RoutePilotRescue;(Get-Service RoutePilotRescue).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(45))
 $process=Get-Process -Id $service.ProcessId -ErrorAction SilentlyContinue
 if($process){try{if($process.Path -ine (Join-Path $AgentRoot 'RoutePilot.Agent.exe') -or -not $process.WaitForExit(10000)){throw 'Stopped agent process has not exited'}}finally{$process.Dispose()}}
 & sc.exe delete RoutePilotRescue|Out-Null;if($LASTEXITCODE -ne 0){throw 'Protected service removal was refused'};$deleted=$true
 # Only the native subtree is recursively removed; unrelated protected files
 # and personal client data are not enumerated for deletion.
 if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($native)) -ine $AgentRoot){throw 'Native removal escaped agent root'}
 foreach($path in $ownedDirectories){Remove-Item -LiteralPath $path -Recurse -Force}
 foreach($path in $ownedFiles){Remove-Item -LiteralPath $path -Force}
 foreach($name in @('RoutePilot.Agent.exe','agent-config.json')){Remove-Item -LiteralPath (Join-Path $AgentRoot $name) -Force -ErrorAction Stop}
}finally{if(-not $deleted){if((Get-Service RoutePilotRescue).Status -eq 'Stopped'){Start-Service RoutePilotRescue};try{$null=Invoke-NativeMaintenanceRpc 'end-operation'}catch{}}}
