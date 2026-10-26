param([switch]$PreferPhysicalRoutes)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$root='C:\CitaLab\PlatformNext';$helper='C:\CitaLab\DependencyLab\Cita.DependencyLab.exe'
$report=@{success=$false;phase='Prepare';startedUtc=[datetime]::UtcNow.ToString('o');scope='Virtual-adapter feasibility; no production connection retention or packet-capture leak proof'}
$original='';$bridge=$null;$directBridge=$null;$policy=$null;$ruleAdded=$false;$changed=$false
$name='CITA-Lab-VirtualBoundary-'+[guid]::NewGuid().ToString('N')
$private=Join-Path $root ('private\boundary-'+[guid]::NewGuid().ToString('N'))
$backup=Join-Path $private 'original\SmartRouting-AWG2.conf';$desired=Join-Path $private 'candidate\SmartRouting-AWG2.conf'
$ownedRoutes=@()
function Read([string]$path){[IO.File]::ReadAllText($path)|ConvertFrom-Json}
function Child([string]$image,[string[]]$arguments,[int]$seconds=120,[switch]$AllowFailure){
 $prefix=Join-Path $private ('call-'+[guid]::NewGuid().ToString('N'))
 $quoted=($arguments|ForEach-Object {'"'+$_.Replace('"','\"')+'"'}) -join ' '
 $p=Start-Process $image -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput ($prefix+'.out') -RedirectStandardError ($prefix+'.err');$null=$p.Handle
 try{
  if(-not $p.WaitForExit($seconds*1000)){throw 'Virtual-boundary child deadline; cleanup required'}
  if($p.ExitCode -ne 0 -and -not $AllowFailure){throw ('Child failed: '+[IO.Path]::GetFileName($image))}
  $stream=[IO.File]::Open(($prefix+'.out'),'Open','Read','ReadWrite, Delete');$reader=New-Object IO.StreamReader($stream)
  try{return @{code=$p.ExitCode;text=$reader.ReadToEnd()}}finally{$reader.Dispose()}
 }finally{$p.Dispose()}
}
function Invoke-LabCli([string[]]$arguments){
 $result=Child 'C:\Program Files\WireSock Secure Connect\command-line\wiresock-connect-cli.exe' $arguments -AllowFailure
 if($result.code -eq 0){return}
 if($arguments[0] -eq 'disconnect'){
  foreach($attempt in 1..8){
   $status=Read-HelperOutput 'C:\Program Files\WireSock Secure Connect\command-line\wiresock-connect-cli.exe' 'status'
   if((Get-WireSockState $status 'SmartRouting-AWG2') -eq 'Disconnected'){return}
   Start-Sleep -Milliseconds 500
  }
 }
 throw 'Test profile CLI operation failed'
}
function Save{$report.updatedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 5|Set-Content C:\CitaLab\virtual-boundary.json -Encoding UTF8}
function Probe([switch]$Proxy,[int]$Port=20838){
 # Empty values do not cross the PowerShell native boundary. Use env-independent
 # loopback proxy selection, and --noproxy '*' for the independent direct probe.
 $arguments=@('--silent','--show-error','--ipv4','--connect-timeout','3','--max-time','7')+$(if($Proxy){@('--socks5-hostname',('127.0.0.1:'+$Port),'--noproxy','localhost')}else{@('--noproxy','*')})+@('https://www.cloudflare.com/cdn-cgi/trace')
 if(-not $Proxy){$arguments=@('--interface',[string]$script:physicalAddress,'--resolve',('www.cloudflare.com:443:'+$script:probeAddress))+$arguments}
 $result=Child 'C:\Windows\System32\curl.exe' $arguments 12 -AllowFailure
 $ip='';if($result.code -eq 0 -and $result.text -match '(?m)^ip=([^\r\n]+)'){$ip=$Matches[1].Trim()}
 $hash='';if($ip){$sha=[Security.Cryptography.SHA256]::Create();try{$hash=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($ip)))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}}
 return @{success=([bool]$ip);egressHash=$hash;curlCode=$result.code}
}
try{
 New-Item -ItemType Directory -Path (Split-Path $backup),(Split-Path $desired) -Force|Out-Null
 . (Join-Path $root 'FileStorage.ps1');. (Join-Path $root 'ConnectionEnvironment.ps1');. (Join-Path $root 'WireSockCli.ps1')
 Protect-PrivateDirectory $private ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
 Save
 if(Get-Process smart-router,vpn-bridge,RoutePilot -ErrorAction SilentlyContinue){throw 'Guest must be idle'}
 $original=(Read 'C:\Program Files\RoutePilot\Agent\agent-config.json').AppRoot
 if(-not $original.StartsWith('C:\CitaLab\Installed\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected original owner'}
 $product=Read (Join-Path $root 'product.json');$product.distribution.agentSha256=(Get-FileHash (Join-Path $root 'runtime\agent-build\RoutePilot.Agent.exe')).Hash
 $product|ConvertTo-Json -Depth 8|Set-Content (Join-Path $root 'product.json') -Encoding UTF8
 $null=Child $helper @('connect',$root);$null=Child $helper @('stop',$root)
 $physical=@(Get-NetIPConfiguration|Where-Object {$_.NetAdapter.HardwareInterface -and $_.NetAdapter.Status -eq 'Up' -and $_.IPv4DefaultGateway}|Select-Object -First 1)
 if($physical.Count -ne 1){throw 'No physical IPv4 baseline'}
 $script:physicalAddress=[string]$physical[0].IPv4Address[0].IPAddress
 $script:probeAddress=[string]([Net.Dns]::GetHostAddresses('www.cloudflare.com')|Where-Object AddressFamily -eq InterNetwork|Select-Object -First 1)
 if(-not $script:physicalAddress -or -not $script:probeAddress){throw 'No pinned IPv4 baseline'}
 $before=Probe;if(-not $before.success){throw 'No direct HTTPS baseline'}
 $report.directBaselineConfirmed=$true
 Invoke-LabCli @('export','SmartRouting-AWG2',$backup)
 $lines=@([IO.File]::ReadAllLines($backup)|Where-Object {$_ -notmatch '^\s*(?:#@ws:\s*)?VirtualAdapterMode\s*='})
 $text=($lines -join "`r`n") -replace '(?m)^\[Interface\]\s*$',"[Interface]`r`n#@ws:VirtualAdapterMode = true"
 [IO.File]::WriteAllText($desired,$text,(New-Object Text.UTF8Encoding($false)))
 $changed=$true;Invoke-LabCli @('delete','SmartRouting-AWG2');Invoke-LabCli @('import',$desired)
 Invoke-LabCli @('connect','SmartRouting-AWG2','-network-lock','disabled','-log-level','warning','-exit')
 $timer=[Diagnostics.Stopwatch]::StartNew();$adapter=$null
 do{$adapter=Get-NetAdapter -IncludeHidden|Where-Object {$_.InterfaceDescription -match '^Wiresock(?:Pro)? Virtual Adapter' -and $_.Status -eq 'Up'}|Select-Object -First 1;if($adapter){break};Start-Sleep -Milliseconds 500}while($timer.Elapsed.TotalSeconds -lt 30)
 if(-not $adapter){throw 'No active WireSock virtual adapter'}
 $report.virtualAdapterObserved=$true
 if($PreferPhysicalRoutes){
  foreach($prefix in @('0.0.0.0/1','128.0.0.0/1')){
   if(Get-NetRoute -DestinationPrefix $prefix -ErrorAction SilentlyContinue){throw 'Existing half-default route prevents isolated feasibility test'}
   $ownedRoutes+=New-NetRoute -DestinationPrefix $prefix -InterfaceIndex $physical[0].InterfaceIndex -NextHop $physical[0].IPv4DefaultGateway.NextHop -RouteMetric 3 -PolicyStore ActiveStore
  }
  $report.physicalRoutePreference=$true
 }
 $config=Read (Join-Path $root 'runtime\bridge.json');$config.inbounds[0].listen_port=20838;$config.outbounds[0].bind_interface=$adapter.Name
 $config.log.output=Join-Path $private 'bridge.log';$configPath=Join-Path $private 'bridge.json';[IO.File]::WriteAllText($configPath,($config|ConvertTo-Json -Depth 15),(New-Object Text.UTF8Encoding($false)))
 $image='C:\Program Files\RoutePilot\Agent\vpn-bridge.exe'
 $bridge=Start-Process $image -ArgumentList @('run','-c',('"'+$configPath+'"'),'--disable-color') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $private 'bridge.out') -RedirectStandardError (Join-Path $private 'bridge.err');$null=$bridge.Handle
 $config.inbounds[0].listen_port=20839;$config.outbounds[0].bind_interface=[string]$physical[0].InterfaceAlias;$config.log.output=Join-Path $private 'direct.log'
 $directConfig=Join-Path $private 'direct.json';[IO.File]::WriteAllText($directConfig,($config|ConvertTo-Json -Depth 15),(New-Object Text.UTF8Encoding($false)))
 $directBridge=Start-Process (Join-Path $root 'runtime\smart-router.exe') -ArgumentList @('run','-c',('"'+$directConfig+'"'),'--disable-color') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $private 'direct.out') -RedirectStandardError (Join-Path $private 'direct.err');$null=$directBridge.Handle
 Start-Sleep -Seconds 2
 $reference=(Read 'C:\CitaLab\server-egress-reference.json').serverEgressHash
 $proxied=Probe -Proxy;$direct=Probe -Proxy -Port 20839
 $report.proxyRequestSucceeded=$proxied.success;$report.proxyCurlCode=$proxied.curlCode;$report.directRequestSucceeded=$direct.success;$report.directCurlCode=$direct.curlCode
 $report.virtualVpnVerified=$proxied.success -and $proxied.egressHash -eq $reference
 $report.directPreserved=$direct.success -and $direct.egressHash -eq $before.egressHash
 if(-not $report.virtualVpnVerified -or -not $report.directPreserved){throw 'Virtual mode did not preserve independent direct/VPN paths'}
 $policy=New-Object -ComObject HNetCfg.FwPolicy2
 foreach($profile in @(1,2,4)){if(-not $policy.FirewallEnabled($profile)){throw 'Firewall disabled'}}
 $rule=New-Object -ComObject HNetCfg.FWRule
 $rule.Name=$name;$rule.Description='Isolated CITA virtual-boundary feasibility';$rule.ApplicationName=$image
 $rule.Direction=2;$rule.Action=0;$rule.Enabled=$true;$rule.Profiles=7;$rule.Protocol=256;$rule.RemoteAddresses='*';$rule.Interfaces=@([string]$physical[0].InterfaceAlias)
 $policy.Rules.Add($rule);$ruleAdded=$true
 $report.phase='ProtectedVirtualProbe';Save
 $guarded=Probe -Proxy;$report.vpnWithPhysicalBlock=$guarded.success -and $guarded.egressHash -eq $reference
 if(-not $report.vpnWithPhysicalBlock){throw 'Physical-interface firewall block also stopped virtual VPN'}
 Invoke-LabCli @('disconnect');Start-Sleep -Seconds 2
 $lost=Probe -Proxy;$stillDirect=Probe -Proxy -Port 20839
 $report.firstDirectAfterDisconnectSucceeded=$stillDirect.success;$report.firstDirectAfterDisconnectCurlCode=$stillDirect.curlCode
 if(-not $stillDirect.success){Start-Sleep -Seconds 1;$stillDirect=Probe -Proxy -Port 20839}
 $rawDirect=Probe;$report.rawDirectAfterDisconnect=$rawDirect.success -and $rawDirect.egressHash -eq $before.egressHash
 $report.directAfterDisconnectRequestSucceeded=$stillDirect.success;$report.directAfterDisconnectCurlCode=$stillDirect.curlCode
 $report.disconnectedProxyBlocked=-not $lost.success;$report.directAfterDisconnect=$stillDirect.success -and $stillDirect.egressHash -eq $before.egressHash
 $report.bridgeRetained=-not $bridge.HasExited
 $report.success=[bool]($report.disconnectedProxyBlocked -and $report.directAfterDisconnect -and $report.bridgeRetained)
}catch{$report.error=$_.Exception.Message}
finally{
 try{
  if($ruleAdded){$policy.Rules.Remove($name);$ruleAdded=$false};$report.ruleRemoved=-not $ruleAdded
  if($bridge -and -not $bridge.HasExited){$bridge.Kill();$bridge.WaitForExit()}
  if($directBridge -and -not $directBridge.HasExited){$directBridge.Kill();$directBridge.WaitForExit()}
  foreach($route in $ownedRoutes){$route|Remove-NetRoute -Confirm:$false}
  $report.temporaryRoutesRemoved=-not [bool]@($ownedRoutes|Where-Object {Get-NetRoute -DestinationPrefix $_.DestinationPrefix -InterfaceIndex $_.InterfaceIndex -NextHop $_.NextHop -ErrorAction SilentlyContinue|Where-Object RouteMetric -eq 3}).Count
  if($changed){Invoke-LabCli @('disconnect');Invoke-LabCli @('delete','SmartRouting-AWG2');Invoke-LabCli @('import',$backup);$report.managedProfileRestored=$true}
  if(Get-Process smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'Guest engines remain'}
  if($original){$config=Read (Join-Path $original 'data\connection.json');$params=@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $original 'Configure-Connection.ps1'),'-Action','Apply','-SourceProfile',$config.SourceProfile,'-CliPath',$config.CliPath);if($config.ExpectedEgressIp){$params+=@('-ExpectedEgressIp',$config.ExpectedEgressIp)};$null=Child 'powershell.exe' $params;$report.originalOwnerRestored=(Read 'C:\Program Files\RoutePilot\Agent\agent-config.json').AppRoot -ieq $original}
 }catch{$report.cleanupError=$_.Exception.Message;$report.success=$false}
 $report.phase='Finished';Save
}
if(-not $report.success){exit 1}
