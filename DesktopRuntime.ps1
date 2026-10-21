[CmdletBinding()]
param([ValidateSet('','Check','Verify')][string]$Action='',[string]$Installer='',[string]$Sha512='',[string]$ResultPath='')

function Test-DesktopRuntimeAt([string]$DotnetRoot) {
    if(-not $DotnetRoot -or -not(Test-Path -LiteralPath (Join-Path $DotnetRoot 'dotnet.exe') -PathType Leaf)){return $false}
    foreach($framework in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')){
        $shared=Join-Path $DotnetRoot ('shared\'+$framework)
        $required=if($framework -eq 'Microsoft.NETCore.App'){'System.Private.CoreLib.dll'}else{'Microsoft.WindowsDesktop.App.deps.json'}
        $found=@(Get-ChildItem -LiteralPath $shared -Directory -ErrorAction SilentlyContinue | Where-Object {$_.Name -match '^8\.0\.\d+$' -and (Test-Path -LiteralPath (Join-Path $_.FullName $required) -PathType Leaf)})
        if($found.Count -eq 0){return $false}
    }
    return $true
}
function Get-MachineDotnetRoot {
    $registry=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
    try{
        $key=$registry.OpenSubKey('SOFTWARE\dotnet\Setup\InstalledVersions\x64')
        try{if($key){$location=[string]$key.GetValue('InstallLocation');if($location){return $location}}}finally{if($key){$key.Dispose()}}
    }finally{$registry.Dispose()}
    $programs=$env:ProgramW6432
    if(-not $programs){$programs=$env:ProgramFiles}
    return (Join-Path $programs 'dotnet')
}
function Test-MachineDesktopRuntime {return (Test-DesktopRuntimeAt (Get-MachineDotnetRoot))}
function Test-DesktopRuntimeSignature([string]$Status,[string]$Subject) {
    # The current official runtime uses CN=.NET, O=Microsoft Corporation.
    # The certificate organization, valid trust chain and pinned file hash are
    # required together; an assumed friendly/common name is not the publisher.
    return ($Status -eq 'Valid' -and $Subject -match '(^|,\s*)O=Microsoft Corporation(,|$)')
}
function Assert-DesktopRuntimeInstaller([string]$Path,[string]$Expected) {
    if($Expected -notmatch '^[a-fA-F0-9]{128}$' -or (Get-FileHash -LiteralPath $Path -Algorithm SHA512).Hash -ne $Expected){throw 'The .NET runtime download failed integrity verification.'}
    $signature=Get-AuthenticodeSignature -LiteralPath $Path
    if(-not(Test-DesktopRuntimeSignature ([string]$signature.Status) ([string]$signature.SignerCertificate.Subject))){throw ('The .NET runtime publisher or signature is invalid: '+$signature.Status)}
}
if($Action){
    $ErrorActionPreference='Stop'
    try{
        if($Action -eq 'Verify'){Assert-DesktopRuntimeInstaller $Installer $Sha512;exit 0}
        if(Test-MachineDesktopRuntime){exit 0}else{exit 10}
    }catch{
        if($ResultPath){[IO.File]::WriteAllText($ResultPath,$_.Exception.Message,[Text.UTF8Encoding]::new($true))}
        [Console]::Error.WriteLine($_.Exception.Message);exit 1
    }
}
