param([Parameter(Mandatory)][string]$Core)
$ErrorActionPreference='Stop'
$path=Join-Path $Core 'route\network.go';$text=[IO.File]::ReadAllText($path).Replace("`r`n","`n")
$anchor="`tr.router.ResetNetwork()`n}"
if(([regex]::Matches($text,[regex]::Escape($anchor))).Count -ne 1){throw 'Pinned TUN reset barrier context differs'}
$replacement=@'
    r.router.ResetNetwork()
    if citaretain.IsInterfaceReset(ctx) && os.Getenv("CITA_NATIVE_TUN_LAB")=="1" {
        os.Stderr.WriteString("CITA_LAB_INTERFACE_RESET_DONE\n")
    }
}
'@
[IO.File]::WriteAllText($path,$text.Replace($anchor,$replacement),[Text.UTF8Encoding]::new($false))
