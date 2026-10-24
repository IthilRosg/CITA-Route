param([Parameter(Mandatory)][string]$Core)
$ErrorActionPreference='Stop'
foreach($name in @('native_binding.go','native_binding_test.go')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $Core ('protocol\socks\cita_'+$name))}
$options=@'
package option
type CitaNativeBindingOptions struct { Owner string `json:"owner"` }
'@
[IO.File]::WriteAllText((Join-Path $Core 'option\cita_native_binding.go'),$options,[Text.UTF8Encoding]::new($false))
$file=Join-Path $Core 'protocol\socks\outbound.go';$text=[IO.File]::ReadAllText($file).Replace("`r`n","`n");$anchor='outbound.Register[option.SOCKSOutboundOptions](registry, C.TypeSOCKS, NewOutbound)'
if(([regex]::Matches($text,[regex]::Escape($anchor))).Count -ne 1){throw 'Pinned native outbound registry context differs'}
[IO.File]::WriteAllText($file,$text.Replace($anchor,$anchor+"`n outbound.Register[option.CitaNativeBindingOptions](registry,`"cita_native_lab`",NewCitaNativeBinding)"),[Text.UTF8Encoding]::new($false))
