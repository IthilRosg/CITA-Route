$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$nics=@(Get-NetIPConfiguration|Where-Object {$_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up'})
if($nics.Count -ne 1){throw 'One physical gateway required'}
$identity=[guid]$nics[0].NetAdapter.InterfaceGuid
$code="Start-Sleep -Seconds 12;if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){exit 1};Get-NetAdapter -IncludeHidden|Where-Object {[guid]`$_.InterfaceGuid -eq [guid]'$identity'}|Enable-NetAdapter -Confirm:`$false"
$helper=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList ('-NoProfile -NonInteractive -EncodedCommand '+[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))) -PassThru
try{
 Get-NetAdapter -IncludeHidden|Where-Object {[guid]$_.InterfaceGuid -eq $identity}|Disable-NetAdapter -Confirm:$false
 Start-Sleep -Seconds 2
}finally{
 Get-NetAdapter -IncludeHidden|Where-Object {[guid]$_.InterfaceGuid -eq $identity}|Enable-NetAdapter -Confirm:$false
 for($i=0;$i -lt 30;$i++){if(Get-NetIPConfiguration|Where-Object {$_.IPv4DefaultGateway -and [guid]$_.NetAdapter.InterfaceGuid -eq $identity}){break};Start-Sleep -Milliseconds 200}
 if(-not (Get-NetIPConfiguration|Where-Object {$_.IPv4DefaultGateway -and [guid]$_.NetAdapter.InterfaceGuid -eq $identity})){throw 'Guest physical link did not return'}
 if(-not $helper.HasExited){Stop-Process -Id $helper.Id -Force};$helper.Dispose()
}
