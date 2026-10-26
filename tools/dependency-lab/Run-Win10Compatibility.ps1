$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne '7FD94D56-8371-9517-13D4-69A8082A2F01'){throw 'Wrong compatibility guest'}
$root='C:\CitaLab\PlatformInstalled\client';$cli='C:\Program Files\WireSock Secure Connect\command-line\wiresock-connect-cli.exe'
$report=@{success=$false;phase='InstallOld';startedUtc=[DateTime]::UtcNow.ToString('o');scope='Real vendor 3.4.8.1 to 3.6.1.1, synthetic profile, no VPN connection'}
function Save {$report.updatedUtc=[DateTime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 5|Set-Content C:\CitaLab\platform-win10-compatibility.json -Encoding UTF8}
function Setup([string]$path,[string]$expected){
 if((Get-FileHash -LiteralPath $path).Hash -ne $expected){throw 'Vendor artifact hash mismatch'}
 $p=Start-Process $path -ArgumentList '/s' -WindowStyle Hidden -PassThru;$null=$p.Handle
 try{if(-not $p.WaitForExit(90000)){throw 'Vendor installer deadline; do not kill the installer'};$p.Refresh();if($p.ExitCode -notin @(0,3010)){throw 'Vendor installation failed'}}finally{$p.Dispose()}
}
function Discover {
 $text=& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $root 'Configure-Connection.ps1') -Action Discover
 if($LASTEXITCODE -ne 0){throw 'CITA discovery failed'}
 ($text -join "`n")|ConvertFrom-Json
}
function Export([string]$profile,[string]$path){
 & $cli export $profile $path|Out-Null;if($LASTEXITCODE -ne 0){throw 'Synthetic profile export failed'}
 # Compare semantic fields, never emit the generated private test key.
 $text=[IO.File]::ReadAllText($path)
 $fields=@();foreach($line in $text -split '\r?\n'){if($line -match '^\s*(PrivateKey|PublicKey|Address|Endpoint|AllowedIPs)\s*=\s*(.*?)\s*$'){$fields+=($Matches[1].ToLowerInvariant()+'='+$Matches[2].Trim())}}
 if($fields.Count -ne 5){throw 'Synthetic profile fields missing'}
 $sha=[Security.Cryptography.SHA256]::Create();try{([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($fields|Sort-Object)-join "`n")))).Replace('-','')}finally{$sha.Dispose()}
}
try{
 Save
 if(Get-Process smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'Guest route already active'}
 Setup 'C:\CitaLab\wiresock-secure-connect-x64-3.4.8.1.exe' '064E775D362ABBA7F37AB6BDC8EEF71727A407B480EA9FAB082B7BF88EB33E27'
 $report.oldVersion=(Get-Item -LiteralPath $cli).VersionInfo.FileVersion
 $old=Discover;$report.oldCommandsAvailable=$old.ManagementReady;$report.oldCliTrusted=$old.CliTrusted
 if(-not $old.ManagementReady -or -not $old.CliTrusted){throw 'Required commands missing in actual old vendor release'}
 $private='C:\CitaLab\CompatibilityPrivate';New-Item -ItemType Directory -Path $private -Force|Out-Null
 $acl=New-Object Security.AccessControl.DirectorySecurity;$acl.SetAccessRuleProtection($true,$false)
 foreach($sid in @('S-1-5-18','S-1-5-32-544',[Security.Principal.WindowsIdentity]::GetCurrent().User.Value)){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl','ContainerInherit,ObjectInherit','None','Allow')))}
 Set-Acl -LiteralPath $private -AclObject $acl
 $name='CITA-Compat-Fixture-'+[guid]::NewGuid().ToString('N').Substring(0,8);$file=Join-Path $private ($name+'.conf')
 $rng=[Security.Cryptography.RandomNumberGenerator]::Create();try{$first=New-Object byte[] 32;$second=New-Object byte[] 32;$rng.GetBytes($first);$rng.GetBytes($second)}finally{$rng.Dispose()}
 $config="[Interface]`r`nPrivateKey = $([Convert]::ToBase64String($first))`r`nAddress = 10.77.0.2/32`r`n[Peer]`r`nPublicKey = $([Convert]::ToBase64String($second))`r`nEndpoint = 203.0.113.1:51820`r`nAllowedIPs = 0.0.0.0/0`r`n"
 [IO.File]::WriteAllText($file,$config,[Text.UTF8Encoding]::new($false));$config=$null;$first=$null;$second=$null
 & $cli import $file|Out-Null;if($LASTEXITCODE -ne 0){throw 'Synthetic import failed'}
 $before=Export $name (Join-Path $private 'before.conf');$report.phase='Upgrade';Save
 Setup 'C:\CitaLab\wiresock-secure-connect-x64-3.6.1.1.exe' '734F3090BA095A698F7293FF90EBD6DC64FCD646277011DFA3648AC5DBDF5D37'
 $after=Export $name (Join-Path $private 'after.conf');$report.syntheticProfilePreserved=$before -eq $after
 $new=Discover;$report.newVersion=$new.Version;$report.newCommandsAvailable=$new.ManagementReady;$report.syntheticProfileListed=$new.Profiles -contains $name
 if($new.Version -ne '3.6.1.1' -or -not $report.syntheticProfilePreserved -or -not $report.syntheticProfileListed){throw 'Upgrade postconditions failed'}
 $report.success=$true
}catch{$report.error=$_.Exception.Message}
finally{$report.phase='Finished';$report.finishedUtc=[DateTime]::UtcNow.ToString('o');Save}
if(-not $report.success){exit 1}
