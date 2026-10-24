param([ValidateSet('Begin','Restore')][string]$Action,[Parameter(Mandatory)][string]$StatePath,[switch]$Delayed)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$path=[IO.Path]::GetFullPath($StatePath)
if($path -notmatch '^C:\\CitaLab\\NativeAwg\\private\\ip-[a-f0-9]{32}\.json$'){throw 'Private migration state path required'}
if($Delayed){Start-Sleep -Seconds 35}
if($Action -eq 'Begin'){
 if(Test-Path -LiteralPath $path){throw 'State already exists'}
 $nics=@(Get-NetIPConfiguration|Where-Object {$_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up'})
 if($nics.Count -ne 1 -or @($nics[0].IPv4Address).Count -ne 1){throw 'Single gateway IPv4 required'}
 $nic=$nics[0];$index=$nic.InterfaceIndex;$original=$nic.IPv4Address[0].IPAddress
 if($original -ne '192.168.252.128' -or $nic.IPv4Address[0].PrefixLength -ne 24 -or $nic.IPv4DefaultGateway.NextHop -ne '192.168.252.2'){throw 'Tested VMnet8 topology differs'}
 $persistentDhcp=[string](Get-NetIPInterface -InterfaceIndex $index -AddressFamily IPv4 -PolicyStore PersistentStore -ErrorAction SilentlyContinue).Dhcp
 if((Get-NetIPInterface -InterfaceIndex $index -AddressFamily IPv4).Dhcp -ne 'Enabled' -or $persistentDhcp -notin @('','Enabled')){throw 'Original DHCP or inherited DHCP required'}
 $temporary='192.168.252.77' # outside the inspected VMnet8 DHCP range .128-.254
 if(Test-Connection -ComputerName $temporary -Count 1 -Quiet -ErrorAction SilentlyContinue){throw 'Temporary address already replies'}
 $state=@{guid=([guid]$nic.NetAdapter.InterfaceGuid).ToString('D');original=$original;temporary=$temporary;gateway='192.168.252.2';routeAdded=$false;restored=$false;persistentDhcp=$persistentDhcp;dns=@((Get-DnsClientServerAddress -InterfaceIndex $index -AddressFamily IPv4).ServerAddresses)}
 $state|ConvertTo-Json|Set-Content -LiteralPath $path -Encoding UTF8
 $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName='C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe';$info.Arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Action Restore -StatePath "'+$path+'" -Delayed';$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden'
 $helper=[Diagnostics.Process]::Start($info);$state.helperPid=$helper.Id;$state.helperTicks=$helper.StartTime.ToUniversalTime().Ticks;$helper.Dispose();$state|ConvertTo-Json|Set-Content -LiteralPath $path -Encoding UTF8
 Set-NetIPInterface -InterfaceIndex $index -AddressFamily IPv4 -Dhcp Disabled -PolicyStore ActiveStore
 Get-NetIPAddress -InterfaceIndex $index -AddressFamily IPv4 -IPAddress $original -ErrorAction SilentlyContinue|Remove-NetIPAddress -Confirm:$false -PolicyStore ActiveStore
 New-NetIPAddress -InterfaceIndex $index -IPAddress $temporary -PrefixLength 24 -PolicyStore ActiveStore|Out-Null
 for($i=0;$i -lt 20;$i++){$ip=Get-NetIPAddress -InterfaceIndex $index -IPAddress $temporary;if($ip.AddressState -eq 'Preferred'){break};if($ip.AddressState -eq 'Duplicate'){throw 'Duplicate temporary address detected'};Start-Sleep -Milliseconds 200}
 if($ip.AddressState -ne 'Preferred'){throw 'DAD did not finish'}
 if(-not (Get-NetRoute -InterfaceIndex $index -DestinationPrefix '0.0.0.0/0' -NextHop $state.gateway -ErrorAction SilentlyContinue)){
  $state.routeAdded=$true;$state|ConvertTo-Json|Set-Content -LiteralPath $path -Encoding UTF8
  New-NetRoute -InterfaceIndex $index -DestinationPrefix '0.0.0.0/0' -NextHop $state.gateway -PolicyStore ActiveStore|Out-Null
 }
 # Probe the physical next hop, not a public /32 that the scoped TUN may own.
 $state.changed=[bool](-not (Get-NetIPAddress -InterfaceIndex $index -IPAddress $original -ErrorAction SilentlyContinue) -and (@(Find-NetRoute -RemoteIPAddress $state.gateway|Where-Object {$_.IPAddress}).IPAddress -contains $temporary))
 $state|ConvertTo-Json|Set-Content -LiteralPath $path -Encoding UTF8
 if(-not $state.changed){throw 'Source-address change not confirmed'}
 [pscustomobject]$state;return
}
if(-not (Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -gt 4096){throw 'Missing/oversized restore state'}
$state=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
if($state.original -ne '192.168.252.128' -or $state.temporary -ne '192.168.252.77' -or $state.gateway -ne '192.168.252.2'){throw 'Unexpected restore target'}
$mutex=[Threading.Mutex]::new($false,('Local\CitaLabIPv4-'+$state.guid));$locked=$false
try{
 $locked=$mutex.WaitOne(20000);if(-not $locked){throw 'Restore already running'}
 $adapter=@(Get-NetAdapter -IncludeHidden|Where-Object {[guid]$_.InterfaceGuid -eq [guid]$state.guid});if($adapter.Count -ne 1){throw 'Restore adapter differs'};$index=$adapter[0].InterfaceIndex
 if($state.routeAdded){Get-NetRoute -InterfaceIndex $index -DestinationPrefix '0.0.0.0/0' -NextHop $state.gateway -PolicyStore ActiveStore -ErrorAction SilentlyContinue|Where-Object {$_.Protocol -eq 'NetMgmt'}|Remove-NetRoute -Confirm:$false}
 Get-NetIPAddress -InterfaceIndex $index -IPAddress $state.temporary -PolicyStore ActiveStore -ErrorAction SilentlyContinue|Remove-NetIPAddress -Confirm:$false -PolicyStore ActiveStore
 Set-NetIPInterface -InterfaceIndex $index -AddressFamily IPv4 -Dhcp Enabled -PolicyStore ActiveStore
 $config=Get-CimInstance Win32_NetworkAdapterConfiguration|Where-Object {$_.InterfaceIndex -eq $index};if($config){Invoke-CimMethod -InputObject $config -MethodName RenewDHCPLease|Out-Null}
 for($i=0;$i -lt 40;$i++){if(Get-NetIPAddress -InterfaceIndex $index -IPAddress $state.original -ErrorAction SilentlyContinue){break};Start-Sleep -Milliseconds 250}
 $currentDns=@((Get-DnsClientServerAddress -InterfaceIndex $index -AddressFamily IPv4).ServerAddresses)
 $persistentAfter=[string](Get-NetIPInterface -InterfaceIndex $index -AddressFamily IPv4 -PolicyStore PersistentStore -ErrorAction SilentlyContinue).Dhcp
 $state.restored=[bool]((Get-NetIPInterface -InterfaceIndex $index -AddressFamily IPv4).Dhcp -eq 'Enabled' -and $persistentAfter -eq $state.persistentDhcp -and (Get-NetIPAddress -InterfaceIndex $index -IPAddress $state.original -ErrorAction SilentlyContinue) -and -not (Get-NetIPAddress -InterfaceIndex $index -IPAddress $state.temporary -ErrorAction SilentlyContinue) -and (($currentDns|Sort-Object)-join ',') -eq (($state.dns|Sort-Object)-join ','))
 $state|ConvertTo-Json|Set-Content -LiteralPath $path -Encoding UTF8
 if($state.restored -and $state.helperPid -ne $PID){$helper=Get-Process -Id $state.helperPid -ErrorAction SilentlyContinue;if($helper -and $helper.StartTime.ToUniversalTime().Ticks -eq $state.helperTicks){Stop-Process -Id $helper.Id -Force}}
 if(-not $state.restored){throw 'DHCP/address/DNS restoration incomplete'}
 $state
}finally{if($locked){$mutex.ReleaseMutex()};$mutex.Dispose()}
