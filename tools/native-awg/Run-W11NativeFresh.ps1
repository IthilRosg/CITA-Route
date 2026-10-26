param([string]$ExistingSnapshot='')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$snapshot='Cita-NativeFresh-'+[guid]::NewGuid().ToString('N');$saved=$false;$report=@{success=$false}
. (Join-Path $root 'runtime/vm-lab/W11-Calls.ps1')
try{
 W11-Code 'if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne "E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83"){throw "Wrong guest"};if(Get-Process native-owner,native-core,native-awg-lab,smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw "Guest must be idle"}'|Out-Null
 if($ExistingSnapshot){$listing=W11-Call @('listSnapshots',$labVmx);if(($listing -split '\r?\n') -cnotcontains $ExistingSnapshot){throw 'The user recovery snapshot was not found'};$snapshot=$ExistingSnapshot;$saved=$true}else{W11-Call @('snapshot',$labVmx,$snapshot)|Out-Null;$saved=$true}
 W11-Code 'New-Item -ItemType Directory -Path C:\CitaLab\NativeFresh\client\runtime\agent-build -Force|Out-Null'|Out-Null
 foreach($name in @('Install-NativeAgent.ps1','Install-NativeComponents.ps1','Uninstall-NativeAgent.ps1','NativeMaintenance.ps1','FileStorage.ps1','AgentPackage.ps1','AgentLease.ps1','DesktopRuntime.ps1','product.json')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $name),('C:\CitaLab\NativeFresh\client\'+$name))|Out-Null}
 W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root 'runtime/native-fresh-package/runtime/agent-build/RoutePilot.Agent.exe'),'C:\CitaLab\NativeFresh\client\runtime\agent-build\RoutePilot.Agent.exe')|Out-Null
 W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root 'runtime/native-components.zip'),'C:\CitaLab\NativeFresh\native-components.zip')|Out-Null
 $code=@'
