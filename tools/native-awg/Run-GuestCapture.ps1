param([switch]$RetentionOnly,[switch]$InboundLoss)
$ErrorActionPreference='Stop'
if($InboundLoss -and -not $RetentionOnly){throw 'Incoming loss requires retention capture'}
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$root='C:\CitaLab\NativeAwg';$exe=Join-Path $root 'native-awg-lab.exe'
$report=@{success=$false;startedUtc=[datetime]::UtcNow.ToString('o');scope='Pktmon OS-NIC IPv4 observation with direct positive control; not all-platform or IPv6 proof'}
$capture=$false
$captureDirectory=Join-Path $root ('private\capture-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $captureDirectory -Force|Out-Null
function Command([string[]]$arguments){& pktmon @arguments *> (Join-Path $root 'pktmon-last.log');if($LASTEXITCODE -ne 0){throw 'Packet monitor command failed; existing capture/filters are not altered'}}
function Capture([string]$name,[scriptblock]$work,[int]$port){
 $etl=Join-Path $captureDirectory ($name+'.etl');$pcap=Join-Path $captureDirectory ($name+'.pcapng')
 Command @('start','--capture','--comp','nics','--pkt-size','128','--file-size','8','--log-mode','circular','--file-name',$etl);$script:capture=$true
 try{& $work;Start-Sleep -Seconds 1}finally{if($script:capture){Command @('stop');$script:capture=$false}}
 Command @('etl2pcap',$etl,'--out',$pcap)
 $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$exe;$info.Arguments='--capture-summary';$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true
 $p=[Diagnostics.Process]::Start($info);$output=$p.StandardOutput.ReadToEndAsync()
 try{$p.StandardInput.WriteLine((@{File=$pcap;Local=$script:local;Endpoint=$script:endpoint;Port=$port}|ConvertTo-Json -Compress));$p.StandardInput.Close();if(-not $p.WaitForExit(10000)){$p.Kill();throw 'Capture summary deadline'};$summary=$output.GetAwaiter().GetResult()|ConvertFrom-Json;if($p.ExitCode -ne 0){throw 'Unsupported/malformed capture cannot certify absence of traffic'};return $summary}finally{$p.Dispose()}
}
function Probe([bool]$unavailable,[switch]$Retention){
 $arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\CitaLab\NativeAwg\Run-GuestProbe.ps1';if($unavailable){$arguments+=' -WrongPresharedKey'}
 if($Retention){$arguments+=' -Retention'}
 if($Retention -and $InboundLoss){$arguments+=' -InboundLoss'}
 $p=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList $arguments -RedirectStandardOutput (Join-Path $root 'capture-probe.out') -RedirectStandardError (Join-Path $root 'capture-probe.err') -PassThru;$null=$p.Handle
 try{if(-not $p.WaitForExit(45000)){throw 'Probe deadline'};if($p.ExitCode -ne 0){throw 'Probe acceptance failed'}}finally{$p.Dispose()}
}
function DirectUdpControl{
 $udp=New-Object Net.Sockets.UdpClient
 try{
  $udp.Client.ReceiveTimeout=2500;$udp.Connect($script:controlDns,53)
  [byte[]]$query=@(97,98,1,0,0,1,0,0,0,0,0,0,7,101,120,97,109,112,108,101,3,99,111,109,0,0,1,0,1)
  $null=$udp.Send($query,$query.Length);$remote=New-Object Net.IPEndPoint([Net.IPAddress]::Any,0);$reply=$udp.Receive([ref]$remote)
  if($reply.Length -lt 12 -or $reply[0] -ne 97 -or $reply[1] -ne 98 -or ($reply[2] -band 128) -eq 0){throw 'Direct UDP control response invalid'}
 }finally{$udp.Dispose()}
}
try{
 # No filter commands: do not clear or replace someone else's capture filters.
 # The direct positive control must demonstrate that TCP packets are observable.
 $nic=Get-NetIPConfiguration|Where-Object {$_.NetAdapter.HardwareInterface -and $_.NetAdapter.Status -eq 'Up' -and $_.IPv4DefaultGateway}|Select-Object -First 1
 $script:local=[string]$nic.IPv4Address[0].IPAddress
 $config=[IO.File]::ReadAllText((Join-Path $root 'private\peer.conf'))
 if($config -notmatch '(?m)^Endpoint\s*=\s*(\d+\.\d+\.\d+\.\d+):(\d+)\s*$'){throw 'Literal endpoint required'}
 $script:endpoint=$Matches[1];$port=[int]$Matches[2]
 if($config -notmatch '(?m)^DNS\s*=\s*(\d+\.\d+\.\d+\.\d+)'){throw 'Literal DNS required for UDP positive control'}
 $script:controlDns=$Matches[1];$config=$null
 $report.control=Capture 'control' {& curl.exe --silent --show-error --ipv4 --noproxy '*' --connect-timeout 3 --max-time 8 https://www.cloudflare.com/cdn-cgi/trace *> (Join-Path $root 'private\control-response.txt');if($LASTEXITCODE -ne 0){throw 'Direct control request failed'};DirectUdpControl} $port
 if($report.control.otherTcp -le 0 -or $report.control.otherUdp -le 0){throw 'Capture positive control did not observe both direct TCP and UDP'}
 if($RetentionOnly){
  $report.retention=Capture 'retention' {Probe $false -Retention} $port
  $report.retentionProbe=[IO.File]::ReadAllText((Join-Path $root 'probe-retention.json'))|ConvertFrom-Json
  $report.success=[bool]($report.retention.tunnelUdp -gt 0 -and $report.retention.otherTcp -eq 0 -and $report.retention.otherUdp -eq 0 -and $report.retentionProbe.success)
 }else{
 $report.native=Capture 'native' {Probe $false} $port
 $report.unavailable=Capture 'wrong-key' {Probe $true} $port
 $report.nativeProbe=[IO.File]::ReadAllText((Join-Path $root 'probe-result.json'))|ConvertFrom-Json
 $report.unavailableProbe=[IO.File]::ReadAllText((Join-Path $root 'probe-wrong-key.json'))|ConvertFrom-Json
 $report.negativeScenario='Wrong in-memory PSK on original endpoint, not unavailable-port proof'
 $report.success=[bool]($report.native.tunnelUdp -gt 0 -and $report.unavailable.tunnelUdp -gt 0 -and $report.native.otherTcp -eq 0 -and $report.native.otherUdp -eq 0 -and $report.unavailable.otherTcp -eq 0 -and $report.unavailable.otherUdp -eq 0)
 }
}catch{$report.error=$_.Exception.Message}
finally{
 if($capture){try{Command @('stop');$capture=$false}catch{$report.cleanupError='Capture stop unconfirmed'}}
 $file=if($RetentionOnly){'capture-retention.json'}else{'capture-result.json'}
 $report.captureStopped=-not $capture;$report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 8|Set-Content (Join-Path $root $file) -Encoding UTF8
}
if(-not $report.success){exit 1}
