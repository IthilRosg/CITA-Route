param([string]$Setup='')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$version=(Get-Content -LiteralPath (Join-Path $root 'product.json') -Raw|ConvertFrom-Json).version
if($version -notmatch '^\d+\.\d+\.\d+$'){throw 'Unexpected candidate version'}
$setup=if($Setup){[IO.Path]::GetFullPath($Setup)}else{Join-Path $root ('dist\CITA-Route-'+$version+'-win-x64-isolated-test-setup.exe')}
if(-not $setup.StartsWith(($root+'\'),[StringComparison]::OrdinalIgnoreCase) -or -not $setup.EndsWith('-isolated-test-setup.exe',[StringComparison]::OrdinalIgnoreCase)){throw 'Only a workspace isolated-test installer is permitted'}
$vmx='E:\VM\CITA TEST 2.vmx';$uuid='56 4d d9 7f 71 83 17 95-13 d4 69 a8 08 2a 2f 01'
$credential=Import-Clixml (Join-Path $root 'private\vm-lab\test2-credential.xml')
function CallVm([string[]]$arguments){
 if([IO.File]::ReadAllText($vmx) -notmatch ('uuid.bios = "'+[regex]::Escape($uuid)+'"')){throw 'Wrong test VM'}
 $info=[Diagnostics.ProcessStartInfo]::new('C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe');$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 foreach($item in (@('-T','ws','-gu',$credential.UserName,'-gp',$credential.GetNetworkCredential().Password)+$arguments)){$info.ArgumentList.Add($item)}
 $process=[Diagnostics.Process]::Start($info);try{$out=$process.StandardOutput.ReadToEndAsync();$err=$process.StandardError.ReadToEndAsync();if(-not $process.WaitForExit(245000)){$process.Kill();throw 'Guest operation timeout'};if($process.ExitCode -ne 0){throw ('Guest operation failed: '+$arguments[0])}}finally{$process.Dispose()}
}
try{
 $code='New-Item -ItemType Directory -Path C:\CitaLab\InstallerTest -Force|Out-Null;exit 0'
 CallVm @('runProgramInGuest',$vmx,'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe','-NoProfile','-NonInteractive','-WindowStyle','Hidden','-EncodedCommand',([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))))
 foreach($pair in @(@($setup,'candidate-test-setup.exe'),@((Join-Path $root 'Verify-Installer.ps1'),'Verify-Installer.ps1'))){CallVm @('copyFileFromHostToGuest',$vmx,$pair[0],('C:\CitaLab\InstallerTest\'+$pair[1]))}
 & (Join-Path $root 'runtime\vm-lab\Invoke-Test2.ps1') -ScriptName 'Installer-Run.ps1' -ReportName 'Installer-Run.json' | Out-Null
 $result=Get-Content (Join-Path $root 'runtime\vm-lab\test2\Installer-Run.json') -Raw|ConvertFrom-Json
 $result|Add-Member -NotePropertyName installerSha256 -NotePropertyValue (Get-FileHash -LiteralPath $setup).Hash
 $result|ConvertTo-Json|Set-Content (Join-Path $root 'runtime\native-installer-test2-latest.json') -Encoding UTF8
 $result|Select-Object success,errorType,errorId,message|ConvertTo-Json -Compress|Write-Output
 if(-not $result.success){throw 'Test2 installer acceptance incomplete'}
}finally{$credential=$null}
