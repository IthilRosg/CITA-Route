param()
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$run=[guid]::NewGuid().ToString('N')
$report=@{schema=1;success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='Cold-start measurements with explicit probe failures and DNS retry evidence'}
$nativeHash=(Get-FileHash (Join-Path $root 'runtime/native-awg-lab.exe')).Hash;$agentHash=(Get-FileHash (Join-Path $root 'runtime/native-agent-backend-lab/Cita.NativeAgentLab.dll')).Hash
. (Join-Path $root 'runtime/vm-lab/W11-Calls.ps1')
try{
 W11-Call @('start',$labVmx,'nogui')|Out-Null
 foreach($pair in @(@('runtime/native-awg-lab.exe','native-awg-lab.exe'),@('tools/native-awg/Run-GuestStartup.ps1','Run-GuestStartup.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
 foreach($name in @('Cita.NativeAgentLab.exe','Cita.NativeAgentLab.dll','Cita.NativeAgentLab.deps.json','Cita.NativeAgentLab.runtimeconfig.json')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root ('runtime/native-agent-backend-lab/'+$name)),('C:\CitaLab\NativeAwg\AgentBackend\'+$name))|Out-Null}
 $options=''
 $code='if(Test-Path -LiteralPath C:\CitaLab\NativeAwg\startup-result.json){Remove-Item -LiteralPath C:\CitaLab\NativeAwg\startup-result.json -Force -ErrorAction Stop};$p=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\CitaLab\NativeAwg\Run-GuestStartup.ps1 OPTIONS" -RedirectStandardOutput C:\CitaLab\NativeAwg\profiles.out -RedirectStandardError C:\CitaLab\NativeAwg\profiles.err -PassThru;$null=$p.Handle;$p.WaitForExit()'
 W11-Code ($code.Replace('OPTIONS',$options))|Out-Null
 $file=Join-Path $root ('runtime/vm-lab/w11/native-startup-'+$run+'.json');W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\startup-result.json',$file)|Out-Null
 $r=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json;$report.file=[IO.Path]::GetFileName($file);$report.cases=$r.probes.cases;$report.errorType=$r.probes.errorType;$report.failedStage=$r.probes.failedStage;$report.failedCheck=$r.probes.failedCheck;$report.observations=$r.probes.observations
 $report.hashesMatched=[bool]($r.nativeSha256 -eq $nativeHash -and $r.agentBackendSha256 -eq $agentHash);$report.clean=[bool]($r.processesStopped -and $r.topologyUnchanged);$report.healthy=$r.probes.healthy;$report.failedProbes=$r.probes.failedProbes;$report.success=[bool]($r.probes.healthy -and $r.success -and $report.hashesMatched -and $report.clean)
 $report|Select-Object success,hashesMatched,clean,errorType|ConvertTo-Json -Depth 5 -Compress|Write-Output
}catch{$report.errorType=$_.Exception.GetType().Name}
finally{$labCredential=$null;$labVmCredential=$null;$report.nativeSha256=$nativeHash;$report.agentBackendSha256=$agentHash;$report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 7|Set-Content (Join-Path $root ('runtime/native-awg-startup-'+$run+'.json')) -Encoding UTF8}
if(-not $report.success){throw 'Startup measurement incomplete; inspect preserved receipt'}
