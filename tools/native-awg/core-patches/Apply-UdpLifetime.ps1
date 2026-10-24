param([Parameter(Mandatory)][string]$Core,[Parameter(Mandatory)][string]$Sing)
$ErrorActionPreference='Stop'
function Patch([string]$file,[string]$old,[string]$new){
 $text=[IO.File]::ReadAllText($file).Replace("`r`n","`n");if(([regex]::Matches($text,[regex]::Escape($old))).Count -ne 1){throw 'Pinned UDP lifetime source context differs'}
 [IO.File]::WriteAllText($file,$text.Replace($old,$new),[Text.UTF8Encoding]::new($false))
}
foreach($name in @('udp_lifetime.go','udp_lifetime_test.go')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $Sing ('protocol\socks\cita_'+$name))}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'socks_udp_lifetime.go') -Destination (Join-Path $Core 'protocol\socks\cita_udp_lifetime.go')
Patch (Join-Path $Sing 'protocol\socks\client.go') "type Client struct {`n" "type Client struct {`n citaUDPLifetime bool`n"
Patch (Join-Path $Sing 'protocol\socks\client.go') "`t`treturn NewAssociatePacketConn(udpConn, address, tcpConn), nil" "if c.citaUDPLifetime { return newCitaAssociatePacketConn(udpConn,address,tcpConn),nil }`n`t`treturn NewAssociatePacketConn(udpConn, address, tcpConn), nil"
Patch (Join-Path $Core 'option\simple.go') "type SOCKSOutboundOptions struct {`n" "type SOCKSOutboundOptions struct {`n CitaNativeUDPLifetime bool ``json:`"cita_native_udp_lifetime,omitempty`"```n"
Patch (Join-Path $Core 'protocol\socks\outbound.go') "`tuotOptions := common.PtrValueOrDefault(options.UDPOverTCP)" "if err:=citaConfigureUDPLifetime(outbound.client,options);err!=nil{return nil,err}`n`tuotOptions := common.PtrValueOrDefault(options.UDPOverTCP)"
