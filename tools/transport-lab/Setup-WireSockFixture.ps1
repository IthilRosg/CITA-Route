$ErrorActionPreference='Stop'
# This installs a driver. Never run it on the user's VPN computer or a self-hosted runner.
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:CITA_DISPOSABLE_WIRESOCK_LAB -ne '1'){
    throw 'WireSock installation fixtures require the dedicated disposable GitHub-hosted Windows job.'
}
$project=Split-Path (Split-Path $PSScriptRoot)
$folder=Join-Path $project 'runtime\wiresock-fixture';New-Item -ItemType Directory -Path $folder -Force|Out-Null
$setup=Join-Path $folder 'wiresock-sdk-3.6.1.1-x64.exe'
# Pinned Microsoft winget manifest NTKERNEL.WireSockVPNClientCLI/3.6.1.
$url='https://wiresock.net/_api/download-release.php?product=wiresock-secure-connect-sdk&platform=x64&version=3.6.1.1&channel=winget'
Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $setup
if((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne 'BDB676263FFFA4E36EC6B51155A8AFE2AC6D5680DC6D22008DE9C75C8F533ECC'){throw 'WireSock fixture installer hash differs from the pinned manifest.'}
if((Get-AuthenticodeSignature -LiteralPath $setup).Status -ne 'Valid'){throw 'WireSock fixture installer signature is not valid.'}
$process=Start-Process -FilePath $setup -ArgumentList '/S' -WindowStyle Hidden -PassThru
try{if(-not $process.WaitForExit(120000)){throw 'WireSock SDK installer timed out.'};if($process.ExitCode -ne 0){throw ('WireSock SDK installer exit code '+$process.ExitCode)}}finally{$process.Dispose()}
$roots=@(Get-ChildItem -LiteralPath $env:ProgramFiles -Directory | Where-Object Name -like '*WireSock*')
$clients=@($roots | ForEach-Object {Get-ChildItem -LiteralPath $_.FullName -Recurse -File -Filter 'wiresock-client.exe'})
if($clients.Count -ne 1){throw ('Expected one SDK reference client; discovered '+$clients.Count)}
$record=@{Schema=1;Cli=$clients[0].FullName;Version=$clients[0].VersionInfo.FileVersion;InstallerVerified=$true;DriverTrafficVerified=$false}
[IO.File]::WriteAllText((Join-Path $folder 'install.json'),($record|ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
Write-Output ('SDK fixture client: '+$record.Cli+'; version '+$record.Version)
Write-Output 'PASS: pinned signed WireSock SDK installed only on disposable runner. Tunnel traffic is a separate check.'
