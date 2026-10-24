param()
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$run=[guid]::NewGuid().ToString('N')
$report=@{schema=1;success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='Distinct server/key backend matrix, no TUN fallback inference'}
$nativeHash=(Get-FileHash (Join-Path $root 'runtime/native-awg-lab.exe')).Hash;$agentHash=(Get-FileHash (Join-Path $root 'runtime/native-agent-backend-lab/Cita.NativeAgentLab.dll')).Hash
. (Join-Path $root 'runtime/vm-lab/W11-Calls.ps1')
try{
 W11-Call @('start',$labVmx,'nogui')|Out-Null
 foreach($pair in @(@('runtime/native-awg-lab.exe','native-awg-lab.exe'),@('tools/native-awg/Run-GuestServers.ps1','Run-GuestServers.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
 W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root 'private/vm-lab/second-server-pinned.conf'),'C:\CitaLab\NativeAwg\private\second-server.conf')|Out-Null
 foreach($name in @('Cita.NativeAgentLab.exe','Cita.NativeAgentLab.dll','Cita.NativeAgentLab.deps.json','Cita.NativeAgentLab.runtimeconfig.json')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root ('runtime/native-agent-backend-lab/'+$name)),('C:\CitaLab\NativeAwg\AgentBackend\'+$name))|Out-Null}
 $options=''
 $code='if(Test-Path -LiteralPath C:\CitaLab\NativeAwg\servers-result.json){Remove-Item -LiteralPath C:\CitaLab\NativeAwg\servers-result.json -Force -ErrorAction Stop};$p=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\CitaLab\NativeAwg\Run-GuestServers.ps1 OPTIONS" -RedirectStandardOutput C:\CitaLab\NativeAwg\profiles.out -RedirectStandardError C:\CitaLab\NativeAwg\profiles.err -PassThru;$null=$p.Handle;$p.WaitForExit()'
 W11-Code ($code.Replace('OPTIONS',$options))|Out-Null
 $file=Join-Path $root ('runtime/vm-lab/w11/native-servers-'+$run+'.json');W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\servers-result.json',$file)|Out-Null
 $r=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json;$report.file=[IO.Path]::GetFileName($file);$report.cases=$r.probes.cases;$report.errorType=$r.probes.errorType;$report.failedStage=$r.probes.failedStage;$report.failedCheck=$r.probes.failedCheck;$report.observations=$r.probes.observations
 $report.hashesMatched=[bool]($r.nativeSha256 -eq $nativeHash -and $r.agentBackendSha256 -eq $agentHash);$report.clean=[bool]($r.processesStopped -and $r.topologyUnchanged);$report.success=[bool]($r.success -and $report.hashesMatched -and $report.clean)
 $report|Select-Object success,hashesMatched,clean,errorType|ConvertTo-Json -Depth 5 -Compress|Write-Output
}catch{$report.errorType=$_.Exception.GetType().Name}
finally{$labCredential=$null;$labVmCredential=$null;$report.nativeSha256=$nativeHash;$report.agentBackendSha256=$agentHash;$report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 7|Set-Content (Join-Path $root ('runtime/native-awg-servers-'+$run+'.json')) -Encoding UTF8}
if(-not $report.success){throw 'Startup measurement incomplete; inspect preserved receipt'}
