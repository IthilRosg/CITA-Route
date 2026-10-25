param([Parameter(Mandatory)][string]$Package,[Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$Sha256,[string]$AgentRoot=(Join-Path $env:ProgramFiles 'RoutePilot\Agent'),[switch]$LabFailAfterSwap)
$ErrorActionPreference='Stop'
if(-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Native component setup requires administrator approval.'}
$AgentRoot=[IO.Path]::GetFullPath($AgentRoot).TrimEnd('\');$normal=Join-Path $env:ProgramFiles 'RoutePilot\Agent'
if($AgentRoot -ine $normal){if([IO.Path]::GetDirectoryName($AgentRoot) -ine $env:ProgramFiles -or [IO.Path]::GetFileName($AgentRoot) -notmatch '^CitaNativeService-[a-f0-9]{32}$' -or (Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Unexpected protected agent location'}}
if($LabFailAfterSwap -and $AgentRoot -ieq $normal){throw 'Fault injection is allowed only in the exact isolated guest fixture'}
for($part=$AgentRoot;$part;$part=[IO.Path]::GetDirectoryName($part)){if((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Native setup refuses linked paths'}}
$service=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'";if(-not $service -or $service.PathName -ine ('"'+(Join-Path $AgentRoot 'RoutePilot.Agent.exe')+'" --service')){throw 'Native setup does not own this service path'}
. (Join-Path $PSScriptRoot 'NativeMaintenance.ps1');. (Join-Path $PSScriptRoot 'AgentLease.ps1')
$capability=Invoke-NativeMaintenanceRpc 'native-capabilities';if($capability.Native.setupProtocol -ne 1){throw 'Update the protected CITA service before preparing native components'}
$wasRunning=(Get-Service RoutePilotRescue).Status -eq 'Running';$stage=Join-Path $AgentRoot ('native-stage-'+[guid]::NewGuid().ToString('N'));$target=Join-Path $AgentRoot 'native';$backup=Join-Path $AgentRoot ('native-backup-'+[guid]::NewGuid().ToString('N'));$reserved=$false;$changed=$false;$previous=$false;$success=$false;$stopped=$false;$configured=$false;$archiveStream=$null;$zip=$null
foreach($path in @($stage,$target,$backup)){if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -ine $AgentRoot){throw 'Native transaction path escaped agent root'};if((Test-Path -LiteralPath $path) -and (Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Native transaction refuses linked directories'}}
$startupPath=Join-Path $AgentRoot 'startup-settings.json';if((Test-Path -LiteralPath $startupPath) -and (Get-Content $startupPath -Raw|ConvertFrom-Json).Enabled){throw 'Retire legacy boot routing before switching to native setup'}
try {
 $archiveStream=[IO.File]::Open([IO.Path]::GetFullPath($Package),'Open','Read','Read');if($archiveStream.Length -gt 201326592){throw 'Native package too large'}
 $sha=[Security.Cryptography.SHA256]::Create();try{$actual=([BitConverter]::ToString($sha.ComputeHash($archiveStream))).Replace('-','')}finally{$sha.Dispose()};if($actual -ine $Sha256){throw 'Native package digest mismatch'};$archiveStream.Position=0
 Add-Type -AssemblyName System.IO.Compression;$zip=[IO.Compression.ZipArchive]::new($archiveStream,[IO.Compression.ZipArchiveMode]::Read,$true)
 $noticeNames=@('GPL-3.0.txt','sing-box-LICENSE.txt','amneziawg-LICENSE.txt','wf-LICENSE.txt')
 $names=@{};foreach($entry in $zip.Entries){if($entry.FullName -notin (@('manifest.json','native-owner.exe','native-core.exe','native-guard.exe')+$noticeNames) -or $names.ContainsKey($entry.FullName) -or (($entry.ExternalAttributes -shr 16) -band 61440) -eq 40960){throw 'Unsafe native archive entry'};$names[$entry.FullName]=$entry}
 if($names.Count -ne 8 -or $names['manifest.json'].Length -gt 16384){throw 'Incomplete native component archive'}
 $reader=[IO.StreamReader]::new($names['manifest.json'].Open());try{$manifest=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
 if($manifest.schema -ne 1 -or $manifest.architecture -ne 'win-x64' -or $manifest.setupProtocol -ne 1 -or $manifest.policyProtocol -ne 1 -or @($manifest.files).Count -ne 3){throw 'Native component protocol mismatch'}
 New-Item -ItemType Directory -Path $stage|Out-Null
 $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false);$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'));foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))};Set-Acl -LiteralPath $stage -AclObject $acl
 $roles=@{};foreach($file in $manifest.files){if($file.name -notin @('native-owner.exe','native-core.exe','native-guard.exe') -or $roles.ContainsKey($file.name) -or $file.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $file.bytes -lt 1 -or $file.bytes -gt 134217728 -or $names[$file.name].Length -ne $file.bytes){throw 'Invalid native component role'};$roles[$file.name]=$true;$source=$names[$file.name].Open();$path=Join-Path $stage $file.name;$destination=[IO.File]::Open($path,'CreateNew','Write','None');try{$source.CopyTo($destination)}finally{$destination.Dispose();$source.Dispose()};if((Get-FileHash $path).Hash -ine $file.sha256){throw 'Native role digest mismatch'}}
 if(@($manifest.notices).Count -ne 4 -or $manifest.sourceSha256 -notmatch '^[a-fA-F0-9]{64}$'){throw 'Native license/source inventory missing'}
 $seen=@{};foreach($file in $manifest.notices){if($file.name -notin $noticeNames -or $seen.ContainsKey($file.name) -or $file.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $file.bytes -lt 1 -or $file.bytes -gt 131072 -or $names[$file.name].Length -ne $file.bytes){throw 'Invalid native notice'};$seen[$file.name]=$true;$source=$names[$file.name].Open();$destination=[IO.File]::Open((Join-Path $stage $file.name),'CreateNew','Write','None');try{$source.CopyTo($destination)}finally{$destination.Dispose();$source.Dispose()};if((Get-FileHash (Join-Path $stage $file.name)).Hash -ine $file.sha256){throw 'Native notice digest mismatch'}}
 $physical=@(Get-NetIPConfiguration|Where-Object {$_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' -and $_.NetAdapter.InterfaceDescription -notmatch 'WireGuard|WireSock|Tailscale|VPNUS'});if($physical.Count -ne 1){throw 'One unambiguous physical uplink is required'}
 $policy=@{Vpn=@('0.0.0.0/0','::/0');Direct=@('10.0.0.0/8','172.16.0.0/12','192.168.0.0/16','100.64.0.0/10','fc00::/7','fe80::/10','ff00::/8','255.255.255.255/32')}
 $core=@{log=@{disabled=$true};inbounds=@(@{type='tun';tag='native-tun';interface_name='Cita-Native';address=@('172.30.127.1/30');route_address=@('0.0.0.0/0');route_exclude_address=@();auto_route=$true;strict_route=$false;dns_mode='disabled';stack='gvisor';mtu=1400});outbounds=@(@{type='cita_native_lab';tag='native-vpn';owner=[guid]::NewGuid().ToString('N')},@{type='direct';tag='native-direct';bind_interface=$physical[0].InterfaceAlias});route=@{final='native-vpn';rules=@(@{action='sniff';timeout='300ms'})}}
 foreach($pair in @(@('policy.json',$policy),@('core.json',$core))){[IO.File]::WriteAllText((Join-Path $stage $pair[0]),($pair[1]|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))}
 function Entry([string]$name){return @{Name=$name;Sha256=(Get-FileHash (Join-Path $stage $name)).Hash}}
 $catalog=@{Schema=1;Owner=(Entry 'native-owner.exe');Core=(Entry 'native-core.exe');Guard=(Entry 'native-guard.exe');Policy=(Entry 'policy.json');CoreConfig=(Entry 'core.json')}
 [IO.File]::WriteAllText((Join-Path $stage 'native-catalog.json'),($catalog|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false));[IO.File]::WriteAllText((Join-Path $stage 'service.json'),(@{Enabled=$true;CatalogSha256=(Get-FileHash (Join-Path $stage 'native-catalog.json')).Hash}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
 if(Get-Process smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'Disconnect the existing route before native setup'}
 Stop-NativeForMaintenance -NativeRoot $target;Invoke-AgentCoordinator 'begin-operation';$reserved=$true
 Stop-Service RoutePilotRescue -ErrorAction Stop;(Get-Service RoutePilotRescue).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(45));$stopped=$true
 if(Test-Path -LiteralPath (Join-Path $target 'service.json')){
  $oldSettings=Get-Content (Join-Path $target 'service.json') -Raw|ConvertFrom-Json;$oldName=if($oldSettings.CatalogName){[string]$oldSettings.CatalogName}else{'native-catalog.json'}
  if($oldName -ne 'native-catalog.json' -and $oldName -notmatch '^catalog-[a-f0-9]{32}\.json$'){throw 'Invalid previous native catalog identity'}
  $oldPath=Join-Path $target $oldName;if((Get-FileHash $oldPath).Hash -ine $oldSettings.CatalogSha256){throw 'Previous native catalog digest mismatch'}
  $oldCatalog=Get-Content $oldPath -Raw|ConvertFrom-Json
  foreach($role in @('Policy','Profile','CoreConfig')){$entry=$oldCatalog.$role;if(-not $entry){continue};$name=[string]$entry.Name;if($name -ne [IO.Path]::GetFileName($name) -or $name -match '[:/\\]' -or $name -in @('native-owner.exe','native-core.exe','native-guard.exe','native-catalog.json','service.json')){throw 'Unsafe previous native document role'};$source=Join-Path $target $name;if((Get-Item $source).Length -gt 65536 -or (Get-Item $source).Attributes -band [IO.FileAttributes]::ReparsePoint -or (Get-FileHash $source).Hash -ine $entry.Sha256){throw 'Previous native document digest mismatch'};Copy-Item -LiteralPath $source -Destination (Join-Path $stage $name) -Force;if((Get-FileHash (Join-Path $stage $name)).Hash -ine $entry.Sha256){throw 'Native document changed during setup'};$catalog[$role]=$entry}
  # Keep DHCP broadcast outside the public VPN boundary. Older generated plans
  # are migrated together with their TUN routes in this atomic replacement.
  $policyPath=Join-Path $stage $catalog.Policy.Name;$previousPolicy=Get-Content $policyPath -Raw|ConvertFrom-Json
  if('255.255.255.255/32' -notin $previousPolicy.Direct){
   $previousPolicy.Direct=@($previousPolicy.Direct)+@('255.255.255.255/32');[IO.File]::WriteAllText($policyPath,($previousPolicy|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false));$catalog.Policy=Entry ([IO.Path]::GetFileName($policyPath))
   $plan=@{Action='Plan';Run=[guid]::NewGuid().ToString('N');Policy=$previousPolicy}|ConvertTo-Json -Depth 8 -Compress|& (Join-Path $stage 'native-guard.exe')|ConvertFrom-Json;if(-not $plan.success){throw 'Migrated native guard plan refused'}
   $corePath=Join-Path $stage $catalog.CoreConfig.Name;$previousCore=Get-Content $corePath -Raw|ConvertFrom-Json;$tun=@($previousCore.inbounds|Where-Object type -eq 'tun');if($tun.Count -ne 1){throw 'Migrated native TUN is ambiguous'};$tun[0].route_address=@($plan.prefixes|Where-Object {$_ -notmatch ':'});[IO.File]::WriteAllText($corePath,($previousCore|ConvertTo-Json -Depth 16),[Text.UTF8Encoding]::new($false));$catalog.CoreConfig=Entry ([IO.Path]::GetFileName($corePath))
  }
  if($oldCatalog.Profile){$configured=$true;$catalog.ExpectedExitHash=$oldCatalog.ExpectedExitHash;$catalog.ProfileName=$oldCatalog.ProfileName}
  if(Test-Path -LiteralPath (Join-Path $target 'intent.json')){$intent=Get-Content (Join-Path $target 'intent.json') -Raw|ConvertFrom-Json;if($intent.Desired){throw 'Native intent was not retired'};Copy-Item -LiteralPath (Join-Path $target 'intent.json') -Destination (Join-Path $stage 'intent.json')}
  [IO.File]::WriteAllText((Join-Path $stage 'native-catalog.json'),($catalog|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false));[IO.File]::WriteAllText((Join-Path $stage 'service.json'),(@{Enabled=$true;CatalogSha256=(Get-FileHash (Join-Path $stage 'native-catalog.json')).Hash}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
 }
 if(Test-Path -LiteralPath $target){Move-Item -LiteralPath $target -Destination $backup;$previous=$true};Move-Item -LiteralPath $stage -Destination $target;$changed=$true
 if($LabFailAfterSwap){throw 'Injected isolated replacement failure'}
 Start-Service RoutePilotRescue;(Get-Service RoutePilotRescue).WaitForStatus('Running',[TimeSpan]::FromSeconds(20));$expectedPhase=if($configured){'Disconnected'}else{'Unconfigured'}
 for($attempt=0;$attempt -lt 40;$attempt++){$state=(Invoke-NativeMaintenanceRpc 'status').Native;if($state.phase -eq $expectedPhase -or $state.phase -eq 'Unavailable'){break};Start-Sleep -Milliseconds 200};if($state.phase -ne $expectedPhase){throw 'Native snapshot was not accepted by the service'};$success=$true
} finally {
 if(-not $success -and $changed){Stop-Service RoutePilotRescue -ErrorAction SilentlyContinue;(Get-Service RoutePilotRescue).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30));$failed=Join-Path $AgentRoot ('native-failed-'+[guid]::NewGuid().ToString('N'));Move-Item -LiteralPath $target -Destination $failed;if($previous){Move-Item -LiteralPath $backup -Destination $target};if($wasRunning){Start-Service RoutePilotRescue}}
 elseif(-not $success -and $previous -and -not(Test-Path -LiteralPath $target)){Move-Item -LiteralPath $backup -Destination $target;if($wasRunning){Start-Service RoutePilotRescue}}
 if(-not $success -and $stopped -and $wasRunning -and (Get-Service RoutePilotRescue).Status -eq 'Stopped'){Start-Service RoutePilotRescue}
 if($reserved){try{Invoke-AgentCoordinator 'end-operation'}catch{}}
 if($zip){$zip.Dispose()};if($archiveStream){$archiveStream.Dispose()}
}
if(-not $success){throw 'Native setup failed; previous components restored'}
'Native components prepared. Import your own profile before connecting.'
