param([switch]$ThroughCore)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'));$run=[guid]::NewGuid().ToString('N');$remote='/tmp/cita-native-workload-'+$run
$report=@{schema=1;success=$false;throughCore=[bool]$ThroughCore;startedUtc=[datetime]::UtcNow.ToString('o')};$server=$null;$stage='prepare';$sshArgs=$null;$remoteCreated=$false
$privateFile=Join-Path $root ('private/vm-lab/workload-'+$run+'.json')
function Remote([string]$Command,[object]$Payload=$null){
 $info=[Diagnostics.ProcessStartInfo]::new('ssh.exe');$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $arguments=@($sshArgs);$arguments[$arguments.Length-1]=$Command;foreach($arg in $arguments){$info.ArgumentList.Add($arg)}
 $p=[Diagnostics.Process]::Start($info)
 try{
  $output=$p.StandardOutput.ReadToEndAsync();$errors=$p.StandardError.ReadToEndAsync()
  if($null -ne $Payload){$p.StandardInput.WriteLine(($Payload|ConvertTo-Json -Depth 5 -Compress))};$p.StandardInput.Close()
  if(-not $p.WaitForExit(30000)){$p.Kill();throw 'Owned server operation deadline'}
  if($p.ExitCode -ne 0){throw 'Owned server operation failed; details suppressed'}
  return $output.GetAwaiter().GetResult()
 }finally{if(-not $p.HasExited){$p.Kill()};$p.Dispose()}
}
. (Join-Path $root 'runtime/vm-lab/W11-Calls.ps1')
try{
 $null=. (Join-Path $root 'runtime/vm-lab/Get-ServerDiagnosis.ps1') -LocalOnly
 $hostAddress=$endpointParts[0];$serverHash=(Get-FileHash (Join-Path $root 'runtime/native-workload-server')).Hash
 $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $root 'runtime/native-workload-lab.exe'),'--prepare');$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true
 $p=[Diagnostics.Process]::Start($info);$material=$p.StandardOutput.ReadToEnd()|ConvertFrom-Json;$p.WaitForExit();if($p.ExitCode -ne 0){throw 'Test certificate generation failed'};$p.Dispose()
 $client=@{CertPEM=$material.CertPEM;KeyPEM=$material.KeyPEM;Address=($hostAddress+':49197');Port=0;Token=''}
 [IO.File]::WriteAllText($privateFile,($client|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
 if($ThroughCore){
  W11-Call @('start',$labVmx,'nogui')|Out-Null
  foreach($pair in @(@('runtime/native-awg-lab.exe','native-awg-lab.exe'),@('private/vm-lab/second-server-pinned.conf','private\second-server.conf'),@('private/vm-lab/second-server-egress.txt','private\second-server-egress.txt'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
  foreach($name in @('Cita.NativeAgentLab.exe','Cita.NativeAgentLab.dll','Cita.NativeAgentLab.deps.json','Cita.NativeAgentLab.runtimeconfig.json')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root ('runtime/native-agent-backend-lab/'+$name)),('C:\CitaLab\NativeAwg\AgentBackend\'+$name))|Out-Null}
  W11-Code 'if(Get-Process native-awg-lab,native-core-lab,Cita.NativeAgentLab -ErrorAction SilentlyContinue){throw "Guest must be idle"};Remove-Item C:\CitaLab\NativeAwg\private\second-server-source.txt -Force -ErrorAction SilentlyContinue;$p=Start-Process C:\CitaLab\NativeAwg\AgentBackend\Cita.NativeAgentLab.exe -WindowStyle Hidden -ArgumentList "--export-second-exit-lab C:\CitaLab\NativeAwg" -RedirectStandardOutput C:\CitaLab\NativeAwg\source.out -RedirectStandardError C:\CitaLab\NativeAwg\source.err -PassThru;$null=$p.Handle;$p.WaitForExit();if($p.ExitCode -ne 0){throw "VPN exit validation failed"}'|Out-Null
  W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\private\second-server-source.txt',(Join-Path $root ('private/vm-lab/workload-source-'+$run+'.txt')))|Out-Null
 }
 $stage='server-deploy';$null=Remote ("umask 077; mkdir '"+$remote+"'");$remoteCreated=$true
 $info=[Diagnostics.ProcessStartInfo]::new('scp.exe');$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 foreach($arg in @('-i',(Join-Path $env:USERPROFILE '.ssh/se1_ed25519'),'-P','22','-o','BatchMode=yes','-o','IdentitiesOnly=yes','-o','StrictHostKeyChecking=yes',(Join-Path $root 'runtime/native-workload-server'),('root@'+$hostAddress+':'+$remote+'/server'))){$info.ArgumentList.Add($arg)}
 $p=[Diagnostics.Process]::Start($info);$out=$p.StandardOutput.ReadToEndAsync();$err=$p.StandardError.ReadToEndAsync();if(-not $p.WaitForExit(60000)){$p.Kill();throw 'Workload fixture copy deadline'};if($p.ExitCode -ne 0){throw 'Workload fixture copy failed'};$p.Dispose()
 $python=[Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $root 'tools/native-workload-lab/server.py')))
 $serverCommand='python3 -c "import base64;exec(base64.b64decode('''+$python+'''))"'
 $serverSpec=@{CertPEM=$material.CertPEM;KeyPEM=$material.KeyPEM;Address='0.0.0.0:49197';Port=0;Token=''}
 $peerText=[IO.File]::ReadAllText((Join-Path $root 'private/vm-lab/CitaW11Baseline-78987403.conf'));$approvedPeer=[regex]::Match($peerText,'(?mi)^Address\s*=\s*(\d+\.\d+\.\d+\.\d+)/').Groups[1].Value;$peerText=$null
 if(-not $approvedPeer){throw 'Approved lab peer address missing'}
 $fixtureInterface='awg-cita2';$secondServerKeyHash=''
 if($ThroughCore){
  $approvedPeer=([IO.File]::ReadAllText((Join-Path $root ('private/vm-lab/workload-source-'+$run+'.txt')))).Trim()
  $expectedSecond=([IO.File]::ReadAllText((Join-Path $root 'private/vm-lab/second-server-egress.txt'))).Trim()
  $sourceHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($approvedPeer)))
  if(-not $sourceHash.Equals($expectedSecond,[StringComparison]::OrdinalIgnoreCase)){throw 'Test server source does not match verified VPN exit'}
  $fixtureInterface='ens3'
  if($approvedPeer -eq $hostAddress){
   $second=[IO.File]::ReadAllText((Join-Path $root 'private/vm-lab/second-server-pinned.conf'))
   $approvedPeer=[regex]::Match($second,'(?mi)^Address\s*=\s*(\d+\.\d+\.\d+\.\d+)/').Groups[1].Value
   $serverPublicKey=[regex]::Match($second,'(?mi)^PublicKey\s*=\s*([^\r\n]+)').Groups[1].Value.Trim()
   $secondServerKeyHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($serverPublicKey)))
   $second=$null;$serverPublicKey=$null;$fixtureInterface='matching-awg-peer';$report.fixtureScope='Verified owned AWG interface and private second-profile peer; shared public exit'
  }
 }
 $server=(Remote $serverCommand @{action='start';run=$run;sha256=$serverHash;spec=$serverSpec;peer=$approvedPeer;interface=$fixtureInterface;serverPublicKeyHash=$secondServerKeyHash})|ConvertFrom-Json
 if(-not $server.success){throw 'Owned fixture not ready'}
 $stage='guest-workload';W11-Call @('start',$labVmx,'nogui')|Out-Null
 foreach($pair in @(@('runtime/native-awg-lab.exe','native-awg-lab.exe'),@('runtime/native-workload-lab.exe','native-workload-lab.exe'),@('tools/native-awg/Run-GuestWorkload.ps1','Run-GuestWorkload.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
 W11-Call @('copyFileFromHostToGuest',$labVmx,$privateFile,'C:\CitaLab\NativeAwg\private\workload.json')|Out-Null
 foreach($name in @('Cita.NativeAgentLab.exe','Cita.NativeAgentLab.dll','Cita.NativeAgentLab.deps.json','Cita.NativeAgentLab.runtimeconfig.json')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root ('runtime/native-agent-backend-lab/'+$name)),('C:\CitaLab\NativeAwg\AgentBackend\'+$name))|Out-Null}
 if($ThroughCore){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root 'private/vm-lab/second-server-pinned.conf'),'C:\CitaLab\NativeAwg\private\second-server.conf')|Out-Null;W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root 'private/vm-lab/second-server-egress.txt'),'C:\CitaLab\NativeAwg\private\second-server-egress.txt')|Out-Null;foreach($pair in @(@('runtime/native-core-lab.exe','native-core-lab.exe'),@('tools/native-awg/Run-GuestCoreWorkload.ps1','Run-GuestWorkload.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}}
 W11-Code 'Remove-Item C:\CitaLab\NativeAwg\workload-result.json -Force -ErrorAction SilentlyContinue;$p=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\CitaLab\NativeAwg\Run-GuestWorkload.ps1" -RedirectStandardOutput C:\CitaLab\NativeAwg\workload.out -RedirectStandardError C:\CitaLab\NativeAwg\workload.err -PassThru;$null=$p.Handle;$p.WaitForExit();Remove-Item C:\CitaLab\NativeAwg\private\workload.json -Force'|Out-Null
 $file=Join-Path $root ('runtime/vm-lab/w11/native-workload-'+$run+'.json');W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\workload-result.json',$file)|Out-Null
 $r=Get-Content $file -Raw|ConvertFrom-Json;$report.probes=$r.probes;$report.clean=[bool]($r.processesStopped -and $r.topologyUnchanged);$report.hashesMatched=[bool]($r.nativeSha256 -eq (Get-FileHash (Join-Path $root 'runtime/native-awg-lab.exe')).Hash -and $r.agentBackendSha256 -eq (Get-FileHash (Join-Path $root 'runtime/native-agent-backend-lab/Cita.NativeAgentLab.dll')).Hash);$report.success=[bool]($r.success -and $report.clean -and $report.hashesMatched);if($ThroughCore){$report.coreHashMatched=[bool]($r.coreSha256 -eq (Get-FileHash (Join-Path $root 'runtime/native-core-lab.exe')).Hash);$report.clean=[bool]($report.clean -and $r.configRemoved);$report.success=[bool]($report.success -and $report.clean -and $report.coreHashMatched)}
}catch{$report.errorType=$_.Exception.GetType().Name;$report.failedStage=$stage}
finally{
 if($server){try{$report.serverCleanup=(Remote $serverCommand @{action='stop';run=$run;pid=$server.pid;start=$server.start})|ConvertFrom-Json;if(-not $report.serverCleanup.success){$report.success=$false}}catch{$report.serverCleanupFailed=$true;$report.success=$false}}
 elseif($remoteCreated -and $serverCommand){try{$report.serverCleanup=(Remote $serverCommand @{action='discard';run=$run;sha256=$serverHash})|ConvertFrom-Json}catch{$report.serverCleanupUnconfirmed=$true}}
 $sourceFile=Join-Path $root ('private/vm-lab/workload-source-'+$run+'.txt');if(Test-Path -LiteralPath $sourceFile){Remove-Item -LiteralPath $sourceFile -Force}
 if(Test-Path -LiteralPath $privateFile){Remove-Item -LiteralPath $privateFile -Force}
 $material=$null;$client=$null;$serverSpec=$null;$fields=$null;$source=$null;$labCredential=$null;$labVmCredential=$null
 $report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 10|Set-Content (Join-Path $root ('runtime/native-awg-workload-'+$run+'.json')) -Encoding UTF8
}
$report|ConvertTo-Json -Depth 9 -Compress|Write-Output
if(-not $report.success){throw 'Mixed workload acceptance incomplete; see sanitized receipt'}
