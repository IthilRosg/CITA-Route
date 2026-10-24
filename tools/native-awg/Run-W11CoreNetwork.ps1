$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$run=[guid]::NewGuid().ToString('N');$observer=$null
$report=@{schema=1;success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='VM network transitions and owned-peer server DNS observation'}
$nativeHash=(Get-FileHash (Join-Path $root 'runtime/native-awg-lab.exe')).Hash;$coreHash=(Get-FileHash (Join-Path $root 'runtime/native-core-lab.exe')).Hash;$agentHash=(Get-FileHash (Join-Path $root 'runtime/native-agent-backend-lab/Cita.NativeAgentLab.dll')).Hash
. (Join-Path $root 'runtime/vm-lab/W11-Calls.ps1')
try{
 W11-Call @('start',$labVmx,'nogui')|Out-Null
 foreach($pair in @(@('runtime/native-awg-lab.exe','native-awg-lab.exe'),@('runtime/native-core-lab.exe','native-core-lab.exe'),@('tools/native-awg/Run-GuestCoreNetwork.ps1','Run-GuestCoreNetwork.ps1'),@('tools/native-awg/Guest-LinkInterruption.ps1','Guest-LinkInterruption.ps1'),@('tools/native-awg/Guest-SleepWake.ps1','Guest-SleepWake.ps1'),@('tools/native-awg/Guest-IPv4Migration.ps1','Guest-IPv4Migration.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
 foreach($name in @('Cita.NativeAgentLab.exe','Cita.NativeAgentLab.dll','Cita.NativeAgentLab.deps.json','Cita.NativeAgentLab.runtimeconfig.json')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root ('runtime/native-agent-backend-lab/'+$name)),('C:\CitaLab\NativeAwg\AgentBackend\'+$name))|Out-Null}
 $null=. (Join-Path $root 'runtime/vm-lab/Get-ServerDiagnosis.ps1') -LocalOnly
 $encoded=[Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $root 'tools/native-awg/Observe-ServerDns.py')))
 $sshArgs[$sshArgs.Length-1]='python3 -c "import base64;exec(base64.b64decode('''+$encoded+'''))"'
 $observerInfo=[Diagnostics.ProcessStartInfo]::new('ssh.exe');$observerInfo.UseShellExecute=$false;$observerInfo.CreateNoWindow=$true;$observerInfo.RedirectStandardInput=$true;$observerInfo.RedirectStandardOutput=$true;$observerInfo.RedirectStandardError=$true
 foreach($arg in $sshArgs){$observerInfo.ArgumentList.Add($arg)}
 $peerText=[IO.File]::ReadAllText((Join-Path $root 'private/vm-lab/CitaW11Baseline-78987403.conf'));$peerAddress=[regex]::Match($peerText,'(?mi)^Address\s*=\s*(\d+\.\d+\.\d+\.\d+)/').Groups[1].Value;$peerText=$null
 $observer=[Diagnostics.Process]::Start($observerInfo);$observerErrors=$observer.StandardError.ReadToEndAsync();$observer.StandardInput.WriteLine((@{peer=$peerAddress}|ConvertTo-Json -Compress));$observer.StandardInput.Close();$observerReady=$observer.StandardOutput.ReadLine()|ConvertFrom-Json;if($observerReady.stage -ne 'ObservingOwnedPeerDNS'){throw 'Server observer not ready'};$observerOutput=$observer.StandardOutput.ReadToEndAsync()
 W11-Code 'Remove-Item C:\CitaLab\NativeAwg\network-ready.json,C:\CitaLab\NativeAwg\network-resumed.txt,C:\CitaLab\NativeAwg\sleep-request.json -Force -ErrorAction SilentlyContinue;if(Test-Path -LiteralPath C:\CitaLab\NativeAwg\core-network-result.json){Remove-Item -LiteralPath C:\CitaLab\NativeAwg\core-network-result.json -Force -ErrorAction Stop};$p=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\CitaLab\NativeAwg\Run-GuestCoreNetwork.ps1" -RedirectStandardOutput C:\CitaLab\NativeAwg\binding.out -RedirectStandardError C:\CitaLab\NativeAwg\binding.err -PassThru'|Out-Null
 $barrier=Join-Path $root ('runtime/vm-lab/w11/network-barrier-'+$run+'.json');$ready=$null;$sleepResumed=$false
 for($i=0;$i -lt 90;$i++){
  if(-not $sleepResumed){
   $sleepRequest=$null;$vmLog=Join-Path ([IO.Path]::GetDirectoryName($labVmx)) 'vmware.log'
   try{W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\sleep-request.json',($barrier+'.sleep'))|Out-Null;$sleepRequest=Get-Content ($barrier+'.sleep') -Raw|ConvertFrom-Json}catch{}
   if(-not $sleepRequest -and (Get-Item -LiteralPath $vmLog).LastWriteTimeUtc -ge ([datetime]$report.startedUtc).ToUniversalTime() -and ((Get-Content -LiteralPath $vmLog -Tail 20) -match 'VMX exit \(0\)')){$sleepRequest=@{stage='GuestSleepRequested';requestedUtc=$report.startedUtc}}
   if($sleepRequest.stage -eq 'GuestSleepRequested'){
    $suspendFinished=$false
    for($s=0;$s -lt 40;$s++){
     if((Get-Item -LiteralPath $vmLog).LastWriteTimeUtc -ge ([datetime]$sleepRequest.requestedUtc).ToUniversalTime() -and ((Get-Content -LiteralPath $vmLog -Tail 20) -match 'VMX exit \(0\)')){$suspendFinished=$true;break}
     Start-Sleep -Seconds 1
    }
    if(-not $suspendFinished){throw 'Guest sleep did not finish saving VM state'}
    Start-Sleep -Seconds 2;W11-Call @('start',$labVmx,'nogui')|Out-Null;$sleepResumed=$true;$report.sleepWakeController='Host-assisted VMware resume after guest OS sleep request'
   }
  }
  try{W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\network-ready.json',$barrier)|Out-Null;$ready=Get-Content $barrier -Raw|ConvertFrom-Json;if($ready.stage -eq 'SuspendReady' -and $ready.owner -match '^[a-f0-9]{32}$'){break}}catch{}
  try{W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\core-network-result.json',($barrier+'.failed'))|Out-Null;$failedGuest=Get-Content ($barrier+'.failed') -Raw|ConvertFrom-Json;$report.earlyFailure=$failedGuest.probes;break}catch{}
  Start-Sleep -Seconds 1
 }
 if(-not $ready){throw 'Guest suspend barrier did not arrive'}
 W11-Call @('suspend',$labVmx,'soft')|Out-Null;Start-Sleep -Seconds 3;W11-Call @('start',$labVmx,'nogui')|Out-Null
 W11-Code ("[IO.File]::WriteAllText('C:\CitaLab\NativeAwg\network-resumed.txt','"+$ready.owner+"')")|Out-Null
 $guestResult=Join-Path $root ('runtime/vm-lab/w11/network-wait-'+$run+'.json');$done=$false
 for($i=0;$i -lt 45;$i++){try{W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\core-network-result.json',$guestResult)|Out-Null;$done=$true;break}catch{};Start-Sleep -Seconds 1}
 if(-not $done){throw 'Guest network result deadline'}
 $file=Join-Path $root ('runtime/vm-lab/w11/native-core-network-'+$run+'.json');W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\core-network-result.json',$file)|Out-Null
 $r=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json;$report.file=[IO.Path]::GetFileName($file);$report.probes=$r.probes;$report.errorType=$r.errorType
 $report.hashesMatched=[bool]($r.nativeSha256 -eq $nativeHash -and $r.coreSha256 -eq $coreHash -and $r.agentBackendSha256 -eq $agentHash);$report.clean=[bool]($r.processesStopped -and $r.topologyUnchanged -and $r.configRemoved);$report.success=[bool]($r.success -and $report.hashesMatched -and $report.clean)
 $report|Select-Object success,hashesMatched,clean,probes,errorType|ConvertTo-Json -Depth 6 -Compress|Write-Output
}catch{$report.errorType=$_.Exception.GetType().Name}
finally{if($observer){if(-not $observer.WaitForExit(60000)){$observer.Kill()};try{$report.serverDns=$observerOutput.GetAwaiter().GetResult()|ConvertFrom-Json}catch{};$observer.Dispose()};$labCredential=$null;$labVmCredential=$null;$report.nativeSha256=$nativeHash;$report.coreSha256=$coreHash;$report.agentBackendSha256=$agentHash;$report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 8|Set-Content (Join-Path $root ('runtime/native-awg-core-network-'+$run+'.json')) -Encoding UTF8}
if(-not $report.success){throw 'Core binding checkpoint incomplete; inspect preserved receipt'}
