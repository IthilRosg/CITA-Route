param([switch]$GoClient,[switch]$Wfp,[ValidateSet('WholeConnection','NonSyn','TlsPayload')][string]$Capture='WholeConnection',[switch]$DebugPackets,[switch]$InjectorPeer,[switch]$PacketMetadata,[switch]$SystemCompanion,[switch]$Continuous,[switch]$ControllerCrash,[ValidateSet('None','system','agent','epoch','stop')][string]$DropRule='None',[ValidateSet(1,4)][int]$Parallel=1)
$ErrorActionPreference='Stop'
if($ControllerCrash -and !($Wfp -and $Continuous)){throw 'Controller crash requires the exact continuous WFP lab'}
# Two runs lost their management transport and left persistent lab rules.
# A remote finally block and SSH keepalive do not survive forced reboot or
# process loss. Refuse WFP runs until an independent rescue is implemented
# and verified on this station; no override based on an environment flag.
if($Wfp){throw 'Physical WFP lab disabled: independently verified rescue and management recovery are required.'}
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'runtime/invoke-rm-lab.ps1')
# Bound transport failures: a dead management connection must not leave the
# operator waiting forever. A transport timeout does not prove remote cleanup.
function Invoke-Rm([string]$Code){$Code="$"+'ProgressPreference=''SilentlyContinue'';'+$Code;$enc=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Code));& ssh -i "$env:USERPROFILE\.ssh\cita_test_station_ed25519" -o "UserKnownHostsFile=$env:USERPROFILE\.ssh\cita_test_station_known_hosts" -o StrictHostKeyChecking=yes -o ConnectTimeout=10 -o ServerAliveInterval=5 -o ServerAliveCountMax=3 citastation@26.68.227.253 "powershell -NoProfile -NonInteractive -EncodedCommand $enc";if($LASTEXITCODE){throw 'RM command failed; remote test and cleanup status are unconfirmed'}}
$image=Join-Path $workspace 'runtime/zapret-live-lab-build/Cita.ZapretLiveLab.exe'
$toolHash=(Get-FileHash -LiteralPath $image).Hash
$zipHash=(Get-FileHash -LiteralPath (Join-Path $workspace 'runtime/zapret-module-lab.zip')).Hash
$cacheCode=@'
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong physical station'}
$lab='C:\CITA-Lab\ZapretLiveLab';New-Item -ItemType Directory -Path $lab -Force|Out-Null
$cache=@{};foreach($name in @('Cita.ZapretLiveLab.exe','Cita.CoreConnectorTests.exe','native-guard-lab.exe')){if(Test-Path -LiteralPath "$lab\$name"){$cache[$name]=(Get-FileHash -LiteralPath "$lab\$name").Hash}}
$cache|ConvertTo-Json -Compress
'@
$cache=((Invoke-Rm $cacheCode) -join "`n")|ConvertFrom-Json
function Copy-ChangedLabArtifact([string]$path,[string]$expected)
{
 $name=[IO.Path]::GetFileName($path)
 if($cache.$name -eq $expected){return}
 & scp -q -i "$env:USERPROFILE\.ssh\cita_test_station_ed25519" -o "UserKnownHostsFile=$env:USERPROFILE\.ssh\cita_test_station_known_hosts" -o StrictHostKeyChecking=yes -o ConnectTimeout=10 -o ServerAliveInterval=5 -o ServerAliveCountMax=3 $path 'citastation@26.68.227.253:C:/CITA-Lab/ZapretLiveLab/'
 if($LASTEXITCODE){throw 'Lab artifact transfer failed'}
}
Copy-ChangedLabArtifact $image $toolHash
$peerHash='none'
if($GoClient -or $Wfp)
{
 $peer=Join-Path $workspace 'runtime/Cita.CoreConnectorTests.exe';$peerHash=(Get-FileHash -LiteralPath $peer).Hash
 Copy-ChangedLabArtifact $peer $peerHash
}
$guardHash='none';$run=[Guid]::NewGuid().ToString('N')
if($Wfp)
{
 $guard=Join-Path $workspace 'runtime/native-guard-lab.exe';$guardHash=(Get-FileHash -LiteralPath $guard).Hash
 Copy-ChangedLabArtifact $guard $guardHash
}
$code=@'
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong physical station'}
$root='C:\Program Files\RoutePilot\Agent\zapret-live-lab';$lab='C:\CITA-Lab\ZapretLiveLab';$image="$lab\Cita.ZapretLiveLab.exe";$archive='C:\CITA-Lab\ZapretPreflight\zapret-module-lab.zip'
if((Get-FileHash $image).Hash -ne '__TOOL__' -or (Get-FileHash $archive).Hash -ne '__ZIP__'){throw 'Live-lab digest mismatch'}
if('__MODE__' -in @('--go-client','--go-wfp') -and (Get-FileHash "$lab\Cita.CoreConnectorTests.exe").Hash -ne '__PEER__'){throw 'Go peer digest mismatch'}
if('__MODE__' -eq '--go-wfp' -and (Get-FileHash "$lab\native-guard-lab.exe").Hash -ne '__GUARD__'){throw 'WFP helper digest mismatch'}
if(@(Get-CimInstance Win32_SystemDriver|Where-Object {$_.Name -like 'WinDivert*'}).Count){throw 'Existing WinDivert driver; no change permitted'}
if(@(Get-CimInstance Win32_Process|Where-Object {$_.Name -in @('winws.exe','winws2.exe')}).Count){throw 'Foreign worker present'}
if((Test-Path $root)-and !(Test-Path "$root\cita-live-lab-owned.json")){throw 'Foreign protected fixture'}
New-Item -ItemType Directory -Path $root -Force|Out-Null
[IO.File]::WriteAllText("$root\cita-live-lab-owned.json",'{"owner":"CITA-Zapret-Live-Lab"}')
Expand-Archive -LiteralPath $archive -DestinationPath "$root\module" -Force
if('__MODE__' -eq '--go-wfp')
{
 New-Item -ItemType Directory -Path "$root\native" -Force|Out-Null
 Copy-Item -LiteralPath $image -Destination "$root\RoutePilot.Agent.exe" -Force
 Copy-Item -LiteralPath "$lab\native-guard-lab.exe" -Destination "$root\native\native-guard.exe" -Force
 Copy-Item -LiteralPath 'C:\Program Files\RoutePilot\Agent\native\native-owner.exe' -Destination "$root\native\native-owner.exe" -Force
 $image="$root\RoutePilot.Agent.exe"
}
$admin=New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544');$system=New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
foreach($item in @((Get-Item -LiteralPath $root))+@(Get-ChildItem -LiteralPath $root -Recurse -Force))
{
 if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Fixture reparse point refused'}
 if($item.PSIsContainer){$acl=New-Object Security.AccessControl.DirectorySecurity;$inherit=[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'}
 else{$acl=New-Object Security.AccessControl.FileSecurity;$inherit=[Security.AccessControl.InheritanceFlags]::None}
 $acl.SetOwner($admin);$acl.SetAccessRuleProtection($true,$false)
 foreach($sid in @($admin,$system)){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl',$inherit,'None','Allow')))}
 Set-Acl -LiteralPath $item.FullName -AclObject $acl
}
if((Get-AuthenticodeSignature -LiteralPath "$root\module\WinDivert64.sys").Status -ne 'Valid'){throw 'Driver signature rejected'}
function Invoke-PktmonText([string[]]$Arguments)
{
 $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName="$env:SystemRoot\System32\pktmon.exe";$info.Arguments=(($Arguments|ForEach-Object {'"'+$_+'"'}) -join ' ');$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;$info.StandardOutputEncoding=[Text.Encoding]::UTF8;$info.StandardErrorEncoding=[Text.Encoding]::UTF8
 $process=[Diagnostics.Process]::Start($info);$output=$process.StandardOutput.ReadToEndAsync();$errors=$process.StandardError.ReadToEndAsync()
 try{if(!$process.WaitForExit(10000)){$process.Kill();$process.WaitForExit(3000)|Out-Null;throw 'pktmon deadline'};if($process.ExitCode){throw ('pktmon failed: '+$errors.Result)};return $output.Result}finally{$process.Dispose()}
}
$p=$null;$exit=-1;$stdout='';$stderr='';$cleanup=$false;$wfpCleanup='not requested';$packetStarted=$false;$packetStopped=$true;$packetFilterAdded=$false;$packetSnapshot='';$packetText='';$packetCleanup='not requested';$controlIp='';$mainFailure='';$cleanupErrors=New-Object 'Collections.Generic.List[string]'
try
{
 if('__PACKETS__' -eq '1')
 {
  $status=Invoke-PktmonText @('status');if($status -notmatch '(не запущен|not running)'){throw 'Packet monitor already active or status unknown'}
  $filters=Invoke-PktmonText @('filter','list');if($filters -notmatch '(?m)^\s*(Нет|None)\s*$'){throw 'Foreign packet filters: capture refused'}
  $controlIp=([Net.Dns]::GetHostAddresses('www.wikipedia.org')|Where-Object {$_.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork}|Select-Object -First 1).ToString()
  Invoke-PktmonText @('filter','add','CITA-__RUN__','-i','192.168.1.124',$controlIp,'-t','TCP','-p','443')|Out-Null
  $packetFilterAdded=$true
  $packetSnapshot=Invoke-PktmonText @('filter','list')
  Invoke-PktmonText @('start','--capture','--comp','all','--pkt-size','64','--flags','0x01e','--file-size','8','--log-mode','circular','--file-name',"$lab\packets-__RUN__.etl")|Out-Null
  $packetStopped=$false
  $packetStarted=$true
 }
 $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$image;$info.Arguments='__MODE__';$info.WorkingDirectory=$lab;$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 if('__MODE__' -eq '--go-wfp'){$info.EnvironmentVariables['CITA_GUARD_LAB_RUN']='__RUN__';$info.EnvironmentVariables['CITA_GUARD_CAPTURE']='__CAPTURE__'}
 $info.EnvironmentVariables['CITA_GUARD_DEBUG']='__DEBUG__'
 $info.EnvironmentVariables['CITA_GUARD_INJECTOR']='__INJECTOR__'
 $info.EnvironmentVariables['CITA_GUARD_SYSTEM']='__SYSTEM__'
 $info.EnvironmentVariables['CITA_GUARD_CONTINUOUS']='__CONTINUOUS__'
 $info.EnvironmentVariables['CITA_GUARD_CONTROLLER_CRASH']='__CRASH__'
 $info.EnvironmentVariables['CITA_DYNAMIC_LAB_DROP']='__DROP__'
 $info.EnvironmentVariables['CITA_GUARD_PARALLEL']='__PARALLEL__'
 if($controlIp){$info.EnvironmentVariables['CITA_CONTROL_IPV4']=$controlIp}
 $p=[Diagnostics.Process]::Start($info);$output=$p.StandardOutput.ReadToEndAsync();$errors=$p.StandardError.ReadToEndAsync()
 $elapsed=[Diagnostics.Stopwatch]::StartNew();$snapshots=New-Object 'Collections.Generic.List[string]'
 while(!$p.WaitForExit(1200))
 {
  if($elapsed.ElapsedMilliseconds -ge 65000){$p.Kill();throw 'Live-lab deadline'}
  if('__CRASH__' -eq '1' -and (Test-Path -LiteralPath "$lab\flow-ready-__RUN__.json"))
  {
   $ready=Get-Content -LiteralPath "$lab\flow-ready-__RUN__.json" -Raw|ConvertFrom-Json
   if($ready.run -ne '__RUN__' -or $ready.controller -ne $p.Id -or !$ready.tlsResponseReceived){throw 'Crash witness identity mismatch'}
   $peerRecord=Get-CimInstance Win32_Process -Filter "ProcessId=$($ready.peer)"
   if($peerRecord.ParentProcessId -ne $p.Id -or $peerRecord.ExecutablePath -ine "$lab\Cita.CoreConnectorTests.exe"){throw 'Crash peer ownership mismatch'}
   $crashPeer=Get-Process -Id $ready.peer;$null=$crashPeer.Handle
   function Read-CrashRules([string]$path)
   {
    $ni=New-Object Diagnostics.ProcessStartInfo;$ni.FileName='netsh.exe';$ni.Arguments='wfp show filters file="'+$path+'" verbose=on';$ni.UseShellExecute=$false;$ni.CreateNoWindow=$true;$ni.RedirectStandardOutput=$true;$ni.RedirectStandardError=$true
    $np=[Diagnostics.Process]::Start($ni);$no=$np.StandardOutput.ReadToEndAsync();$ne=$np.StandardError.ReadToEndAsync()
    try{if(!$np.WaitForExit(5000)){$np.Kill();throw 'Crash WFP snapshot deadline'};if($np.ExitCode -ne 0){throw 'Crash WFP snapshot failed'}}finally{$np.Dispose()}
    [xml]$xml=[IO.File]::ReadAllText($path);$names=@()
    foreach($f in $xml.SelectNodes("//*[local-name()='filterId']/..")){if(([string]$f.displayData.name).StartsWith('CITA native guard lab v1:__RUN__')){$names+=[string]$f.displayData.name}}
    return @($names)
   }
   $before=@(Read-CrashRules "$lab\wfp-before-crash-__RUN__.xml")
   if(@($before|Where-Object {$_ -eq 'CITA native guard lab v1:__RUN__:system'}).Count -ne 1 -or @($before|Where-Object {$_ -eq 'CITA native guard lab v1:__RUN__:socket-agent'}).Count -ne 1){throw 'Both distinct dynamic permissions not witnessed before crash'}
   function Inspect-CrashGuard([string]$action)
   {
    $gi=New-Object Diagnostics.ProcessStartInfo;$gi.FileName="$root\native\native-guard.exe";$gi.UseShellExecute=$false;$gi.CreateNoWindow=$true;$gi.RedirectStandardInput=$true;$gi.RedirectStandardOutput=$true;$gi.RedirectStandardError=$true;$gi.EnvironmentVariables['CITA_PHYSICAL_GUARD_LAB']='1'
    $gp=[Diagnostics.Process]::Start($gi);$go=$gp.StandardOutput.ReadToEndAsync();$ge=$gp.StandardError.ReadToEndAsync()
    try{
     $request=@{Action=$action;Run='__RUN__';ExpectedPolicy=$ready.policyDigest;ExpectedEndpoint=$ready.endpointDigest};if($action -eq 'CheckSystemConnector'){$request.Connector=$ready.permission.Scope}
     $gp.StandardInput.WriteLine(($request|ConvertTo-Json -Depth 4 -Compress));$gp.StandardInput.Close()
     if(!$gp.WaitForExit(5000)){$gp.Kill();throw 'Crash strict inspection deadline'}
     $check=$go.Result|ConvertFrom-Json;if($gp.ExitCode -ne 0 -or !$check.success -or $check.run -ne '__RUN__'){throw 'Crash strict inspection refused'}
     return $check
    }finally{$gp.Dispose()}
   }
   $strictBefore=Inspect-CrashGuard 'CheckSystemConnector'
   if($strictBefore.systemPermits -ne 1 -or $strictBefore.socketAgentPermits -ne 1){throw 'Exact dynamic agent/System pair not independently confirmed'}
   $helper=Get-Process -Id $ready.permission.ProcessId;$null=$helper.Handle
   if($helper.StartTime.ToUniversalTime().Ticks -ne $ready.permission.StartedUtcTicks -or $helper.HasExited -or !$ready.permission.Current){throw 'Helper identity/liveness changed before crash'}
   $witnessAgeMs=1000.0*([Diagnostics.Stopwatch]::GetTimestamp()-[long]$ready.at)/[Diagnostics.Stopwatch]::Frequency
   $heartbeatAgeMs=1000.0*([Diagnostics.Stopwatch]::GetTimestamp()-[long]$ready.permission.HeartbeatTimestamp)/[Diagnostics.Stopwatch]::Frequency
   if($witnessAgeMs -lt 0 -or $witnessAgeMs -gt 4500 -or $heartbeatAgeMs -lt 0 -or $heartbeatAgeMs -gt 4500){throw 'Crash witness too old; watchdog expiry cannot be excluded'}
   $killedAt=[DateTime]::UtcNow.ToString('o')
   $p.Kill();if(!$p.WaitForExit(5000)){throw 'Controller hard-kill unconfirmed'}
   $after=@();$dynamicAfter=2
   for($attempt=0;$attempt -lt 5;$attempt++){$after=@(Read-CrashRules "$lab\wfp-after-crash-__RUN__.xml");$dynamicAfter=@($after|Where-Object {$_ -ne 'CITA native guard lab v1:__RUN__'}).Count;if($dynamicAfter -eq 0){break};Start-Sleep -Milliseconds 150}
   $strictAfter=Inspect-CrashGuard 'CheckSystemConnector';$baseAfter=Inspect-CrashGuard 'Inspect'
   if($strictAfter.stage -ne 'SystemLeaseAbsent' -or $strictAfter.rules -ne 0 -or $baseAfter.rules -ne 2 -or $baseAfter.policyDigest -ne $ready.policyDigest -or $baseAfter.endpointDigest -ne $ready.endpointDigest){throw 'Strict post-crash rule/deny proof failed'}
   if(!$helper.WaitForExit(3000)){throw 'Controller-owned helper survived crash'};$helper.Dispose()
   if(!$crashPeer.HasExited){$crashPeer.Kill();if(!$crashPeer.WaitForExit(5000)){throw 'Owned peer exit unconfirmed'}};$crashPeer.Dispose()
   $workers=@(Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -ieq "$root\module\winws2.exe"}).Count
   $crashEvidence=@{tlsResponseReceived=$true;bothDynamicBefore=$true;strictPairBefore=$true;helperAliveBefore=$true;witnessAgeMs=$witnessAgeMs;heartbeatAgeMs=$heartbeatAgeMs;killedAt=$killedAt;inspectedAfter=[DateTime]::UtcNow.ToString('o');dynamicAfter=$dynamicAfter;strictPairAfterAbsent=$true;basePolicyUnchanged=$true;remainingOwnRules=$after.Count;workerProcesses=$workers;controllerExited=$p.HasExited}
   if($dynamicAfter -ne 0 -or $after.Count -ne 2 -or $workers -ne 0){throw 'Controller crash did not preserve deny while retiring exact socket permissions'}
   break
  }
  if('__DEBUG__' -eq '1' -and $snapshots.Count -lt 8)
  {
   # Resolve ephemeral drop filter IDs while WinDivert is still alive. These
   # read-only snapshots stay private in the lab; never print the whole state.
   $snapshot="$lab\wfp-__RUN__-$($snapshots.Count).xml"
   $ni=New-Object Diagnostics.ProcessStartInfo;$ni.FileName='netsh.exe';$ni.Arguments='wfp show filters file="'+$snapshot+'" verbose=on';$ni.UseShellExecute=$false;$ni.CreateNoWindow=$true;$ni.RedirectStandardOutput=$true;$ni.RedirectStandardError=$true
   $np=[Diagnostics.Process]::Start($ni);$no=$np.StandardOutput.ReadToEndAsync();$ne=$np.StandardError.ReadToEndAsync()
   try{if(!$np.WaitForExit(5000)){$np.Kill();throw 'Read-only WFP snapshot deadline'};if($np.ExitCode -ne 0){throw 'Read-only WFP snapshot failed'};$snapshots.Add($snapshot)}finally{$np.Dispose()}
  }
 }
 $exit=$p.ExitCode;$stdout=$output.Result;$stderr=$errors.Result
}
catch{$mainFailure=$_.Exception.Message}
finally
{
 if($p){try{if(!$p.HasExited){$p.Kill();if(!$p.WaitForExit(5000)){throw 'Lab process exit unconfirmed'}}}catch{$cleanupErrors.Add($_.Exception.Message)}finally{$p.Dispose()}}
 if($packetStarted)
 {
  try{Invoke-PktmonText @('stop')|Out-Null;$packetStopped=$true}catch{$cleanupErrors.Add($_.Exception.Message)}
  if($packetStopped){try{Invoke-PktmonText @('etl2txt',"$lab\packets-__RUN__.etl",'--out',"$lab\packets-__RUN__.txt",'--metadata','--brief')|Out-Null;$packetText="$lab\packets-__RUN__.txt"}catch{$cleanupErrors.Add($_.Exception.Message)}}
 }
 if($packetSnapshot)
 {
  try{if(!$packetStopped){throw 'Active capture retained: filter removal refused'};$now=Invoke-PktmonText @('filter','list');if($now -cne $packetSnapshot){throw 'Packet filters changed concurrently: global removal refused'};Invoke-PktmonText @('filter','remove')|Out-Null;$packetCleanup='confirmed'}catch{$cleanupErrors.Add($_.Exception.Message);$packetCleanup='unconfirmed'}
 }
 elseif($packetFilterAdded){$packetCleanup='unconfirmed';$cleanupErrors.Add('Owned packet filter added but snapshot unavailable: global removal refused')}
 try{
  $driver=Get-CimInstance Win32_SystemDriver -Filter "Name='WinDivert'"
  if($driver)
  {
   if($driver.PathName -cne '\??\C:\Program Files\RoutePilot\Agent\zapret-live-lab\module\WinDivert64.sys'){throw 'Foreign driver path: retirement refused'}
   if(@(Get-CimInstance Win32_Process|Where-Object {$_.Name -in @('winws.exe','winws2.exe','Cita.ZapretLiveLab.exe')}).Count){throw 'Worker remains active'}
   & sc.exe stop WinDivert|Out-Null;if($LASTEXITCODE -notin @(0,1062)){throw 'Driver stop failed'}
   if(Get-CimInstance Win32_SystemDriver -Filter "Name='WinDivert'"){& sc.exe delete WinDivert|Out-Null;if($LASTEXITCODE -notin @(0,1060)){throw 'Driver delete failed'}}
  }
  $cleanup=@(Get-CimInstance Win32_SystemDriver|Where-Object {$_.Name -like 'WinDivert*'}).Count -eq 0
 }catch{$cleanupErrors.Add($_.Exception.Message)}
 if('__MODE__' -eq '--go-wfp')
 {
  $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName="$root\native\native-guard.exe";$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;$info.EnvironmentVariables['CITA_PHYSICAL_GUARD_LAB']='1'
  $rescue=$null
  try{
   $rescue=[Diagnostics.Process]::Start($info)
   $rescue.StandardInput.WriteLine('{"Action":"Cleanup","Run":"__RUN__"}');$rescue.StandardInput.Close()
   $reply=$rescue.StandardOutput.ReadToEndAsync();$rescueError=$rescue.StandardError.ReadToEndAsync()
   if(!$rescue.WaitForExit(15000)){$rescue.Kill();throw 'Owned WFP cleanup deadline'}
   $wfpCleanup=$reply.Result
   if($rescue.ExitCode -ne 0){throw 'Owned WFP cleanup failed'}
   $check=$wfpCleanup|ConvertFrom-Json;if(!$check.success -or $check.rules -ne 0){throw 'Owned WFP cleanup unconfirmed'}
  }catch{$cleanupErrors.Add($_.Exception.Message)}finally{if($rescue){$rescue.Dispose()}}
 }
}
$dropFilters=@();$filterResolutionError=''
try{
 if($stdout -and $snapshots.Count){$report=$stdout|ConvertFrom-Json;$ids=@($report.dropEvidence|ForEach-Object {[string]$_.FilterID})
 foreach($snapshot in $snapshots){[xml]$xml=[IO.File]::ReadAllText($snapshot);foreach($node in $xml.SelectNodes("//*[local-name()='filterId']")){if($ids -contains $node.InnerText.Trim()){$rule=$node.ParentNode;$dropFilters+=@{id=$node.InnerText.Trim();name=$rule.displayData.name;layer=$rule.layerKey;provider=$rule.providerKey;action=$rule.action.type;sublayer=$rule.subLayerKey;snapshot=[IO.Path]::GetFileName($snapshot)}}}}
 }
}catch{$filterResolutionError=$_.Exception.Message}
$result=@{checkedAt=[DateTime]::UtcNow.ToString('o');exit=$exit;mainFailure=$mainFailure;cleanupErrors=@($cleanupErrors);toolSha256='__TOOL__';peerSha256='__PEER__';guardSha256='__GUARD__';guardRun='__RUN__';wfpCleanup=$wfpCleanup;driverCleanup=$cleanup;packetText=$packetText;packetCleanup=$packetCleanup;controlIp=$controlIp;output=$stdout;error=$stderr;dropFilters=$dropFilters;filterResolutionError=$filterResolutionError;crashEvidence=$crashEvidence}|ConvertTo-Json -Depth 5
# Persist before writing to SSH: the caller may have lost its transport while
# the remote cleanup continued. No result means the outcome is still unknown.
$resultPath=Join-Path $lab 'result-__RUN__.json'
$resultTemporary=Join-Path $lab 'result-__RUN__.tmp'
[IO.File]::WriteAllText($resultTemporary,$result,[Text.UTF8Encoding]::new($false))
[IO.File]::Move($resultTemporary,$resultPath)
$result
'@
$script=Join-Path $workspace 'runtime/zapret-physical-live-run.ps1'
[IO.File]::WriteAllText($script,$code.Replace('__TOOL__',$toolHash).Replace('__ZIP__',$zipHash).Replace('__PEER__',$peerHash).Replace('__GUARD__',$guardHash).Replace('__RUN__',$run).Replace('__CAPTURE__',$Capture).Replace('__DEBUG__',$(if($DebugPackets){'1'}else{'0'})).Replace('__INJECTOR__',$(if($InjectorPeer){'1'}else{'0'})).Replace('__SYSTEM__',$(if($SystemCompanion){'1'}else{'0'})).Replace('__CONTINUOUS__',$(if($Continuous){'1'}else{'0'})).Replace('__CRASH__',$(if($ControllerCrash){'1'}else{'0'})).Replace('__DROP__',$DropRule).Replace('__PARALLEL__',[string]$Parallel).Replace('__PACKETS__',$(if($PacketMetadata){'1'}else{'0'})).Replace('__MODE__',$(if($Wfp){'--go-wfp'}elseif($GoClient){'--go-client'}else{''})),[Text.UTF8Encoding]::new($true))
& scp -q -i "$env:USERPROFILE\.ssh\cita_test_station_ed25519" -o "UserKnownHostsFile=$env:USERPROFILE\.ssh\cita_test_station_known_hosts" -o StrictHostKeyChecking=yes -o ConnectTimeout=10 -o ServerAliveInterval=5 -o ServerAliveCountMax=3 $script 'citastation@26.68.227.253:C:/CITA-Lab/ZapretLiveLab/run.ps1'
if($LASTEXITCODE){throw 'Live-lab script transfer failed'}
Invoke-Rm "& ([ScriptBlock]::Create([IO.File]::ReadAllText('C:\CITA-Lab\ZapretLiveLab\run.ps1')))"|Set-Content (Join-Path $workspace 'runtime/zapret-physical-live-result.json') -Encoding utf8
Get-Content (Join-Path $workspace 'runtime/zapret-physical-live-result.json')
