$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $root 'runtime/vm-lab/W11-Calls.ps1')
$report=@{success=$false;scope='Actual isolated W11 reboot; no host power/network changes'}
try {
 W11-Call @('start',$labVmx,'nogui')|Out-Null
 foreach($name in @('RoutePilot.Agent.exe','RoutePilot.Agent.dll','RoutePilot.Agent.deps.json','RoutePilot.Agent.runtimeconfig.json')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root ('runtime/native-agent-backend-compile/'+$name)),('C:\CitaLab\NativeAwg\ServiceCandidate\'+$name))|Out-Null}
 foreach($pair in @(@('runtime/native-core-lab.exe','native-core-lab.exe'),@('runtime/native-awg-lab.exe','native-awg-lab.exe'),@('runtime/native-guard-lab.exe','native-guard-lab.exe'),@('tools/native-awg/Run-GuestNativeService.ps1','Run-GuestNativeService.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
 W11-Code 'if(Test-Path C:\CitaLab\NativeAwg\native-boot-handoff.json){Remove-Item -LiteralPath C:\CitaLab\NativeAwg\native-boot-handoff.json -Force}'|Out-Null
 try{W11-Code '& C:\CitaLab\NativeAwg\Run-GuestNativeService.ps1 -Reboot'|Out-Null}catch{}
 $handoff=Join-Path $root 'runtime/native-boot-handoff.json';W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\native-boot-handoff.json',$handoff)|Out-Null
 Start-Sleep -Seconds 7
 $ready=$false;for($attempt=0;$attempt -lt 40;$attempt++){try{$state=W11-Call @('checkToolsState',$labVmx);if($state -match 'running'){$ready=$true;break}}catch{};Start-Sleep -Seconds 2};if(-not $ready){throw 'Guest tools did not return after reboot'}
 # Tools reports running before guest command execution is ready after boot.
 # Retry only a read-only readiness command, never the destructive acceptance.
 $ready=$false;for($attempt=0;$attempt -lt 20;$attempt++){try{W11-Code '$null=Get-Service RoutePilotRescue -ErrorAction Stop'|Out-Null;$ready=$true;break}catch{};Start-Sleep -Seconds 2};if(-not $ready){throw 'Guest command channel did not return; startup rescue remains armed'}
 $code=@'
$ErrorActionPreference='Stop';$root='C:\CitaLab\NativeAwg';$handoff=Get-Content (Join-Path $root 'native-boot-handoff.json') -Raw|ConvertFrom-Json;$report=@{success=$false}
function Rpc([string]$command,[string]$id=''){$pipe=[IO.Pipes.NamedPipeClientStream]::new('.','RoutePilot.Rescue.v1',[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous,[Security.Principal.TokenImpersonationLevel]::Identification);try{$pipe.Connect(3000);$b=[Text.Encoding]::UTF8.GetBytes((@{Command=$command;RunId=$id}|ConvertTo-Json -Compress)+"`n");$pipe.Write($b,0,$b.Length);$pipe.Flush();$reader=[IO.StreamReader]::new($pipe);$task=$reader.ReadLineAsync();if(-not $task.Wait(5000)){throw 'Boot RPC deadline'};return ($task.Result|ConvertFrom-Json)}finally{$pipe.Dispose()}}
try {
 if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong guest'}
 $boot=(Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().Ticks;if($boot -eq $handoff.bootBefore){throw 'No actual Windows reboot occurred'};$report.actualReboot=$true
 for($i=0;$i -lt 50;$i++){try{$native=(Rpc 'status').Native;if($native.phase -eq 'Disconnected'){break}}catch{};Start-Sleep -Milliseconds 200}
 if($native.phase -ne 'Disconnected' -or $native.desired -or $native.profileId -ne $handoff.profileId -or (Get-Process native-owner,native-core -ErrorAction SilentlyContinue)){throw 'Unexpected native session after boot'};$report.noUnrequestedConnectionAfterBoot=$true;$report.profileRetained=$true
 $null=Rpc 'native-start';for($i=0;$i -lt 80;$i++){$native=(Rpc 'status').Native;if($native.phase -eq 'Connected'){break};Start-Sleep -Milliseconds 300};if($native.phase -ne 'Connected'){throw 'Explicit connection after reboot failed'};$report.explicitConnectAfterBoot=$true
 $null=Rpc 'native-stop' $native.operation;for($i=0;$i -lt 60;$i++){if((Rpc 'status').Native.phase -eq 'Disconnected'){break};Start-Sleep -Milliseconds 200};$report.success=$true
}catch{$report.errorType=$_.Exception.GetType().Name;$report.error=$_.Exception.Message}
finally {
 if($handoff.task -notmatch '^CitaNativeBootRescue-[a-f0-9]{32}$' -or [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($handoff.folder)) -ne $env:ProgramFiles -or [IO.Path]::GetFileName($handoff.folder) -notmatch '^CitaNativeService-[a-f0-9]{32}$'){throw 'Boot recovery ownership mismatch'}
 try{& $handoff.restore;$report.serviceRestored=$true;Unregister-ScheduledTask -TaskName $handoff.task -Confirm:$false -ErrorAction SilentlyContinue;Remove-Item -LiteralPath $handoff.folder -Recurse -Force;$report.candidateRemoved=$true}catch{$report.success=$false;$report.restoreError=$_.Exception.GetType().Name}
 $report|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root 'native-boot-result.json') -Encoding UTF8
}
'@
 W11-Code $code|Out-Null
 $file=Join-Path $root ('runtime/native-boot-'+[guid]::NewGuid().ToString('N')+'.json');W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\native-boot-result.json',$file)|Out-Null;$report=Get-Content $file -Raw|ConvertFrom-Json;$report|ConvertTo-Json -Depth 5 -Compress|Write-Output
 if(-not $report.success){throw 'Native actual-boot acceptance incomplete'}
}finally{$labCredential=$null;$labVmCredential=$null}
