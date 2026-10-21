$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'DesktopRuntime.ps1')
$fixture=Join-Path $PSScriptRoot ('runtime\desktop-runtime-test-'+[guid]::NewGuid().ToString('N'))
function Assert($Condition,[string]$Message){if(-not $Condition){throw $Message}}
try{
    New-Item -ItemType Directory -Path $fixture -Force | Out-Null
    Assert (-not(Test-DesktopRuntimeAt $fixture)) 'A missing runtime must not pass.'
    [IO.File]::WriteAllText((Join-Path $fixture 'dotnet.exe'),'fixture')
    foreach($framework in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')){
        $directory=Join-Path $fixture ('shared\'+$framework+'\9.0.0')
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $directory $(if($framework -eq 'Microsoft.NETCore.App'){'System.Private.CoreLib.dll'}else{'Microsoft.WindowsDesktop.App.deps.json'})),'fixture')
    }
    Assert (-not(Test-DesktopRuntimeAt $fixture)) '.NET 9 is not a substitute for .NET Desktop 10.'
    foreach($framework in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')){
        foreach($version in @('10.0.12','10.0.13-preview.1','11.0.0')){
            $directory=Join-Path $fixture ('shared\'+$framework+'\'+$version)
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
            [IO.File]::WriteAllText((Join-Path $directory $(if($framework -eq 'Microsoft.NETCore.App'){'System.Private.CoreLib.dll'}else{'Microsoft.WindowsDesktop.App.deps.json'})),'fixture')
        }
    }
    Assert (-not(Test-DesktopRuntimeAt $fixture)) 'Older patches, previews and a newer major must not satisfy the prerequisite.'
    foreach($framework in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')){
        $directory=Join-Path $fixture ('shared\'+$framework+'\10.0.13')
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $directory $(if($framework -eq 'Microsoft.NETCore.App'){'System.Private.CoreLib.dll'}else{'Microsoft.WindowsDesktop.App.deps.json'})),'fixture')
        if($framework -eq 'Microsoft.NETCore.App'){Assert (-not(Test-DesktopRuntimeAt $fixture)) 'Core runtime alone cannot run WPF.'}
    }
    Assert (Test-DesktopRuntimeAt $fixture) 'Both machine runtime families must be accepted.'
    Assert (-not(Test-DesktopRuntimeAt $fixture ([version]'10.0.14'))) 'A package minimum newer than the installed patch must be respected.'
    Assert (-not(Test-PackageDesktopRuntime @{selfContained=$false;desktopRuntimeMajor=10})) 'Missing package minimum must be rejected.'
    $linked=$fixture+'-linked'
    New-Item -ItemType Junction -Path $linked -Target $fixture | Out-Null
    try{Assert (-not(Test-DesktopRuntimeAt $linked)) 'Linked runtime roots must not certify the SYSTEM prerequisite.'}
    finally{[IO.Directory]::Delete($linked)}
    Assert (Test-DesktopRuntimeSignature 'Valid' 'CN=.NET, O=Microsoft Corporation, C=US') 'The real Microsoft runtime publisher was rejected.'
    Assert (-not(Test-DesktopRuntimeSignature 'NotTrusted' 'CN=.NET, O=Microsoft Corporation, C=US')) 'An untrusted Microsoft-looking signature was accepted.'
    Assert (-not(Test-DesktopRuntimeSignature 'Valid' 'CN=Microsoft Corporation, O=Other Organization, C=US')) 'The common name must not substitute for verified publisher organization.'
    $file=Join-Path $fixture 'unsigned.exe';[IO.File]::WriteAllText($file,'not an executable')
    $rejected=$false;try{Assert-DesktopRuntimeInstaller $file ('0'*128)}catch{$rejected=$true};Assert $rejected 'An incorrect hash was accepted.'
    $rejected=$false;try{Assert-DesktopRuntimeInstaller $file (Get-FileHash -LiteralPath $file -Algorithm SHA512).Hash}catch{$rejected=$true};Assert $rejected 'An unsigned file with a matching hash was accepted.'
    Write-Output 'PASS: Desktop/Core x64 runtime requirements, major-version rejection, incomplete runtime, hash and publisher checks. No runtime installed or removed.'
}finally{
    if(-not [IO.Path]::GetFullPath($fixture).StartsWith([IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runtime\desktop-runtime-test-')),[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture cleanup.'}
    if(Test-Path -LiteralPath $fixture){Remove-Item -LiteralPath $fixture -Recurse -Force}
}
