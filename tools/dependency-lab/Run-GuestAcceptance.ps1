param([ValidateRange(30,300)][int]$ObserveSeconds=60,[switch]$ResourcesOnly)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'This runner is restricted to the approved W11 guest.'}
$root='C:\CitaLab\PlatformNext'
$helper='C:\CitaLab\DependencyLab\Cita.DependencyLab.exe'
$protected='C:\Program Files\RoutePilot\Agent\RoutePilot.Agent.exe'
$report=@{success=$false;phase='Preflight';startedUtc=[DateTime]::UtcNow.ToString('o');observations=@();diagnostics=@();scope='W11 isolated short ordinary-session acceptance; not Windows 10, IPv6, gaming or long-term stability proof'}
$original='';$owned='';$gui=$null;$lastResources=@{}
function Save {$report.updatedUtc=[DateTime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 7|Set-Content C:\CitaLab\platform-live-acceptance.json -Encoding UTF8}
function Read([string]$path){[IO.File]::ReadAllText($path)|ConvertFrom-Json}
function Child([string]$image,[string[]]$arguments,[int]$seconds=120){
 $id=[guid]::NewGuid().ToString('N');$prefix='C:\CitaLab\platform-call-'+$id
 $quoted=($arguments|ForEach-Object {'"'+$_.Replace('"','\"')+'"'}) -join ' '
 $p=Start-Process -FilePath $image -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput ($prefix+'.out') -RedirectStandardError ($prefix+'.err');$null=$p.Handle
 try{if(-not $p.WaitForExit($seconds*1000)){throw 'Guest child deadline; do not start another network operation until owner cleanup.'};$p.Refresh();if($p.ExitCode -ne 0){throw ('Guest child failed: '+[IO.Path]::GetFileName($image))};
   $stream=[IO.File]::Open(($prefix+'.out'),'Open','Read',([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete));$reader=New-Object IO.StreamReader($stream)
   try{$reader.ReadToEnd()}finally{$reader.Dispose()}}
 finally{$p.Dispose()}
}
function Script([string]$path,[string[]]$parameters=@()){Child 'powershell.exe' (@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$path)+$parameters)}
try{
 Save
 if(Get-Process smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'A guest route is already active.'}
 $installed=Read 'C:\Program Files\RoutePilot\Agent\agent-config.json';$original=$installed.AppRoot
 if(-not $original.StartsWith('C:\CitaLab\Installed\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected original protected owner.'}
 $sessionPath=Join-Path $root 'runtime\session.json'
 if((Test-Path -LiteralPath $sessionPath) -and (Read $sessionPath).running){throw 'Candidate session is already active.'}
 $productPath=Join-Path $root 'product.json';$product=Read $productPath
 $product.distribution.agentSha256=(Get-FileHash (Join-Path $root 'runtime\agent-build\RoutePilot.Agent.exe')).Hash
 $product|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $productPath -Encoding UTF8
 $report.phase='Connect';Save
 $null=Child $helper @('connect',$root) 240
 $session=Read $sessionPath;$owned=$session.id
 if(-not $session.running -or -not $session.bridgeReady){throw 'Candidate session not ready.'}
 $binding=Read 'C:\Program Files\RoutePilot\Agent\agent-config.json'
 if($binding.AppRoot -ine $root){throw 'Protected owner differs from candidate.'}
 if((Get-FileHash $protected).Hash -ne (Get-FileHash (Join-Path $root 'runtime\agent-build\RoutePilot.Agent.exe')).Hash){throw 'Protected agent hash differs.'}
 $report.protectedAgentVerified=$true;$report.phase='Observe';Save
 if($ResourcesOnly){
  if(Get-Process RoutePilot -ErrorAction SilentlyContinue){throw 'An existing guest GUI prevents the isolated resource fixture'}
  $gui=Start-Process (Join-Path $root 'RoutePilot.exe') -ArgumentList '--startup' -WindowStyle Hidden -PassThru;$null=$gui.Handle
  Start-Sleep -Seconds 5
 }
 $clock=[Diagnostics.Stopwatch]::StartNew()
 do{
   $health=(Child $protected @('--session-health') 25)|ConvertFrom-Json
   $current=Read $sessionPath
   if($current.id -ne $owned -or -not $current.running){throw 'Session ownership changed.'}
   if(-not $health.healthy){Start-Sleep -Seconds 2;$health=(Child $protected @('--session-health') 25)|ConvertFrom-Json}
   $process=Get-Process -Id $current.bridgePid
   $report.observations+=@{seconds=[int]$clock.Elapsed.TotalSeconds;healthy=[bool]$health.healthy;code=$health.code;generation=$current.bridgeGeneration;privateMiB=[math]::Round($process.PrivateMemorySize64/1MB,2);handles=$process.HandleCount}
   if($ResourcesOnly){
    $ids=@{Router=[int]$current.routerPid;Bridge=[int]$current.bridgePid;GUI=[int]$gui.Id;Agent=[int](Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'").ProcessId}
    foreach($entry in @(@('Policy','policy-process.json'),@('Watcher','watch-process.json'))){
     $ownerPath=Join-Path $root ('runtime\'+$entry[1]);if(Test-Path $ownerPath){$ids[$entry[0]]=[int](Read $ownerPath).Pid}
    }
    $rows=@();$sampleUtc=[datetime]::UtcNow
    foreach($role in $ids.Keys){
     $proc=Get-Process -Id $ids[$role] -ErrorAction SilentlyContinue;if(-not $proc){continue}
     $started=$proc.StartTime.ToUniversalTime();$cpu=$proc.TotalProcessorTime.TotalMilliseconds;$percent=$null;$before=$lastResources[$role]
     if($before -and $before.pid -eq $proc.Id -and $before.started -eq $started -and $sampleUtc -gt $before.at -and $cpu -ge $before.cpu){$percent=100*($cpu-$before.cpu)/($sampleUtc-$before.at).TotalMilliseconds/[Environment]::ProcessorCount}
     $rows+=@{role=$role;privateMiB=[math]::Round($proc.PrivateMemorySize64/1MB,2);workingSetMiB=[math]::Round($proc.WorkingSet64/1MB,2);handles=$proc.HandleCount;machineCpuPercent=$percent}
     $lastResources[$role]=@{pid=$proc.Id;started=$started;at=$sampleUtc;cpu=$cpu}
    }
    if(-not $report.ContainsKey('resources')){$report.resources=@()}
    $totalPrivate=0.0;foreach($row in $rows){$totalPrivate+=$row.privateMiB}
    $report.resources+=@{seconds=[int]$clock.Elapsed.TotalSeconds;components=$rows;totalPrivateMiB=$totalPrivate}
   }
   if(-not $health.healthy){throw 'Persistent HTTPS failure.'}
   Save
   if($clock.Elapsed.TotalSeconds -lt $ObserveSeconds){Start-Sleep -Seconds 10}
 }while($clock.Elapsed.TotalSeconds -lt $ObserveSeconds)
 $report.phase='DomainDiagnostics';Save
 # Public roots only, never credentials, paths from the user or downloaded files.
 foreach($domain in $(if($ResourcesOnly){@()}else{@('example.com','www.deepl.com','www.wikipedia.org')})){
   $diagnostic=(Child (Join-Path $root 'runtime\agent-build\RoutePilot.Agent.exe') @('--diagnose-route',$root,$domain) 16)|ConvertFrom-Json
   $report.diagnostics+=@{domain=$domain;current=$diagnostic.Current;code=$diagnostic.Code;paths=$diagnostic.Paths}
 }
 $report.success=$true
}catch{$report.error=$_.Exception.Message}
finally{
 $report.phase='Cleanup';Save
 try{
   if($gui -and -not $gui.HasExited){if($gui.Path -ine (Join-Path $root 'RoutePilot.exe')){throw 'Wrong fixture GUI identity'};$gui.Kill();$gui.WaitForExit()}
   $sessionPath=Join-Path $root 'runtime\session.json'
   if(Test-Path -LiteralPath $sessionPath){$current=Read $sessionPath;if($current.running -and (!$owned -or $current.id -eq $owned)){$null=Child $helper @('stop',$root) 120}}
   $report.stopped=-not [bool](Get-Process smart-router,vpn-bridge -ErrorAction SilentlyContinue)
   if(-not $report.stopped){throw 'Guest engines remain; protected owner was not restored.'}
   if($original){$config=Read (Join-Path $original 'data\connection.json');$parameters=@('-Action','Apply','-SourceProfile',$config.SourceProfile,'-CliPath',$config.CliPath);if($config.ExpectedEgressIp){$parameters+=@('-ExpectedEgressIp',$config.ExpectedEgressIp)};$null=Script (Join-Path $original 'Configure-Connection.ps1') $parameters
      $report.originalProtectedOwnerRestored=(Read 'C:\Program Files\RoutePilot\Agent\agent-config.json').AppRoot -ieq $original}
 }catch{$report.cleanupError=$_.Exception.Message;$report.success=$false}
 $report.finishedUtc=[DateTime]::UtcNow.ToString('o');$report.phase='Finished';Save
}
if(-not $report.success){exit 1}