$ErrorActionPreference='Stop';$report=@{success=$false};$root='C:\CitaLab\NativeFresh\client';$agent=Join-Path $env:ProgramFiles 'RoutePilot\Agent';$rescue=$null
function Rpc([string]$command,[string]$id='', $profile=$null){$pipe=[IO.Pipes.NamedPipeClientStream]::new('.','RoutePilot.Rescue.v1',[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous,[Security.Principal.TokenImpersonationLevel]::Identification);try{$pipe.Connect(3000);$bytes=[Text.Encoding]::UTF8.GetBytes((@{Command=$command;RunId=$id;Profile=$profile}|ConvertTo-Json -Compress)+"`n");$pipe.Write($bytes,0,$bytes.Length);$pipe.Flush();$reader=[IO.StreamReader]::new($pipe);$line=$reader.ReadLineAsync();if(-not $line.Wait(5000)){throw 'IPC deadline'};return ($line.Result|ConvertFrom-Json)}finally{$pipe.Dispose()}}
function Wait-Native([string]$phase){for($i=0;$i -lt 120;$i++){try{$state=(Rpc 'status').Native;if($state.phase -eq $phase){return $state};if($state.phase -eq 'RecoveryFailed'){throw 'Native recovery failed'}}catch{};Start-Sleep -Milliseconds 300};throw 'Native state deadline'}
function Phase([string]$name){$report.phase=$name;$report|ConvertTo-Json -Depth 5|Set-Content C:\CitaLab\NativeFresh\fresh-progress.json -Encoding UTF8}
try{
 Phase 'Preflight'
 if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong VM'}
 if([IO.Path]::GetFullPath($agent) -ine (Join-Path $env:ProgramFiles 'RoutePilot\Agent')){throw 'Wrong isolated removal path'}
 if((Rpc 'status').Native.desired){throw 'Existing route must be intentionally off'}
 $report.baselineAgentSha256=(Get-FileHash (Join-Path $agent 'RoutePilot.Agent.exe')).Hash;$report.baselineConfigSha256=(Get-FileHash (Join-Path $agent 'agent-config.json')).Hash
 $rescueText=@'
param([switch]$Delayed)
if($Delayed){Start-Sleep -Seconds 180}
$ErrorActionPreference='Stop';if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){exit 1}
$agent=Join-Path $env:ProgramFiles 'RoutePilot\Agent';$config=Join-Path $agent 'agent-config.json';if(-not(Test-Path -LiteralPath $config)){exit 0};$owner=Get-Content $config -Raw|ConvertFrom-Json;if($owner.AppRoot -ne 'C:\CitaLab\NativeFresh\client'){exit 0}
try{. C:\CitaLab\NativeFresh\client\NativeMaintenance.ps1;Stop-NativeForMaintenance (Join-Path $agent 'native')}catch{}
if(Get-Service RoutePilotRescue -ErrorAction SilentlyContinue){Stop-Service RoutePilotRescue -Force;(Get-Service RoutePilotRescue).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))}
$intent=Join-Path $agent 'native\intent.json';if(Test-Path -LiteralPath $intent){$id=Get-Content $intent -Raw|ConvertFrom-Json;if($id.Run -notmatch '^[a-f0-9]{32}$'){throw 'Fresh rescue ownership mismatch'};$result=@{Action='Cleanup';Run=$id.Run}|ConvertTo-Json -Compress|& (Join-Path $agent 'native\native-guard.exe')|ConvertFrom-Json;if(-not $result.success){throw 'Fresh guard cleanup failed'}}
__CITA_RESCUE_END__
 $rescuePath='C:\CitaLab\NativeFresh\fresh-rescue.ps1';[IO.File]::WriteAllText($rescuePath,$rescueText,[Text.UTF8Encoding]::new($false));$rescue=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$rescuePath,'-Delayed') -PassThru
 Stop-Service RoutePilotRescue -ErrorAction SilentlyContinue;if(Get-Service RoutePilotRescue -ErrorAction SilentlyContinue){(Get-Service RoutePilotRescue).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30));& sc.exe delete RoutePilotRescue|Out-Null;if($LASTEXITCODE){throw 'Guest service deletion failed'}}
 for($i=0;$i -lt 30;$i++){if(-not(Get-Service RoutePilotRescue -ErrorAction SilentlyContinue)){break};Start-Sleep -Milliseconds 200}
 if(Test-Path -LiteralPath $agent){if((Get-Item -LiteralPath $agent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked VM fixture path'};Remove-Item -LiteralPath $agent -Recurse -Force}
 Phase 'InstallService'; & (Join-Path $root 'Install-NativeAgent.ps1') -UserSid ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) -UserProfile $env:USERPROFILE
 if((Rpc 'native-capabilities').Native.setupProtocol -ne 1){throw 'Fresh service capabilities missing'};$report.freshServiceCreated=$true
 Phase 'InstallComponents';$zip='C:\CitaLab\NativeFresh\native-components.zip'
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 Add-Type -AssemblyName System.IO.Compression
 $legacy='C:\CitaLab\NativeFresh\legacy-components.zip';Copy-Item -LiteralPath $zip -Destination $legacy -Force
 $archive=[IO.Compression.ZipFile]::Open($legacy,[IO.Compression.ZipArchiveMode]::Update)
 try{$entry=$archive.GetEntry('manifest.json');$reader=[IO.StreamReader]::new($entry.Open());try{$manifest=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()};$entry.Delete();$manifest.directScopeProtocol=0;$writer=[IO.StreamWriter]::new($archive.CreateEntry('manifest.json').Open(),[Text.UTF8Encoding]::new($false));try{$writer.Write(($manifest|ConvertTo-Json -Depth 8))}finally{$writer.Dispose()}}finally{$archive.Dispose()}
 $rejected=$false;try{& (Join-Path $root 'Install-NativeComponents.ps1') -Package $legacy -Sha256 (Get-FileHash $legacy).Hash}catch{if($_.Exception.Message -eq 'Native component protocol mismatch'){$rejected=$true}else{throw}}
 if(-not $rejected -or (Test-Path (Join-Path $agent 'native')) -or (Get-Service RoutePilotRescue).Status -ne 'Running'){throw 'Legacy component rejection changed the fresh service'};$report.legacyScopeProtocolRefused=$true
 & (Join-Path $root 'Install-NativeComponents.ps1') -Package $zip -Sha256 (Get-FileHash $zip).Hash
 $template=Wait-Native 'Unconfigured';$report.templateInstalled=$true
 $bytes=[IO.File]::ReadAllBytes('C:\CitaLab\NativeAwg\private\peer.conf');try{$profile=@{Base64=[Convert]::ToBase64String($bytes);Name='Fresh install profile';RequestId=[guid]::NewGuid().ToString('N')}}finally{[Array]::Clear($bytes,0,$bytes.Length)}
 Phase 'ImportAndConnect';if(-not(Rpc 'native-import' $template.operation $profile).Success){throw 'Fresh profile import refused'};$profile=$null;$null=Wait-Native 'Disconnected';if(-not(Rpc 'native-start').Success){throw 'Fresh connect refused'};$active=Wait-Native 'Connected';$report.freshProfileConnected=$true
 # Leave an actual retired component copy, then prove uninstall removes it.
 Phase 'UpdateActiveComponents'; & (Join-Path $root 'Install-NativeComponents.ps1') -Package $zip -Sha256 (Get-FileHash $zip).Hash
 $selected=Wait-Native 'Disconnected';if($selected.profileId -ne $active.profileId){throw 'Active update changed profile'};if(-not(Rpc 'native-start').Success){throw 'Updated profile connect refused'};$null=Wait-Native 'Connected';$report.activeUpdateRetainsProfile=$true
 Phase 'UninstallActiveRoute'; & (Join-Path $root 'Uninstall-NativeAgent.ps1') -AppRoot $root -UserSid ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
 if((Get-Service RoutePilotRescue -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath (Join-Path $agent 'native')) -or (Get-Process native-owner,native-core -ErrorAction SilentlyContinue)){throw 'Native removal incomplete'};$report.activeRouteUninstalledCleanly=$true
 if(Get-ChildItem -LiteralPath $agent -Directory -Force|Where-Object Name -match '^native-(backup|failed|stage)-[a-f0-9]{32}$'){throw 'Retired native copies remain after uninstall'};$report.retiredComponentsRemoved=$true
 $report.success=$true
}catch{$report.errorType=$_.Exception.GetType().Name;$report.errorId=$_.FullyQualifiedErrorId;$report.failedLine=$_.InvocationInfo.ScriptLineNumber}
finally{if($rescue){try{& $rescuePath;if(-not $rescue.HasExited){Stop-Process -Id $rescue.Id -Force};$report.independentCleanupConfirmed=$true}catch{$report.success=$false;$report.cleanupFailed=$true};$rescue.Dispose()};$report|ConvertTo-Json -Depth 5|Set-Content C:\CitaLab\NativeFresh\fresh-result.json -Encoding UTF8}
'@
 # Keep long fixture bodies out of VMware's command-line transport. The file
 # contains test logic only; the personal profile is read inside the guest.
 $code=$code.Replace('__CITA_RESCUE_END__',"'@");$runner=Join-Path $root 'runtime/native-fresh-guest.ps1';[IO.File]::WriteAllText($runner,$code,[Text.UTF8Encoding]::new($true))
 W11-Call @('copyFileFromHostToGuest',$labVmx,$runner,'C:\CitaLab\NativeFresh\Run-Fresh.ps1')|Out-Null
 W11-Call @('runProgramInGuest',$labVmx,'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe','-NoProfile','-NonInteractive','-WindowStyle','Hidden','-ExecutionPolicy','Bypass','-File','C:\CitaLab\NativeFresh\Run-Fresh.ps1')|Out-Null
 $file=Join-Path $root ('runtime/native-fresh-'+[guid]::NewGuid().ToString('N')+'.json');W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeFresh\fresh-result.json',$file)|Out-Null;$report=Get-Content $file -Raw|ConvertFrom-Json
}catch{
 try{W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeFresh\fresh-progress.json',(Join-Path $root 'runtime/native-fresh-last-progress.json'))|Out-Null;W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeFresh\fresh-result.json',(Join-Path $root 'runtime/native-fresh-late-result.json'))|Out-Null}catch{}
 $report=@{success=$false;testStarted=$saved;errorType=$_.Exception.GetType().Name;stage=if($saved){'FreshInstall'}else{'SnapshotPrerequisite'}}
 $report|ConvertTo-Json|Set-Content (Join-Path $root 'runtime/native-fresh-blocked.json') -Encoding UTF8
 throw
}finally{
 if($saved){try{W11-Call @('revertToSnapshot',$labVmx,$snapshot)|Out-Null;W11-Call @('start',$labVmx,'nogui')|Out-Null;if(-not $ExistingSnapshot){W11-Call @('deleteSnapshot',$labVmx,$snapshot)|Out-Null}}catch{$report|Add-Member -NotePropertyName restoreRequired -NotePropertyValue $true -Force;$report|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root 'runtime/native-fresh-restore-required.json') -Encoding UTF8}}
 $labCredential=$null;$labVmCredential=$null
}
$report|ConvertTo-Json -Compress|Write-Output;if(-not $report.success){throw 'Fresh native installation acceptance incomplete'}
