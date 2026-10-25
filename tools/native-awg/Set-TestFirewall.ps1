param([Parameter(Mandatory=$true)][string]$Image)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$expected=[IO.Path]::GetFullPath((Join-Path $root 'runtime/native-awg-unit/native-awg-lab.test.exe'))
if([IO.Path]::GetFullPath($Image) -ne $expected -or -not (Test-Path -LiteralPath $expected -PathType Leaf)){throw 'Unexpected native unit-test image'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent();$principal=[Security.Principal.WindowsPrincipal]::new($identity)
$name='CitaRoute-NativeUnit-LoopbackOnly-v1'
# An explicit block suppresses Windows' application-discovery prompt. These
# ranges exclude IPv4/IPv6 loopback; no host LAN/public inbound access is opened.
$remote=@('0.0.0.0-126.255.255.255','128.0.0.0-255.255.255.255','::','::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff')
$rule=Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
if($rule){
 $app=$rule|Get-NetFirewallApplicationFilter;$address=$rule|Get-NetFirewallAddressFilter
 if($rule.Direction -ne 'Inbound' -or $rule.Action -ne 'Block' -or $rule.Enabled -ne 'True' -or $app.Program -ne $expected -or @(Compare-Object @($address.RemoteAddress|Sort-Object) @($remote|Sort-Object)).Count){throw 'Native unit-test firewall rule changed; tests were not started'}
}else{
 if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
  $arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Image "'+$expected+'"'
  $child=Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList $arguments -PassThru
  try{$null=$child.Handle;$child.WaitForExit();if($child.ExitCode -ne 0){throw 'Native unit-test firewall setup failed; tests were not started'}}finally{$child.Dispose()}
  if(-not (Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue)){throw 'Native unit-test firewall setup was not confirmed; tests were not started'}
  return
 }
 New-NetFirewallRule -Name $name -DisplayName 'CITA Route native unit tests: block external inbound' -Description 'Development test executable only. Loopback tests remain available; no external inbound access is granted.' -Group 'CITA Route development tests' -Program $expected -Direction Inbound -Action Block -RemoteAddress $remote -Profile Any -Enabled True|Out-Null
}
