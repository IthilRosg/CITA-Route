param([string]$AgentFolder='runtime/native-agent-backend-compile',[switch]$Bootstrap,[switch]$Network,[switch]$Performance,[ValidateRange(12,90)][int]$PerformanceSeconds=12,[switch]$Dns)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$run=[guid]::NewGuid().ToString('N')
$agentRoot=[IO.Path]::GetFullPath((Join-Path $root $AgentFolder));$runtimeRoot=[IO.Path]::GetFullPath((Join-Path $root 'runtime'))+[IO.Path]::DirectorySeparatorChar
if(-not $agentRoot.StartsWith($runtimeRoot,[StringComparison]::OrdinalIgnoreCase)-or -not (Test-Path -LiteralPath $agentRoot -PathType Container)){throw 'Unexpected candidate artifact directory'}
. (Join-Path $root 'runtime/vm-lab/W11-Calls.ps1')
try{
 W11-Call @('start',$labVmx,'nogui')|Out-Null
 W11-Code 'New-Item -ItemType Directory -Path C:\CitaLab\NativeAwg\ServiceCandidate,C:\CitaLab\NativeAwg\NativeUiLab -Force|Out-Null'|Out-Null
 foreach($name in @('Cita.NativeUiLab.exe','Cita.NativeUiLab.dll','Cita.NativeUiLab.deps.json','Cita.NativeUiLab.runtimeconfig.json','RoutePilot.dll')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root ('runtime/native-ui-clean-build/'+$name)),('C:\CitaLab\NativeAwg\NativeUiLab\'+$name))|Out-Null}
 foreach($name in @('RoutePilot.Agent.exe','RoutePilot.Agent.dll','RoutePilot.Agent.deps.json','RoutePilot.Agent.runtimeconfig.json')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $agentRoot $name),('C:\CitaLab\NativeAwg\ServiceCandidate\'+$name))|Out-Null}
 foreach($pair in @(@('runtime/native-awg-lab.exe','native-awg-lab.exe'),@('runtime/native-core-lab.exe','native-core-lab.exe'),@('runtime/native-guard-lab.exe','native-guard-lab.exe'),@('tools/native-awg/Run-GuestNativeService.ps1','Run-GuestNativeService.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
 foreach($name in @('Cita.NativeAgentLab.exe','Cita.NativeAgentLab.dll','Cita.NativeAgentLab.deps.json','Cita.NativeAgentLab.runtimeconfig.json')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root ('runtime/native-agent-backend-lab/'+$name)),('C:\CitaLab\NativeAwg\AgentBackend\'+$name))|Out-Null}
 W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root 'NativeMaintenance.ps1'),'C:\CitaLab\NativeAwg\NativeMaintenance.ps1')|Out-Null
 W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $PSScriptRoot 'NativeImportAcceptance.ps1'),'C:\CitaLab\NativeAwg\NativeImportAcceptance.ps1')|Out-Null
 if($Bootstrap){foreach($pair in @(@('runtime/native-components.zip','native-components.zip'),@('Install-NativeComponents.ps1','Install-NativeComponents.ps1'),@('AgentLease.ps1','AgentLease.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}}
 if($Network){foreach($name in @('Guest-LinkInterruption.ps1','Guest-IPv4Migration.ps1')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $PSScriptRoot $name),('C:\CitaLab\NativeAwg\'+$name))|Out-Null}}
 $mode=if($Bootstrap){' -Bootstrap'}else{''};if($Network){$mode+=' -Network'}
 if($Dns){$mode+=' -Dns'}
 if($Performance){$mode+=' -Performance -PerformanceSeconds '+$PerformanceSeconds}
 W11-Code ('if(Test-Path C:\CitaLab\NativeAwg\native-service-result.json){Remove-Item -LiteralPath C:\CitaLab\NativeAwg\native-service-result.json -Force};Start-Process powershell.exe -WindowStyle Hidden -ArgumentList ''-NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\CitaLab\NativeAwg\Run-GuestNativeService.ps1'+$mode+''' -RedirectStandardOutput C:\CitaLab\NativeAwg\service-suite.out -RedirectStandardError C:\CitaLab\NativeAwg\service-suite.err|Out-Null')|Out-Null
 $file=Join-Path $root ('runtime/native-scm-'+$run+'.json');$deadline=[DateTime]::UtcNow.AddMinutes(7);$done=$false
 do{try{W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\native-service-result.json',$file)|Out-Null;$null=Get-Content $file -Raw|ConvertFrom-Json;$done=$true;break}catch{};Start-Sleep -Seconds 2}while([DateTime]::UtcNow -lt $deadline)
 if(-not $done){throw 'Native service suite exceeded its bounded deadline; independent guest rescue remains armed'}
 $result=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json
 $matched=[bool]($result.agentSha256 -eq (Get-FileHash (Join-Path $agentRoot 'RoutePilot.Agent.dll')).Hash -and $result.ownerSha256 -eq (Get-FileHash (Join-Path $root 'runtime/native-awg-lab.exe')).Hash -and $result.coreSha256 -eq (Get-FileHash (Join-Path $root 'runtime/native-core-lab.exe')).Hash -and $result.guardSha256 -eq (Get-FileHash (Join-Path $root 'runtime/native-guard-lab.exe')).Hash)
 $result|Add-Member -NotePropertyName agentArtifactDirectory -NotePropertyValue (Split-Path $agentRoot -Leaf)
 $result|Add-Member -NotePropertyName hashesMatched -NotePropertyValue $matched;$result|Add-Member -NotePropertyName runnerSha256 -NotePropertyValue (Get-FileHash (Join-Path $PSScriptRoot 'Run-GuestNativeService.ps1')).Hash;$result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $file -Encoding UTF8;$result|ConvertTo-Json -Depth 5 -Compress|Write-Output
 if(-not $result.success -or -not $matched){throw 'Native SCM acceptance incomplete'}
}finally{$labCredential=$null;$labVmCredential=$null}
