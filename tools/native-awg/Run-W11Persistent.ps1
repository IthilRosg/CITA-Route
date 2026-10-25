$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$run=[guid]::NewGuid().ToString('N')
$report=@{schema=1;success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='Independent VM guard and shared automatic core recovery'}
$nativeHash=(Get-FileHash (Join-Path $root 'runtime/native-awg-lab.exe')).Hash;$coreHash=(Get-FileHash (Join-Path $root 'runtime/native-core-lab.exe')).Hash;$agentHash=(Get-FileHash (Join-Path $root 'runtime/native-agent-backend-lab/Cita.NativeAgentLab.dll')).Hash
. (Join-Path $root 'runtime/vm-lab/W11-Calls.ps1')
try{
 W11-Call @('start',$labVmx,'nogui')|Out-Null
 foreach($pair in @(@('runtime/native-awg-lab.exe','native-awg-lab.exe'),@('runtime/native-core-lab.exe','native-core-lab.exe'),@('tools/native-awg/Run-GuestPersistent.ps1','Run-GuestPersistent.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
 foreach($name in @('Cita.NativeAgentLab.exe','Cita.NativeAgentLab.dll','Cita.NativeAgentLab.deps.json','Cita.NativeAgentLab.runtimeconfig.json')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root ('runtime/native-agent-backend-lab/'+$name)),('C:\CitaLab\NativeAwg\AgentBackend\'+$name))|Out-Null}
 W11-Code 'if(Test-Path -LiteralPath C:\CitaLab\NativeAwg\persistent-control-result.json){Remove-Item -LiteralPath C:\CitaLab\NativeAwg\persistent-control-result.json -Force -ErrorAction Stop};$p=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\CitaLab\NativeAwg\Run-GuestPersistent.ps1" -RedirectStandardOutput C:\CitaLab\NativeAwg\binding.out -RedirectStandardError C:\CitaLab\NativeAwg\binding.err -PassThru;$null=$p.Handle;$p.WaitForExit()'|Out-Null
 $file=Join-Path $root ('runtime/vm-lab/w11/native-persistent-'+$run+'.json');W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\persistent-control-result.json',$file)|Out-Null
 $r=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json;$report.file=[IO.Path]::GetFileName($file);$report.probes=$r.probes;$report.errorType=$r.errorType
 $report.hashesMatched=[bool]($r.nativeSha256 -eq $nativeHash -and $r.coreSha256 -eq $coreHash -and $r.agentBackendSha256 -eq $agentHash);$report.clean=[bool]($r.processesStopped -and $r.topologyUnchanged -and $r.configRemoved -and $r.guardRemoved);$report.success=[bool]($r.success -and $report.hashesMatched -and $report.clean)
 $report|Select-Object success,hashesMatched,clean,probes,errorType|ConvertTo-Json -Depth 6 -Compress|Write-Output
}catch{$report.errorType=$_.Exception.GetType().Name}
finally{$labCredential=$null;$labVmCredential=$null;$report.nativeSha256=$nativeHash;$report.coreSha256=$coreHash;$report.agentBackendSha256=$agentHash;$report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 8|Set-Content (Join-Path $root ('runtime/native-awg-persistent-'+$run+'.json')) -Encoding UTF8}
if(-not $report.success){throw 'Core binding checkpoint incomplete; inspect preserved receipt'}
