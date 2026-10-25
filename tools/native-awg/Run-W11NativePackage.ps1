param([Parameter(Mandatory)][string]$CleanRoot,[switch]$InstallerOnly)
$ErrorActionPreference='Stop';$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$CleanRoot=[IO.Path]::GetFullPath($CleanRoot)
if(-not $CleanRoot.StartsWith((Join-Path $root 'runtime/native-five-clean-'),[StringComparison]::OrdinalIgnoreCase)){throw 'Only the isolated native candidate is permitted'}
. (Join-Path $root 'runtime/vm-lab/W11-Calls.ps1')
try{
 W11-Code 'New-Item -ItemType Directory -Path C:\CitaLab\NativeCandidate -Force|Out-Null'|Out-Null
 foreach($name in @('Verify-Portable.ps1','Verify-Installer.ps1','FileStorage.ps1','ConnectionEnvironment.ps1','Configure-Connection.ps1','SmartRouting.ps1','Install-Agent.ps1','Build-Portable.ps1','DesktopRuntime.ps1')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $CleanRoot $name),('C:\CitaLab\NativeCandidate\'+$name))|Out-Null}
 foreach($name in @('CITA-Route-0.6.4-win-x64-framework.zip','CITA-Route-0.6.4-win-x64-test-setup.exe')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $CleanRoot ('dist/'+$name)),('C:\CitaLab\NativeCandidate\'+$name))|Out-Null}
 $code=@'
$ErrorActionPreference='Stop';$root='C:\CitaLab\NativeCandidate';$report=@{success=$false};$rules=@()
try{
 if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong guest'}
 $extract=Join-Path $root ('package-'+[guid]::NewGuid().ToString('N'));Expand-Archive -LiteralPath (Join-Path $root 'CITA-Route-0.6.4-win-x64-framework.zip') -DestinationPath $extract
 $package=@(Get-ChildItem $extract -Directory);if($package.Count -ne 1){throw 'Ambiguous candidate package'};$package=$package[0].FullName
 foreach($relative in @('runtime\agent-build\RoutePilot.Agent.exe','RoutePilot.exe')){
  $name='CitaNativePackage-'+[guid]::NewGuid().ToString('N');New-NetFirewallRule -Name $name -DisplayName $name -Direction Inbound -Action Block -Program (Join-Path $package $relative) -RemoteAddress @('0.0.0.0-126.255.255.255','128.0.0.0-255.255.255.255','::','::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff') -Profile Any|Out-Null;$rules+=$name
 }
 if('__INSTALLER_ONLY__' -ne 'True'){
  & (Join-Path $root 'Verify-Portable.ps1') -PackageDirectory $package -PackageArchive (Join-Path $root 'CITA-Route-0.6.4-win-x64-framework.zip') -AllowFrameworkDependent|Out-Null;$report.portableVerified=$true
 }else{$report.portableSkipped=$true}
 & (Join-Path $root 'Verify-Installer.ps1') -Setup (Join-Path $root 'CITA-Route-0.6.4-win-x64-test-setup.exe')|Out-Null;$report.installerVerified=$true
 $report.success=$true
}catch{$report.errorType=$_.Exception.GetType().Name;$report.errorId=$_.FullyQualifiedErrorId;$report.failedLine=$_.InvocationInfo.ScriptLineNumber}
finally{foreach($rule in $rules){Remove-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue};$report|ConvertTo-Json -Depth 4|Set-Content (Join-Path $root 'package-result.json') -Encoding UTF8}
'@
 W11-Code ($code.Replace('__INSTALLER_ONLY__',$InstallerOnly.ToString()))|Out-Null
 $file=Join-Path $root ('runtime/native-package-'+[guid]::NewGuid().ToString('N')+'.json');W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeCandidate\package-result.json',$file)|Out-Null;$report=Get-Content $file -Raw|ConvertFrom-Json
 $pins=@{installerSha256=(Get-FileHash (Join-Path $CleanRoot 'dist/CITA-Route-0.6.4-win-x64-test-setup.exe')).Hash;fixtureSha256=(Get-FileHash (Join-Path $CleanRoot 'Verify-Installer.ps1')).Hash;guardSha256=(Get-FileHash (Join-Path $CleanRoot 'installer/InstallationGuard.ps1')).Hash;completedUtc=[DateTime]::UtcNow.ToString('o')}
 foreach($key in $pins.Keys){$report|Add-Member -NotePropertyName $key -NotePropertyValue $pins[$key]}
 $report|ConvertTo-Json|Set-Content $file -Encoding UTF8;$report|ConvertTo-Json -Compress|Write-Output;if(-not $report.success){throw 'Native candidate package acceptance incomplete'}
}finally{$labCredential=$null;$labVmCredential=$null}
