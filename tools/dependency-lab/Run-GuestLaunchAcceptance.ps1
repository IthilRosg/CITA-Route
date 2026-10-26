$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$root='C:\CitaLab\PlatformNext';$helper='C:\CitaLab\DependencyLab\Cita.DependencyLab.exe'
$report=@{success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='Real W11 program-start auto-connect; not a Windows power-cycle or long stability test'}
$original='';$gui=$null;$preferencesPath=Join-Path $root 'data\preferences.json';$preferencesBackup=$null
function Read([string]$path){[IO.File]::ReadAllText($path)|ConvertFrom-Json}
function Preferences([bool]$enabled){
 $value=@{ConnectOnLaunch=$enabled;StartupBehaviorVersion=1;AutoInstallUpdates=$false;AutoUpdateWireSock=$false}|ConvertTo-Json
 [IO.File]::WriteAllText($preferencesPath,$value,(New-Object Text.UTF8Encoding($false)))
}
function Child([string]$image,[string[]]$arguments,[int]$seconds=150){
 $prefix='C:\CitaLab\launch-call-'+[guid]::NewGuid().ToString('N')
 $quoted=($arguments|ForEach-Object {'"'+$_.Replace('"','\"')+'"'}) -join ' '
 $p=Start-Process $image -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput ($prefix+'.out') -RedirectStandardError ($prefix+'.err');$null=$p.Handle
 try{if(-not $p.WaitForExit($seconds*1000)){throw 'Guest child deadline'};if($p.ExitCode -ne 0){throw ('Guest child failed: '+[IO.Path]::GetFileName($image))}}
 finally{$p.Dispose()}
}
function Session{Read (Join-Path $root 'runtime\session.json')}
function Launch{
 $script:launchUtc=[datetime]::UtcNow
 $script:gui=Start-Process (Join-Path $root 'RoutePilot.exe') -ArgumentList '--startup' -WindowStyle Hidden -PassThru;$null=$script:gui.Handle
}
function EndGui{
 if($script:gui -and -not $script:gui.HasExited){
  if($script:gui.Path -ine (Join-Path $root 'RoutePilot.exe')){throw 'Unexpected GUI identity'}
  $script:gui.Kill();$script:gui.WaitForExit();$script:gui.Dispose();$script:gui=$null
 }
}
function WaitConnected{
 $clock=[Diagnostics.Stopwatch]::StartNew()
 do{
  if($gui.HasExited){throw 'GUI exited during startup'}
  $launchFile=Join-Path $root 'runtime\launch-connect.json'
  if(Test-Path $launchFile){$status=Read $launchFile;if($status.phase -eq 'NotConnected' -and ([datetime]$status.updatedUtc).ToUniversalTime() -ge $launchUtc){throw ('Automatic startup rejected: '+[string]$status.message)}}
  $s=Session
  if($s.running -and $s.bridgeReady){
   $health=Read (Join-Path $root 'runtime\session-health.json')
   if($health.healthy -and $health.sessionId -eq $s.id -and $health.bridgeGeneration -eq $s.bridgeGeneration -and ([datetime]::UtcNow-([datetime]$health.updatedUtc).ToUniversalTime()).TotalSeconds -le 45 -and $status.phase -eq 'Running' -and ([datetime]$status.updatedUtc).ToUniversalTime() -ge $launchUtc){return $s}
  }
  Start-Sleep -Milliseconds 500
 }while($clock.Elapsed.TotalSeconds -lt 100)
 throw 'Automatic startup did not confirm a fresh HTTPS generation'
}
try{
 if(Get-Process smart-router,vpn-bridge,RoutePilot -ErrorAction SilentlyContinue){throw 'Guest has an active route or GUI'}
 $original=(Read 'C:\Program Files\RoutePilot\Agent\agent-config.json').AppRoot
 if(-not $original.StartsWith('C:\CitaLab\Installed\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected original owner'}
 if(Test-Path -LiteralPath $preferencesPath){$preferencesBackup=[IO.File]::ReadAllBytes($preferencesPath)}
 $product=Read (Join-Path $root 'product.json');$product.distribution.agentSha256=(Get-FileHash (Join-Path $root 'runtime\agent-build\RoutePilot.Agent.exe')).Hash
 $product|ConvertTo-Json -Depth 8|Set-Content (Join-Path $root 'product.json') -Encoding UTF8
 $config=Read (Join-Path $root 'data\connection.json')
 $originalConnection=Read (Join-Path $original 'data\connection.json')
 if(-not $config.ExpectedEgressIp -and $config.SourceProfile -eq $originalConnection.SourceProfile){$config.ExpectedEgressIp=$originalConnection.ExpectedEgressIp}
 $apply=@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $root 'Configure-Connection.ps1'),'-Action','Apply','-SourceProfile',$config.SourceProfile,'-CliPath',$config.CliPath)
 if($config.ExpectedEgressIp){$apply+=@('-ExpectedEgressIp',$config.ExpectedEgressIp)}
 Child 'powershell.exe' $apply
 Preferences $true
 Launch;$first=WaitConnected;$report.firstLaunchConnected=$true
 Child (Join-Path $root 'RoutePilot.exe') @('--startup') 10
 Start-Sleep -Seconds 3
 if((Session).id -ne $first.id -or -not(Session).running){throw 'Duplicate launch changed the working session'}
 $report.duplicateLaunchPreservedSession=$true
 EndGui;Launch;$adopted=WaitConnected
 if($adopted.id -ne $first.id){throw 'Restarting the GUI replaced a working route'}
 $report.guiRestartPreservedWorkingRoute=$true
 Child $helper @('stop',$root)
 Start-Sleep -Seconds 12
 if((Session).running){throw 'Same-process automatic startup undid manual Stop'}
 $report.manualStopPreserved=$true
 EndGui;Launch;$second=WaitConnected
 if($second.id -eq $first.id){throw 'Restart reused a stale session'}
 $report.processRestartConnected=$true
 Child $helper @('stop',$root);EndGui
 Preferences $false
 Launch;Start-Sleep -Seconds 15
 if((Session).running){throw 'Disabled launch option still connected'}
 $report.disabledRemainedStopped=$true;$report.success=$true
}catch{$report.error=$_.Exception.Message}
finally{
 try{
  if((Session).running){Child $helper @('stop',$root)}
  EndGui
  if(Get-Process smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'Guest engines remain'}
  if($preferencesBackup){[IO.File]::WriteAllBytes($preferencesPath,$preferencesBackup)}else{Remove-Item -LiteralPath $preferencesPath -ErrorAction SilentlyContinue}
  if($original){$config=Read (Join-Path $original 'data\connection.json');$parameters=@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $original 'Configure-Connection.ps1'),'-Action','Apply','-SourceProfile',$config.SourceProfile,'-CliPath',$config.CliPath);if($config.ExpectedEgressIp){$parameters+=@('-ExpectedEgressIp',$config.ExpectedEgressIp)};Child 'powershell.exe' $parameters;$report.originalOwnerRestored=(Read 'C:\Program Files\RoutePilot\Agent\agent-config.json').AppRoot -ieq $original}
 }catch{$report.cleanupError=$_.Exception.Message;$report.success=$false}
 $report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 5|Set-Content C:\CitaLab\platform-launch-acceptance.json -Encoding UTF8
}
if(-not $report.success){exit 1}
