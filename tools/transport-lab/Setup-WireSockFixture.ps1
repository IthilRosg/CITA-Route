param([switch]$Uninstall)
$ErrorActionPreference='Stop'
# This installs a driver. Never run it on the user's VPN computer or a self-hosted runner.
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:CITA_DISPOSABLE_WIRESOCK_LAB -ne '1'){
    throw 'WireSock installation fixtures require the dedicated disposable GitHub-hosted Windows job.'
}
$project=Split-Path (Split-Path $PSScriptRoot)
$folder=Join-Path $project 'runtime\wiresock-fixture';New-Item -ItemType Directory -Path $folder -Force|Out-Null
$recordPath=Join-Path $folder 'install.json'
if($Uninstall){
    if(-not(Test-Path -LiteralPath $recordPath)){return}
    $record=Get-Content -LiteralPath $recordPath -Raw|ConvertFrom-Json
    $uninstaller=[IO.Path]::GetFullPath($record.Uninstaller)
    if((Get-FileHash -LiteralPath $uninstaller).Hash -ne $record.UninstallerHash){throw 'SDK fixture uninstaller identity changed.'}
    if($record.UninstallKind -eq 'Msi'){
        if($uninstaller -ine (Join-Path $env:SystemRoot 'System32\msiexec.exe') -or $record.ProductCode -notmatch '^\{[a-fA-F0-9-]{36}\}$'){throw 'Invalid owned SDK MSI identity.'}
        $uninstallArguments='/x '+$record.ProductCode+' /qn /norestart'
    }elseif($record.UninstallKind -eq 'PrivateExe'){
        if(-not $uninstaller.StartsWith(($record.InstallRoot.TrimEnd('\')+'\'),[StringComparison]::OrdinalIgnoreCase)){throw 'SDK uninstaller left its package root.'}
        $uninstallArguments=$record.UninstallArguments
    }elseif($record.UninstallKind -eq 'PinnedCachedInstaller'){
        if($record.UninstallerHash -ne 'BDB676263FFFA4E36EC6B51155A8AFE2AC6D5680DC6D22008DE9C75C8F533ECC'){throw 'Cached SDK installer is not the pinned source.'}
        $uninstallArguments=$record.UninstallArguments
    }else{throw 'Unknown fixture uninstall mode.'}
    $process=Start-Process -FilePath $uninstaller -ArgumentList $uninstallArguments -WindowStyle Hidden -PassThru
    try{if(-not $process.WaitForExit(120000)){throw 'WireSock fixture uninstaller timed out.'};if($process.ExitCode -notin @(0,3010)){throw ('SDK uninstaller exit code '+$process.ExitCode)}}finally{$process.Dispose()}
    Write-Output 'PASS: the fixture SDK uninstaller completed on the disposable VM.'
    return
}
$setup=Join-Path $folder 'wiresock-sdk-3.6.1.1-x64.exe'
# Pinned Microsoft winget manifest NTKERNEL.WireSockVPNClientCLI/3.6.1.
$url='https://wiresock.net/_api/download-release.php?product=wiresock-secure-connect-sdk&platform=x64&version=3.6.1.1&channel=winget'
Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $setup
if((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne 'BDB676263FFFA4E36EC6B51155A8AFE2AC6D5680DC6D22008DE9C75C8F533ECC'){throw 'WireSock fixture installer hash differs from the pinned manifest.'}
if((Get-AuthenticodeSignature -LiteralPath $setup).Status -ne 'Valid'){throw 'WireSock fixture installer signature is not valid.'}
$registryPaths=@('HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')
$previousKeys=@(Get-ItemProperty $registryPaths -ErrorAction SilentlyContinue|ForEach-Object PSPath)
$previousWireSock=@(Get-ItemProperty $registryPaths -ErrorAction SilentlyContinue|Where-Object {$_.DisplayName -like '*WireSock*'})
Write-Output ('WireSock uninstall entries before install: '+$previousWireSock.Count)
$process=Start-Process -FilePath $setup -ArgumentList '/S' -WindowStyle Hidden -PassThru
try{if(-not $process.WaitForExit(120000)){throw 'WireSock SDK installer timed out.'};if($process.ExitCode -ne 0){throw ('WireSock SDK installer exit code '+$process.ExitCode)}}finally{$process.Dispose()}
$roots=@(Get-ChildItem -LiteralPath $env:ProgramFiles -Directory | Where-Object Name -like '*WireSock*')
$clients=@($roots | ForEach-Object {Get-ChildItem -LiteralPath $_.FullName -Recurse -File -Filter 'wiresock-client.exe'})
if($clients.Count -ne 1){throw ('Expected one SDK reference client; discovered '+$clients.Count)}
$entry=@(Get-ItemProperty $registryPaths -ErrorAction SilentlyContinue | Where-Object {$_.DisplayName -like '*WireSock*' -and $_.PSPath -notin $previousKeys})
Write-Output ('New WireSock uninstall entries: '+$entry.Count)
Get-ItemProperty $registryPaths -ErrorAction SilentlyContinue|Where-Object {$_.DisplayName -like '*WireSock*'}|ForEach-Object {
    [pscustomobject]@{Name=$_.DisplayName;ProductCode=$_.PSChildName;WindowsInstaller=$_.WindowsInstaller;UninstallString=$_.UninstallString;InstallLocation=$_.InstallLocation}|ConvertTo-Json -Compress
}
if($entry.Count -ne 1){throw 'Expected one newly installed WireSock SDK uninstall entry.'}
$entry=$entry[0]
$command=if($entry.QuietUninstallString){[string]$entry.QuietUninstallString}else{[string]$entry.UninstallString}
$arguments=''
if($command -match '^"([^"]+\.exe)"\s*(.*)$'){$uninstaller=$Matches[1];$arguments=$Matches[2]}
elseif($command -match '^(.+?\.exe)\s*(.*)$'){$uninstaller=$Matches[1];$arguments=$Matches[2]}
else{throw 'Unrecognized SDK fixture uninstall entry.'}
if($uninstaller -ieq 'msiexec.exe'){$uninstaller=Join-Path $env:SystemRoot 'System32\msiexec.exe'}
$uninstaller=[IO.Path]::GetFullPath($uninstaller)
Write-Output ('SDK fixture uninstall executable: '+$uninstaller)
$installRoots=@($roots|Where-Object {$clients[0].FullName.StartsWith(($_.FullName.TrimEnd('\')+'\'),[StringComparison]::OrdinalIgnoreCase)})
if($installRoots.Count -ne 1){throw 'SDK client has no unique installed package root.'}
$installRoot=$installRoots[0].FullName
$uninstallHash=(Get-FileHash -LiteralPath $uninstaller).Hash;$kind='';$productCode=''
if($uninstaller -ieq (Join-Path $env:SystemRoot 'System32\msiexec.exe') -and $entry.WindowsInstaller -eq 1 -and $entry.PSChildName -match '^\{[a-fA-F0-9-]{36}\}$'){
    $kind='Msi';$productCode=$entry.PSChildName
}elseif($uninstaller.StartsWith(($installRoot.TrimEnd('\')+'\'),[StringComparison]::OrdinalIgnoreCase)){
    $kind='PrivateExe';if(-not $entry.QuietUninstallString){$arguments+=' /S'}
}elseif($uninstaller.StartsWith((Join-Path $env:ProgramData 'Package Cache\'),[StringComparison]::OrdinalIgnoreCase) -and $uninstallHash -eq 'BDB676263FFFA4E36EC6B51155A8AFE2AC6D5680DC6D22008DE9C75C8F533ECC' -and $arguments -match '/uninstall'){
    $kind='PinnedCachedInstaller';if(-not $entry.QuietUninstallString){$arguments+=' /quiet /norestart'}
}else{throw 'SDK uninstaller is outside its package and is neither its own new MSI entry nor the pinned cached installer.'}
$dnsReady=$false;try{$dnsReady=@(Resolve-DnsName github.com -Type A -DnsOnly -QuickTimeout -ErrorAction Stop).Count -gt 0}catch{}
$record=@{Schema=1;Cli=$clients[0].FullName;Version=$clients[0].VersionInfo.FileVersion;InstallerVerified=$true;DriverTrafficVerified=$false;
    InstallRoot=$installRoot;Uninstaller=$uninstaller;UninstallerHash=$uninstallHash;UninstallKind=$kind;UninstallArguments=$arguments;ProductCode=$productCode;DnsReadyAfterInstall=$dnsReady}
[IO.File]::WriteAllText($recordPath,($record|ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
Write-Output ('SDK fixture client: '+$record.Cli+'; version '+$record.Version)
Write-Output ('DNS ready immediately after SDK installation: '+$dnsReady)
Write-Output 'PASS: pinned signed WireSock SDK installed only on disposable runner. Tunnel traffic is a separate check.'
