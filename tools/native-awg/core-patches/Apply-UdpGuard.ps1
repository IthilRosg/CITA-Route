param([Parameter(Mandatory)][string]$Sing)
$ErrorActionPreference='Stop'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'udp_guard.go') -Destination (Join-Path $Sing 'protocol\socks\cita_udp_guard.go')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'udp_guard_test.go') -Destination (Join-Path $Sing 'protocol\socks\cita_udp_guard_test.go')
$file=Join-Path $Sing 'protocol\socks\handshake.go';$text=[IO.File]::ReadAllText($file).Replace("`r`n","`n");$anchor="`t`t`tserverConn := bufio.NewServerPacketConn(udpConn)"
if(([regex]::Matches($text,[regex]::Escape($anchor))).Count -ne 1){throw 'Pinned SOCKS UDP guard context differs'}
$patch=@'
            if os.Getenv("CITA_NATIVE_UDP_LAB") == "1" {
                guarded,guardErr:=newCitaUDPGuard(udpConn,conn.RemoteAddr(),request.Destination)
                if guardErr!=nil{udpConn.Close();return guardErr};udpConn=guarded
            }
'@
[IO.File]::WriteAllText($file,$text.Replace($anchor,$patch+"`n"+$anchor),[Text.UTF8Encoding]::new($false))
$text=[IO.File]::ReadAllText($file)
$old="`t`t`tif err != nil {`n`t`t`t`treturn E.Cause(err, `"socks5: write response`")`n`t`t`t}"
if(([regex]::Matches($text,[regex]::Escape($old))).Count -ne 1){throw 'Pinned SOCKS UDP write cleanup context differs'}
[IO.File]::WriteAllText($file,$text.Replace($old,"`t`t`tif err != nil {`n`t`t`t`tudpConn.Close()`n`t`t`t`treturn E.Cause(err, `"socks5: write response`")`n`t`t`t}"),[Text.UTF8Encoding]::new($false))
