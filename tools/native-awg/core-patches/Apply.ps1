param([Parameter(Mandatory)][string]$Core)
$ErrorActionPreference='Stop'
function Patch([string]$File,[string]$Old,[string]$New){
 $path=Join-Path $Core $File;$text=[IO.File]::ReadAllText($path).Replace("`r`n","`n")
 if(([regex]::Matches($text,[regex]::Escape($Old))).Count -ne 1){throw "Native retention patch context differs: $File"}
 [IO.File]::WriteAllText($path,$text.Replace($Old,$New),[Text.UTF8Encoding]::new($false))
}
foreach($file in @('common\citaretain\lease.go','route\cita_retention.go','protocol\socks\cita_retention.go','option\cita_retention.go')){New-Item -ItemType Directory -Path (Split-Path (Join-Path $Core $file)) -Force|Out-Null}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'lease.go') -Destination (Join-Path $Core 'common\citaretain\lease.go')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'route_retention.go') -Destination (Join-Path $Core 'route\cita_retention.go')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'socks_retention.go') -Destination (Join-Path $Core 'protocol\socks\cita_retention.go')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'option_retention.go') -Destination (Join-Path $Core 'option\cita_retention.go')
Patch 'option\simple.go' "type SOCKSOutboundOptions struct {`n" "type SOCKSOutboundOptions struct {`n CitaNativeRetention *CitaNativeRetentionOptions ``json:`"cita_native_retention,omitempty`"```n"
foreach($file in @('route\network.go','common\dialer\default.go','protocol\socks\outbound.go')){Patch $file "import (`n" "import (`n `"github.com/sagernet/sing-box/common/citaretain`"`n"}
Patch 'common\dialer\default.go' "`t`tconn = d.connectionManager.TrackConn(conn)" "`t`tconn = d.connectionManager.TrackConn(citaretain.Wrap(ctx,destination.String(),conn))"
Patch 'route\network.go' "`t`tr.connectionManager.CloseAll()" @'
        if citaretain.IsInterfaceReset(ctx) {
            if scoped,ok:=r.connectionManager.(interface{CloseForInterfaceChange()});ok{scoped.CloseForInterfaceChange()}else{r.connectionManager.CloseAll()}
        } else {r.connectionManager.CloseAll()}
'@
Patch 'route\network.go' "`tif resetNetwork {`n`t`tr.ResetNetwork(ctx)`n`t}" "`tif resetNetwork {`n`t`tr.ResetNetwork(citaretain.InterfaceReset(ctx))`n`t}"
Patch 'protocol\socks\outbound.go' "`tuotClient *uot.Client`n" "`tuotClient *uot.Client`n citaLease *citaretain.Lease`n"
Patch 'protocol\socks\outbound.go' "`tvar version socks.Version`n" " lease,leaseErr:=citaLease(options);if leaseErr!=nil{return nil,leaseErr}`n`tvar version socks.Version`n"
Patch 'protocol\socks\outbound.go' "`toutbound := &Outbound{`n" "`toutbound := &Outbound{`n citaLease:lease,`n"
Patch 'protocol\socks\outbound.go' "func (h *Outbound) DialContext(ctx context.Context, network string, destination M.Socksaddr) (net.Conn, error) {`n" "func (h *Outbound) DialContext(ctx context.Context, network string, destination M.Socksaddr) (net.Conn, error) {`n ctx=h.citaContext(ctx)`n"
