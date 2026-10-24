$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$run=[guid]::NewGuid().ToString('N')
$report=@{schema=1;success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='Agent backend candidate→single recovery owner→AWG; shared socket/process budget, scoped OS TUN TCP/UDP and held direct TCP';cases=@()}
$nativeHash=(Get-FileHash (Join-Path $root 'runtime\native-awg-lab.exe')).Hash;$coreHash=(Get-FileHash (Join-Path $root 'runtime\native-core-lab.exe')).Hash;$agentHash=(Get-FileHash (Join-Path $root 'runtime\native-agent-backend-lab\Cita.NativeAgentLab.dll')).Hash
. (Join-Path $root 'runtime\vm-lab\W11-Calls.ps1')
try{
 W11-Call @('start',$labVmx,'nogui')|Out-Null
 W11-Code 'New-Item -ItemType Directory -Path C:\CitaLab\NativeAwg\AgentBackend -Force|Out-Null'|Out-Null
 foreach($pair in @(@('runtime/native-awg-lab.exe','native-awg-lab.exe'),@('runtime/native-core-lab.exe','native-core-lab.exe'),@('tools/native-awg/Run-GuestProxy.ps1','Run-GuestProxy.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
 foreach($name in @('Cita.NativeAgentLab.exe','Cita.NativeAgentLab.dll','Cita.NativeAgentLab.deps.json','Cita.NativeAgentLab.runtimeconfig.json')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root ('runtime/native-agent-backend-lab/'+$name)),('C:\CitaLab\NativeAwg\AgentBackend\'+$name))|Out-Null}
 foreach($case in @(@('combined-failures',''),@('stop-during-recovery','-OwnerStopRace'))){
  $code='if(Test-Path -LiteralPath C:\CitaLab\NativeAwg\proxy-migration.json){Remove-Item -LiteralPath C:\CitaLab\NativeAwg\proxy-migration.json -Force -ErrorAction Stop};$p=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\CitaLab\NativeAwg\Run-GuestProxy.ps1 -Retention -CandidateCore -Migration Rebind -Tun -Supervised -UDPLifetime -Coordinated -AgentBackend OPTIONS" -RedirectStandardOutput C:\CitaLab\NativeAwg\coordinated.out -RedirectStandardError C:\CitaLab\NativeAwg\coordinated.err -PassThru;$null=$p.Handle;$p.WaitForExit()'
  W11-Code ($code.Replace('OPTIONS',$case[1]))|Out-Null
  $file=Join-Path $root ('runtime\vm-lab\w11\native-coordinated-'+$run+'-'+$case[0]+'.json');W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\proxy-migration.json',$file)|Out-Null
  $r=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json
  $match=[bool]($r.nativeSha256 -eq $nativeHash -and $r.coreSha256 -eq $coreHash -and $r.agentBackendSha256 -eq $agentHash);$clean=[bool]($r.topologyUnchanged -and $r.processesStopped -and $r.credentialFileRemoved)
  $item=@{name=$case[0];file=[IO.Path]::GetFileName($file);success=[bool]($r.success -and $r.supervisionPassed -and $match -and $clean -and $r.tunNoFallback -and $r.tunUdpNoFallback -and $r.directRetained);hashesMatched=$match;clean=$clean;failedStage=$r.failedStage;directRetained=$r.directRetained;socketRetained=$r.coordinatedSocketRetained;sharedBudget=$r.sharedExhausted.budgetUsed;socketRepairs=$r.sharedExhausted.socketRepairs;restarts=$r.sharedExhausted.restarts}
  if($case[0] -eq 'combined-failures'){$item.success=[bool]($item.success -and $r.coordinatedPassed)}
  if($case[0] -eq 'stop-during-recovery'){$item.success=[bool]($item.success -and $r.stopDuringBackoff -and $r.stopAck.budgetUsed -eq 0 -and $r.stopAck.childPid -eq 0 -and $r.stopAck.health -eq 'Stopped')}
  $report.cases+=,$item;$item|ConvertTo-Json -Compress|Write-Output
 }
 $report.success=[bool]($report.cases.Count -eq 2 -and -not($report.cases|Where-Object {-not $_.success}))
}catch{$report.errorType=$_.Exception.GetType().Name}
finally{$labCredential=$null;$labVmCredential=$null;$report.nativeSha256=$nativeHash;$report.coreSha256=$coreHash;$report.agentBackendSha256=$agentHash;$report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 7|Set-Content (Join-Path $root ('runtime\native-awg-coordinated-'+$run+'.json')) -Encoding UTF8}
if(-not $report.success){throw 'Coordinated backend checkpoint incomplete; inspect preserved receipts'}
