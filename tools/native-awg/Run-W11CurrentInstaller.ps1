$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$version=(Get-Content -LiteralPath (Join-Path $root 'product.json') -Raw|ConvertFrom-Json).version
if($version -notmatch '^\d+\.\d+\.\d+$'){throw 'Unexpected candidate version'}
$setup=Join-Path $root ('dist\CITA-Route-'+$version+'-win-x64-isolated-test-setup.exe')
if(-not(Test-Path -LiteralPath $setup)){throw 'Candidate test installer missing'}
. (Join-Path $root 'runtime\vm-lab\W11-Calls.ps1')
try{
 W11-Code 'New-Item -ItemType Directory -Path C:\CitaLab\InstallerCurrent -Force|Out-Null;if(Test-Path -LiteralPath C:\CitaLab\InstallerCurrent\result.json){Remove-Item -LiteralPath C:\CitaLab\InstallerCurrent\result.json -Force};exit 0'|Out-Null
 W11-Call @('copyFileFromHostToGuest',$labVmx,$setup,'C:\CitaLab\InstallerCurrent\candidate-test-setup.exe')|Out-Null
 W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root 'Verify-Installer.ps1'),'C:\CitaLab\InstallerCurrent\Verify-Installer.ps1')|Out-Null
 $code=@'
$ErrorActionPreference='Stop';$result=@{success=$false}
try{
 if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
 # Use the actual service location. A foreign installed service must cause
 # an ownership refusal; do not alter environment paths to bypass that check.
 & 'C:\CitaLab\InstallerCurrent\Verify-Installer.ps1' -Setup 'C:\CitaLab\InstallerCurrent\candidate-test-setup.exe' -NativeStandalone -IsolatedTest
 $result.success=$true
}catch{$result.errorType=$_.Exception.GetType().Name;$result.errorId=$_.FullyQualifiedErrorId;$result.message=$_.Exception.Message;$result.line=$_.InvocationInfo.ScriptLineNumber}
$result|ConvertTo-Json|Set-Content 'C:\CitaLab\InstallerCurrent\result.json' -Encoding UTF8
'@
 W11-Code $code|Out-Null
 $file=Join-Path $root ('runtime\native-installer-current-'+[guid]::NewGuid().ToString('N')+'.json')
 W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\InstallerCurrent\result.json',$file)|Out-Null
 $result=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json
 $result|Add-Member -NotePropertyName version -NotePropertyValue $version
 $result|Add-Member -NotePropertyName setupSha256 -NotePropertyValue (Get-FileHash -LiteralPath $setup).Hash
 $result|ConvertTo-Json|Set-Content -LiteralPath $file -Encoding UTF8
 $result|Select-Object success,version,errorType,errorId,message,line|ConvertTo-Json -Compress|Write-Output
 if(-not $result.success){throw 'Current installer fixture failed'}
}finally{$labCredential=$null;$labVmCredential=$null}
