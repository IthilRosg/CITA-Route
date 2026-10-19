[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Setup)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$setupPath=[IO.Path]::GetFullPath($Setup)
# Real OS integration is tested with a separate AppId and shortcut names only.
if((Get-Item -LiteralPath $setupPath).VersionInfo.ProductName.Trim() -ne 'CITA Route Installer Test' -or -not $setupPath.EndsWith('-test-setup.exe',[StringComparison]::OrdinalIgnoreCase)){throw 'Only the dedicated test installer is allowed in integration fixtures.'}
$label='CITA Route Installer Test'
$links=@((Join-Path ([Environment]::GetFolderPath('Desktop')) ($label+'.lnk')),(Join-Path ([Environment]::GetFolderPath('Programs')) ($label+'.lnk')))
$registry='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CITA.Route.InstallerFixture_is1'
if((Test-Path -LiteralPath $registry) -or @($links | Where-Object {Test-Path -LiteralPath $_}).Count){throw 'Previous installer test still exists. Refusing to replace it.'}
$base=Join-Path $root ('runtime\installer-integration-'+[guid]::NewGuid().ToString('N'))
$target=Join-Path $base 'Клиент с пробелами'
$client=Join-Path $target 'client'
$uninstaller=Join-Path $target 'unins000.exe'
New-Item -ItemType Directory -Path $base -Force | Out-Null
function Assert($Value,[string]$Message){if(-not $Value){throw $Message}}
function Install([string]$Log){
    $process=Start-Process -FilePath $setupPath -WindowStyle Hidden -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="'+$target+'"'),('/LOG="'+(Join-Path $base $Log)+'"')) -PassThru -Wait
    return $process.ExitCode
}
function Uninstall([string]$Log){
    $process=Start-Process -FilePath $uninstaller -WindowStyle Hidden -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $base $Log)+'"')) -PassThru -Wait
    return $process.ExitCode
}
try{
    Assert ((Install 'install.log') -eq 0) 'Clean installer failed.'
    Assert (Test-Path -LiteralPath $registry) 'Windows uninstall entry missing.'
    Assert (Test-Path -LiteralPath (Join-Path $client 'RoutePilot.exe')) 'Application was not installed.'
    Assert (-not(Test-Path -LiteralPath (Join-Path $client 'runtime\app-build\RoutePilot.exe'))) 'Redundant GUI copy was installed.'
    $shell=New-Object -ComObject WScript.Shell
    foreach($link in $links){
        Assert (Test-Path -LiteralPath $link) 'Application shortcut missing.'
        $shortcut=$shell.CreateShortcut($link)
        Assert ($shortcut.TargetPath -eq (Join-Path $client 'RoutePilot.exe') -and $shortcut.WorkingDirectory -eq $client) 'Shortcut points outside the installation.'
    }
    $preview=Start-Process -FilePath (Join-Path $client 'RoutePilot.exe') -ArgumentList '--render --compact' -WindowStyle Hidden -PassThru -Wait
    Assert ($preview.ExitCode -eq 0 -and (Test-Path -LiteralPath (Join-Path $client 'artifacts\routepilot-compact.png'))) 'Installed application did not open from its final directory.'
    [IO.File]::WriteAllText((Join-Path $client 'force-vpn.txt'),'keep-user-rule.example')
    New-Item -ItemType Directory -Path (Join-Path $client 'data'),(Join-Path $client 'private') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $client 'data\preferences.json'),'{"fixture":true}')
    [IO.File]::WriteAllText((Join-Path $client 'private\fixture.txt'),'keep-local-data')
    [IO.File]::WriteAllText((Join-Path $client 'runtime\session.json'),'{"running":true}')
    $before=(Get-FileHash -LiteralPath (Join-Path $client 'RoutePilot.exe')).Hash
    Assert ((Install 'blocked-install.log') -ne 0) 'Installer overwrote an active route.'
    Assert ((Uninstall 'blocked-uninstall.log') -ne 0) 'Uninstaller removed an active route.'
    Assert ((Get-FileHash -LiteralPath (Join-Path $client 'RoutePilot.exe')).Hash -eq $before) 'Rejected operation changed executable.'
    [IO.File]::WriteAllText((Join-Path $client 'runtime\session.json'),'{"running":false}')
    Assert ((Install 'reinstall.log') -eq 0) 'Repeat installation failed.'
    Assert ([IO.File]::ReadAllText((Join-Path $client 'force-vpn.txt')) -eq 'keep-user-rule.example') 'Repeat installation changed user rules.'
    Assert ([IO.File]::ReadAllText((Join-Path $client 'data\preferences.json')) -eq '{"fixture":true}') 'Repeat installation changed preferences.'
    # Simulate an internal update: the old uninstaller must accept a newer product.json.
    [IO.File]::WriteAllText((Join-Path $client 'product.json'),'{"name":"CITA Route","version":"99.0.0"}')
    Assert ((Uninstall 'uninstall.log') -eq 0) 'Upgraded application could not be uninstalled.'
    Assert (-not(Test-Path -LiteralPath (Join-Path $client 'RoutePilot.exe'))) 'Uninstall left public application files.'
    Assert (-not(Test-Path -LiteralPath $registry)) 'Uninstall left its registry entry.'
    foreach($link in $links){Assert (-not(Test-Path -LiteralPath $link)) 'Uninstall left a shortcut.'}
    Assert ([IO.File]::ReadAllText((Join-Path $client 'force-vpn.txt')) -eq 'keep-user-rule.example') 'Uninstall removed personal rules.'
    Assert ([IO.File]::ReadAllText((Join-Path $client 'private\fixture.txt')) -eq 'keep-local-data') 'Uninstall removed private local files.'
    Write-Output 'PASS: actual installer, Desktop/Start shortcuts, Windows Apps registration, repeat install, active-route refusal, upgraded uninstall and personal-data preservation. No VPN/services changed.'
}finally{
    # Do not remove the fixture on failure: keep logs and the test uninstaller
    # available for diagnosis, rather than orphaning OS integration entries.
    if(-not(Test-Path -LiteralPath $registry) -and -not @($links | Where-Object {Test-Path -LiteralPath $_}).Count){
        $prefix=[IO.Path]::GetFullPath((Join-Path $root 'runtime\installer-integration-'))
        if(-not [IO.Path]::GetFullPath($base).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe installer fixture cleanup.'}
        Remove-Item -LiteralPath $base -Recurse -Force
    }
}
