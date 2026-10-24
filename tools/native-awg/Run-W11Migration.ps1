$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$report=@{schema=1;success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='TCP through core and UDP direct-native across local encrypted-port rebind and same-subnet guest IPv4 change; not provider/Wi-Fi/TUN migration';cases=@()}
$nativeHash=(Get-FileHash (Join-Path $root 'runtime\native-awg-lab.exe')).Hash;$coreHash=(Get-FileHash (Join-Path $root 'runtime\native-core-lab.exe')).Hash
. (Join-Path $root 'runtime\vm-lab\W11-Calls.ps1')
try{
 W11-Call @('start',$labVmx,'nogui')|Out-Null
 foreach($pair in @(@('runtime/native-awg-lab.exe','native-awg-lab.exe'),@('runtime/native-core-lab.exe','native-core-lab.exe'),@('tools/native-awg/Run-GuestProxy.ps1','Run-GuestProxy.ps1'),@('tools/native-awg/Run-GuestCapture.ps1','Run-GuestCapture.ps1'),@('tools/native-awg/Guest-IPv4Migration.ps1','Guest-IPv4Migration.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
 foreach($case in @(@('rebind','Run-GuestProxy.ps1','-Retention -CandidateCore -Migration Rebind','proxy-migration.json'),@('ipv4','Run-GuestProxy.ps1','-Retention -CandidateCore -Migration GuestIPv4','proxy-migration.json'),@('capture','Run-GuestCapture.ps1','-ProxyChain -Rebind','capture-migration.json'))){
  $code='$p=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\CitaLab\NativeAwg\SCRIPT OPTIONS" -RedirectStandardOutput C:\CitaLab\NativeAwg\migration-final.out -RedirectStandardError C:\CitaLab\NativeAwg\migration-final.err -PassThru;$null=$p.Handle;$p.WaitForExit()'
  W11-Code ($code.Replace('SCRIPT',$case[1]).Replace('OPTIONS',$case[2]))|Out-Null
  $file=Join-Path $root ('runtime\vm-lab\w11\native-migration-final-'+$case[0]+'.json');W11-Call @('copyFileFromGuestToHost',$labVmx,('C:\CitaLab\NativeAwg\'+$case[3]),$file)|Out-Null
  $result=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json;$probe=if($case[0] -eq 'capture'){$result.proxyProbe}else{$result}
  $clean=[bool]($probe.processesStopped -and $probe.topologyUnchanged -and $probe.credentialFileRemoved);$match=[bool]($probe.nativeSha256 -eq $nativeHash -and $probe.coreSha256 -eq $coreHash)
  $item=@{name=$case[0];success=[bool]($result.success -and $clean -and $match);clean=$clean;hashesMatched=$match;transitionMilliseconds=$probe.migrationElapsedMilliseconds;requestMilliseconds=$probe.retention.client.requestMilliseconds;udpQueries=$probe.migrationUdp.queries;sourceChanged=$probe.guestIpChanged;sourceRestored=$probe.guestIpRestored}
  $report.cases+=,$item;$item|ConvertTo-Json -Compress|Write-Output
 }
 $report.success=[bool]($report.cases.Count -eq 3 -and -not ($report.cases|Where-Object {-not $_.success}))
}catch{$report.errorType=$_.Exception.GetType().Name}
finally{$labCredential=$null;$labVmCredential=$null;$report.nativeSha256=$nativeHash;$report.coreSha256=$coreHash;$report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root 'runtime\native-awg-migration-acceptance.json') -Encoding UTF8}
if(-not $report.success){throw 'Migration checkpoint incomplete; inspect preserved local receipts'}
