$ErrorActionPreference='Stop'
$src=Get-Content (Join-Path $PSScriptRoot '..\Install-NativeComponents.ps1') -Raw
$start=$src.IndexOf(' $candidates=@(foreach($route in @(Get-NetRoute')
$end=$src.IndexOf(' $policy=@{Vpn=', $start)
if($start -lt 0 -or $end -lt 0){throw 'Selection block not found'}
$select=[scriptblock]::Create($src.Substring($start,$end-$start)+"`n`$physical[0].InterfaceAlias")
$script:routes=@();$script:adapters=@{};$script:interfaces=@{}
function Get-NetRoute {param($AddressFamily,$DestinationPrefix) $script:routes}
function Get-NetAdapter {param($InterfaceIndex,$ErrorAction) $script:adapters[[int]$InterfaceIndex]}
function Get-NetIPInterface {param($InterfaceIndex,$AddressFamily,$ErrorAction) $script:interfaces[[int]$InterfaceIndex]}
function Add-Route([int]$index,[string]$name,[string]$description,[int]$routeMetric,[int]$interfaceMetric){
 $script:routes+=@([pscustomobject]@{InterfaceIndex=$index;RouteMetric=$routeMetric;NextHop='192.0.2.1'})
 $script:adapters[$index]=[pscustomobject]@{Name=$name;InterfaceDescription=$description;Status='Up'}
 $script:interfaces[$index]=[pscustomobject]@{InterfaceMetric=$interfaceMetric}
}
function Assert-Selected([string]$name){$actual=& $select;if($actual -ne $name){throw 'Wrong uplink selected'}}
function Assert-Rejected([string]$reason){try{& $select|Out-Null;throw 'Expected failure not observed'}catch{if($_.Exception.Message -notmatch $reason){throw}}}
Add-Route 1 'Wi-Fi' 'Wi-Fi adapter' 0 35
Add-Route 2 'Radmin VPN' 'Famatech Radmin VPN Ethernet Adapter' 9256 1
Add-Route 3 'ZeroTier' 'ZeroTier Virtual Port' 9999 35
Assert-Selected 'Wi-Fi'
$script:routes+=@([pscustomobject]@{InterfaceIndex=99;RouteMetric=0;NextHop='192.0.2.1'})
Assert-Selected 'Wi-Fi' # A stale route without an adapter must not win.
$script:routes+=@([pscustomobject]@{InterfaceIndex=1;RouteMetric=0;NextHop='192.0.2.2'})
Assert-Selected 'Wi-Fi' # Equal default routes on the same adapter are not ambiguous.
Add-Route 4 'Ethernet' 'Ethernet' 0 15
Assert-Selected 'Ethernet'
$script:interfaces[4].InterfaceMetric=35
Assert-Rejected 'equally preferred'
$script:adapters[4].Status='Down'
Assert-Selected 'Wi-Fi'
$script:adapters[1].Status='Down'
Assert-Rejected 'No eligible'
'PASS: overlay adapters, preferred uplink, equal metric, disconnected and missing uplink'
