$ErrorActionPreference='Stop';$report=@{success=$false;startedUtc=[datetime]::UtcNow.ToString('o')};$root=$null
function Run-Adapter([string]$Verb,[string]$Target){
 $p=Start-Process 'C:\CitaLab\Adaptive\browser\RoutePilot.Agent.exe' -ArgumentList ($Verb+' "'+$Target+'"') -WindowStyle Hidden -PassThru -RedirectStandardOutput 'C:\CitaLab\adapter-setup.out' -RedirectStandardError 'C:\CitaLab\adapter-setup.err';$null=$p.Handle
 try{if(-not $p.WaitForExit(15000)){$p.Kill();throw 'Adapter command deadline'};$p.Refresh();return [int]$p.ExitCode}finally{$p.Dispose()}
}
try{
 if(([string](Get-ItemProperty 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS').SystemManufacturer) -notmatch 'VMware'){throw 'VMware guest required'}
 $env:DOTNET_ROOT='C:\CitaLab\Dotnet';$env:DOTNET_ROOT_X64='C:\CitaLab\Dotnet'
 if(Test-Path 'HKCU:\Software\CITA\Route\BrowserFeedback'){throw 'A previous adapter registration remains'}
 $latest=Get-Content 'C:\CitaLab\browser-tun-result.json' -Raw|ConvertFrom-Json
 if(-not $latest.success -or -not $latest.result.ordinaryExtensionVerified){throw 'Native browser run was not successful'}
 $report.observerRemoved=-not (Test-Path ($latest.labRoot+'\runtime\browser-observer.json'))
 $root='C:\CitaLab\NativeSetup\'+[guid]::NewGuid().ToString('N');[void][IO.Directory]::CreateDirectory($root+'\runtime');[void][IO.Directory]::CreateDirectory($root+'\data')
 @{Mode='Auto';AllowListedDpi=$false;ExperimentalBrowserFeedback=$false}|ConvertTo-Json|Set-Content ($root+'\data\routing-policy.json') -Encoding UTF8
 @{id=[guid]::NewGuid().ToString('N');running=$true}|ConvertTo-Json|Set-Content ($root+'\runtime\session.json') -Encoding UTF8
 $report.activeSessionRefused=(Run-Adapter '--register-browser-feedback' $root) -ne 0
 $report.noPartialRegistration=-not (Test-Path 'HKCU:\Software\CITA\Route\BrowserFeedback')
 @{id=[guid]::NewGuid().ToString('N');running=$false}|ConvertTo-Json|Set-Content ($root+'\runtime\session.json') -Encoding UTF8
 if((Run-Adapter '--register-browser-feedback' $root) -ne 0){throw 'Adapter registration failed'}
 $report.optionEnabled=[bool]((Get-Content ($root+'\data\routing-policy.json') -Raw|ConvertFrom-Json).ExperimentalBrowserFeedback)
 $report.fakeBrowserRejected=(Run-Adapter 'chrome-extension://gokcbalkmlaaakcdnlflpknihmpaekga/' '--parent-window=0') -ne 0
 if((Run-Adapter '--unregister-browser-feedback' $root) -ne 0){throw 'Adapter removal failed'}
 $report.optionDisabled=-not [bool]((Get-Content ($root+'\data\routing-policy.json') -Raw|ConvertFrom-Json).ExperimentalBrowserFeedback)
 $remaining=0;foreach($view in @([Microsoft.Win32.RegistryView]::Registry32,[Microsoft.Win32.RegistryView]::Registry64)){
  $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,$view)
  try{foreach($prefix in @('Software\Google\Chrome\NativeMessagingHosts\','Software\Google\Chrome for Testing\NativeMessagingHosts\','Software\Google\ChromeForTesting\NativeMessagingHosts\','Software\Microsoft\Edge\NativeMessagingHosts\')){$key=$base.OpenSubKey($prefix+'xyz.citaeris.route.feedback');if($key){$remaining++;$key.Dispose()}}}finally{$base.Dispose()}
 }
 $report.registryEntriesRemaining=$remaining
 $report.hostManifestRemoved=-not (Test-Path (Join-Path $env:LOCALAPPDATA 'CITA Route\BrowserFeedback\host.json'))
 $report.nativeProcessesRemaining=@(Get-CimInstance Win32_Process -Filter "Name='RoutePilot.Agent.exe'"|Where-Object {$_.ExecutablePath -eq 'C:\CitaLab\Adaptive\browser\RoutePilot.Agent.exe'}).Count
 $report.success=$report.observerRemoved -and $report.activeSessionRefused -and $report.noPartialRegistration -and $report.optionEnabled -and $report.optionDisabled -and $report.fakeBrowserRejected -and $report.hostManifestRemoved -and $remaining -eq 0 -and $report.nativeProcessesRemaining -eq 0
 if(-not $report.success){throw 'Adapter lifecycle verification failed'}
}catch{$report.error=$_.Exception.Message}
finally{if($root -and (Test-Path 'HKCU:\Software\CITA\Route\BrowserFeedback')){try{$null=Run-Adapter '--unregister-browser-feedback' $root}catch{}}}
$report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 5|Set-Content 'C:\CitaLab\adapter-setup-result.json' -Encoding UTF8