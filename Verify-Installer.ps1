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
# Read the actual Unicode Shell Link interface. WScript.Shell may expose ANSI
# path values on a Windows installation with a different system code page.
if(-not ('CitaFixtureShortcut' -as [type])){Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class CitaFixtureShortcut {
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW {
        void GetPath([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder path,int length,IntPtr data,uint flags);
        void GetIDList(out IntPtr list); void SetIDList(IntPtr list);
        void GetDescription([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder text,int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder text,int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetArguments([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder text,int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetHotkey(out short key); void SetHotkey(short key);
        void GetShowCmd(out int command); void SetShowCmd(int command);
        void GetIconLocation([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder path,int length,out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path,int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path,uint flags);
        void Resolve(IntPtr window,uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
    public static string[] Read(string path) {
        object link=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));
        try {
            ((IPersistFile)link).Load(path,0);
            var target=new StringBuilder(32768); var directory=new StringBuilder(32768);
            ((IShellLinkW)link).GetPath(target,target.Capacity,IntPtr.Zero,4);
            ((IShellLinkW)link).GetWorkingDirectory(directory,directory.Capacity);
            return new[]{target.ToString(),directory.ToString()};
        } finally { Marshal.FinalReleaseComObject(link); }
    }
}
'@}
function Install([string]$Log){
    $task=if($Log -eq 'install.log'){'/TASKS=autostart'}else{'/TASKS=""'}
    $process=Start-Process -FilePath $setupPath -WindowStyle Hidden -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',$task,('/DIR="'+$target+'"'),('/LOG="'+(Join-Path $base $Log)+'"')) -PassThru -Wait
    return $process.ExitCode
}
function Uninstall([string]$Log){
    $process=Start-Process -FilePath $uninstaller -WindowStyle Hidden -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $base $Log)+'"')) -PassThru -Wait
    return $process.ExitCode
}
try{
    Assert ((Install 'install.log') -eq 0) 'Clean installer failed.'
    $runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $run=(Get-ItemProperty -LiteralPath $runKey -Name $label).$label
    Assert ($run -eq ('"'+(Join-Path $client 'RoutePilot.exe')+'" --startup')) 'Installer startup command must quote the chosen Unicode path and open the tray only.'
    Assert (Test-Path -LiteralPath $registry) 'Windows uninstall entry missing.'
    Assert (Test-Path -LiteralPath (Join-Path $client 'RoutePilot.exe')) 'Application was not installed.'
    Assert (-not(Test-Path -LiteralPath (Join-Path $client 'runtime\app-build\RoutePilot.exe'))) 'Redundant GUI copy was installed.'
    foreach($link in $links){
        Assert (Test-Path -LiteralPath $link) 'Application shortcut missing.'
        $shortcut=[CitaFixtureShortcut]::Read($link)
        $expected=Join-Path $client 'RoutePilot.exe'
        Assert ($shortcut[0] -eq $expected -and $shortcut[1].TrimEnd('\') -eq $client) ('Shortcut differs: '+(@{Expected=$expected;Target=$shortcut[0];WorkingDirectory=$shortcut[1]} | ConvertTo-Json -Compress))
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
    Assert (-not(Get-ItemProperty -LiteralPath $runKey -Name $label -ErrorAction SilentlyContinue)) 'Unchecking startup during reinstall did not remove it.'
    Assert ([IO.File]::ReadAllText((Join-Path $client 'force-vpn.txt')) -eq 'keep-user-rule.example') 'Repeat installation changed user rules.'
    Assert ([IO.File]::ReadAllText((Join-Path $client 'data\preferences.json')) -eq '{"fixture":true}') 'Repeat installation changed preferences.'
    # Match enabling tray startup in the application after Setup's task was unchecked.
    Set-ItemProperty -LiteralPath $runKey -Name $label -Value ('"'+(Join-Path $client 'RoutePilot.exe')+'" --startup')
    # Simulate an internal update: the old uninstaller must accept a newer product.json.
    [IO.File]::WriteAllText((Join-Path $client 'product.json'),'{"name":"CITA Route","version":"99.0.0"}')
    Assert ((Uninstall 'uninstall.log') -eq 0) 'Upgraded application could not be uninstalled.'
    Assert (-not(Test-Path -LiteralPath (Join-Path $client 'RoutePilot.exe'))) 'Uninstall left public application files.'
    Assert (-not(Test-Path -LiteralPath $registry)) 'Uninstall left its registry entry.'
    Assert (-not(Get-ItemProperty -LiteralPath $runKey -Name $label -ErrorAction SilentlyContinue)) 'Uninstall left startup enabled from application settings.'
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
