#requires -Version 5.1
# VM-only, no installed client/VPN dependency. Fixed public origin and one /32.
param([ValidateSet('cloudflare-quic.com','www.cloudflare.com')][string]$Domain='cloudflare-quic.com')
$ErrorActionPreference='Stop'
if(([string](Get-ItemProperty 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS').SystemManufacturer) -notmatch 'VMware'){throw 'VMware guest required.'}
if(-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Elevated guest token required.'}
function Get-PublicLabNetwork {
 $routes=@(Get-NetRoute -PolicyStore ActiveStore|Where-Object {$_.DestinationPrefix -in @('0.0.0.0/0','0.0.0.0/1','128.0.0.0/1','::/0','::/1','8000::/1')}|Sort-Object InterfaceIndex,DestinationPrefix,NextHop|ForEach-Object {('{0}|{1}|{2}|{3}' -f $_.InterfaceIndex,$_.DestinationPrefix,$_.NextHop,$_.RouteMetric)})
 $dns=@(Get-DnsClientServerAddress|Where-Object {$_.InterfaceAlias -ne 'Cita-PublicWeb'}|Sort-Object InterfaceIndex,AddressFamily|ForEach-Object {('{0}|{1}|{2}' -f $_.InterfaceIndex,$_.AddressFamily,($_.ServerAddresses -join ','))})
 @{routes=($routes -join ';');dns=($dns -join ';')}
}
$report=@{success=$false;startedUtc=[datetime]::UtcNow.ToString('o');domain=$Domain};$run=$null;$started=$false;$target=$null
try{
 if(Get-NetRoute -PolicyStore ActiveStore|Where-Object {$_.InterfaceAlias -eq 'Cita-PublicWeb'}){throw 'Previous public fixture route is still active.'}
 $network=Get-PublicLabNetwork
 $manifest=Get-Content 'C:\CitaLab\Adaptive\browser\stage.json' -Raw|ConvertFrom-Json
 if($manifest.files.Count -ne 7){throw 'Unexpected public fixture manifest.'}
 foreach($file in $manifest.files){if((Get-FileHash -LiteralPath $file.guest).Hash -ne $file.sha256){throw 'Candidate fixture checksum differs.'}}
 $address=@(Resolve-DnsName -Name $Domain -Type A -DnsOnly -QuickTimeout|Where-Object {$_.Type -eq 'A'}|Select-Object -ExpandProperty IPAddress)[0]
 if(-not $address){throw 'Public test origin DNS unavailable.'}
 $wan=Get-NetRoute -DestinationPrefix '0.0.0.0/0'|Sort-Object RouteMetric|Select-Object -First 1
 if(-not $wan -or $wan.InterfaceAlias -eq 'Cita-PublicWeb'){throw 'Physical guest default interface unavailable.'}
 $root='C:\CitaLab\PublicWebTun\'+[guid]::NewGuid().ToString('N');[void][IO.Directory]::CreateDirectory($root);$report.labRoot=$root
 $target=@{domain=$Domain;address=$address;bindInterface=$wan.InterfaceAlias};[IO.File]::WriteAllText(($root+'\public-target.json'),($target|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
 $run=Start-Process 'C:\CitaLab\Dotnet\dotnet.exe' -ArgumentList ('"C:\CitaLab\Adaptive\browser\RoutePilot.Agent.dll" --public-browser-tun-lab "'+$root+'"') -WindowStyle Hidden -PassThru -RedirectStandardOutput 'C:\CitaLab\public-browser.out' -RedirectStandardError 'C:\CitaLab\public-browser.err';$null=$run.Handle;$started=$true
 if(-not $run.WaitForExit(235000)){$run.Kill();throw 'Public browser fixture deadline exceeded.'};$run.Refresh();$report.exitCode=$run.ExitCode
 if(Test-Path ($root+'\result.json')){$report.result=Get-Content ($root+'\result.json') -Raw|ConvertFrom-Json}
 if($run.ExitCode -ne 0){throw ([IO.File]::ReadAllText('C:\CitaLab\public-browser.err'))}
 if(-not $report.result.success -or -not $report.result.automaticPublicCertificateFallbackVerified){throw 'Complete browser protocol cycle was not verified.'}
 $report.success=$true
}catch{$report.error=$_.Exception.Message}
finally{
 if($run){$run.Dispose()}
 if($started){
  $remaining=@(Get-NetRoute -DestinationPrefix ($target.address+'/32') -ErrorAction SilentlyContinue|Where-Object {$_.InterfaceAlias -eq 'Cita-PublicWeb'});$report.gracefulScopedRouteCleanup=($remaining.Count -eq 0)
  foreach($route in $remaining){Remove-NetRoute -InterfaceIndex $route.InterfaceIndex -DestinationPrefix ($target.address+'/32') -NextHop $route.NextHop -Confirm:$false}
  $after=Get-PublicLabNetwork;$report.defaultRoutesUnchanged=($network.routes -eq $after.routes);$report.dnsUnchanged=($network.dns -eq $after.dns)
  if(-not $report.gracefulScopedRouteCleanup -or -not $report.defaultRoutesUnchanged -or -not $report.dnsUnchanged){$report.success=$false;$report.cleanupMismatch=$true}
 }
}
$report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 12|Set-Content 'C:\CitaLab\public-browser-result.json' -Encoding UTF8
