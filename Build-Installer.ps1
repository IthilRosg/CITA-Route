[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PackageDirectory,[string]$Compiler='',
      [string]$DownloadProxy='',[switch]$TestBuild,[switch]$IsolatedTest,[switch]$PhysicalAcceptance,[string]$OutputDirectory='')
$ErrorActionPreference='Stop'
if($IsolatedTest -and -not $TestBuild){throw 'Isolated fixture requires a test build.'}
if($PhysicalAcceptance -and ($TestBuild -or $IsolatedTest)){throw 'Physical acceptance cannot be combined with test fixture modes.'}
$root=$PSScriptRoot
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root $(if($PhysicalAcceptance){'runtime\physical-acceptance'}else{'dist'})}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $OutputDirectory.StartsWith(([IO.Path]::GetFullPath($root)+'\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Installer output must stay inside the workspace.'}
if($PhysicalAcceptance){
    $allowed=[IO.Path]::GetFullPath((Join-Path $root 'runtime\physical-acceptance')).TrimEnd('\')
    if($OutputDirectory -ne $allowed -and -not $OutputDirectory.StartsWith($allowed+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Physical acceptance output must stay in runtime/physical-acceptance.'}
    $current=Get-Item -LiteralPath (Join-Path $root 'runtime') -Force
    if($current.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Physical acceptance runtime cannot be a link.'}
    $walk=$allowed
    foreach($part in (($OutputDirectory.Substring($allowed.Length)).Trim('\') -split '\\')){
        if(Test-Path -LiteralPath $walk){if((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Physical acceptance output cannot traverse links.'}}
        if($part){$walk=Join-Path $walk $part}
    }
    if(Test-Path -LiteralPath $walk){if((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Physical acceptance output cannot traverse links.'}}
}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$version=([IO.File]::ReadAllText((Join-Path $root 'product.json')) | ConvertFrom-Json).version
$artwork=Join-Path $root 'runtime\installer-artwork'
& (Join-Path $root 'installer\Create-Branding.ps1') -OutputDirectory $artwork
$package=[IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\')
$manifest=[IO.File]::ReadAllText((Join-Path $package 'manifest.json')) | ConvertFrom-Json
if($manifest.edition -eq 'native'){
    $packageProduct=[IO.File]::ReadAllText((Join-Path $package 'product.json'))|ConvertFrom-Json
    $nativePackage=Join-Path $package 'runtime/native-components.zip'
    if(-not(Test-Path -LiteralPath $nativePackage)){throw 'Native installer component archive missing.'}
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $nativeZip=[IO.Compression.ZipFile]::OpenRead($nativePackage)
    try{$containsZapret=@($nativeZip.Entries|Where-Object {$_.FullName -ieq 'zapret-manifest.json' -or $_.FullName.StartsWith('zapret/',[StringComparison]::OrdinalIgnoreCase) -or $_.FullName.StartsWith('zapret\',[StringComparison]::OrdinalIgnoreCase)}).Count -gt 0}
    finally{$nativeZip.Dispose()}
    if([bool]$manifest.zapretInternal -ne $containsZapret -or [bool]$packageProduct.distribution.zapretInternal -ne $containsZapret){throw 'Native installer Zapret metadata does not match its archive.'}
    if($containsZapret -and -not ($IsolatedTest -or $PhysicalAcceptance)){throw 'Internal Zapret staging is limited to isolated test or physical acceptance installers.'}
}
if($PhysicalAcceptance -and ($manifest.edition -ne 'native' -or -not $containsZapret)){throw 'Physical acceptance requires internal native Zapret staging.'}
if($manifest.version -ne $version -or $manifest.architecture -ne 'win-x64' -or (-not $manifest.selfContained -and ($manifest.desktopRuntimeMajor -ne 10 -or $manifest.desktopRuntimeMinimum -ne '10.0.13'))){throw 'Installer requires the matching Windows package and a supported runtime declaration.'}
$runtime=Get-Content -LiteralPath (Join-Path $root 'installer\desktop-runtime.json') -Raw | ConvertFrom-Json
if($runtime.version -notmatch '^10\.0\.\d+$' -or $runtime.sha512 -notmatch '^[a-fA-F0-9]{128}$' -or $runtime.url -cne ('https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/'+$runtime.version+'/windowsdesktop-runtime-'+$runtime.version+'-win-x64.exe')){throw 'Unexpected runtime prerequisite metadata.'}
if(-not $Compiler){
    $tool=Join-Path $root 'runtime\installer-tool'
    $Compiler=Join-Path $tool 'ISCC.exe'
    if(-not(Test-Path -LiteralPath $Compiler)){
        $download=Join-Path $root 'downloads\innosetup-6.7.3.exe'
        New-Item -ItemType Directory -Path (Split-Path $download) -Force | Out-Null
        $expected='9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'
        if(-not(Test-Path -LiteralPath $download) -or (Get-FileHash -LiteralPath $download).Hash -ne $expected){
            $arguments=@('--fail','--silent','--show-error','--location','--proto','=https','--proto-redir','=https','--max-time','180','--retry','2','--output',$download)
            if($DownloadProxy){$arguments+=@('--proxy',$DownloadProxy)}
            & curl.exe @arguments 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'
            if($LASTEXITCODE -ne 0){throw 'Installer compiler download failed.'}
        }
        if((Get-FileHash -LiteralPath $download).Hash -ne $expected){throw 'Installer compiler SHA-256 differs from the pinned official release.'}
        $signature=Get-AuthenticodeSignature -LiteralPath $download
        if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)CN=Pyrsys B\.V\.(,|$)'){throw 'Installer compiler signature is not valid for the expected publisher.'}
        # Official portable mode: no tool registration, uninstaller or shortcuts.
        $process=Start-Process -FilePath $download -WindowStyle Hidden -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/PORTABLE=1','/NOICONS',('/DIR="'+$tool+'"')) -PassThru -Wait
        if($process.ExitCode -ne 0 -or -not(Test-Path -LiteralPath $Compiler)){throw 'Portable installer compiler preparation failed.'}
    }
}
$payload=Join-Path $root ('runtime\installer-payload-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $payload -Force | Out-Null
try{
    $entries=@();$names=@{};$savedBytes=0L
    foreach($entry in $manifest.files){
        $source=[IO.Path]::GetFullPath((Join-Path $package $entry.path))
        if(-not $source.StartsWith($package+'\',[StringComparison]::OrdinalIgnoreCase) -or $names.ContainsKey($source)){throw 'Unsafe installer manifest path.'}
        $names[$source]=$true
        if((Get-Item -LiteralPath $source).Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $source).Hash -ne $entry.sha256){throw 'Installer source differs from the verified manifest.'}
        # Keep this duplicate in the portable ZIP for old updaters, but never
        # install it: the actual GUI already exists in client\RoutePilot.exe.
        if($entry.path -eq 'runtime\app-build\RoutePilot.exe'){$savedBytes+=$entry.bytes;continue}
        $target=Join-Path $payload $entry.path
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
        $entries+=$entry
    }
    $nativeOnly=$manifest.edition -eq 'native'
    $requiredFiles=if($nativeOnly){@('RoutePilot.exe','Install-NativeAgent.ps1','Install-NativeComponents.ps1','NativeMaintenance.ps1','runtime\native-components.zip','product.json','force-vpn.txt','force-direct.txt','runtime\agent-build\RoutePilot.Agent.exe')}else{@('RoutePilot.exe','SmartRouting.ps1','Install-Release.ps1','product.json','force-vpn.txt','force-direct.txt','runtime\agent-build\RoutePilot.Agent.exe')}
    foreach($required in $requiredFiles){
        if(-not(Test-Path -LiteralPath (Join-Path $payload $required))){throw ('Incomplete installer: '+$required)}
    }
    $manifest.files=$entries
    [IO.File]::WriteAllText((Join-Path $payload 'manifest.json'),($manifest | ConvertTo-Json -Depth 10),(New-Object Text.UTF8Encoding($false)))
    $compilerArguments=@('/Qp',('/DPayloadDir='+$payload),('/DArtworkDir='+$artwork),('/DProductVersion='+$version),('/DOutputDir='+$OutputDirectory))
    if($nativeOnly){$compilerArguments+='/DNativeStandalone=1'}
    if(-not $manifest.selfContained){$compilerArguments+=@('/DNeedsDesktopRuntime=1',('/DRuntimeUrl='+$runtime.url),('/DRuntimeSha512='+$runtime.sha512),('/DRuntimeVersion='+$runtime.version))}
    if($TestBuild){$compilerArguments+='/DTestBuild=1'}
    if($IsolatedTest){$compilerArguments+='/DIsolatedTest=1'}
    if($PhysicalAcceptance){$compilerArguments+='/DPhysicalAcceptance=1'}
    $compilerArguments+=(Join-Path $root 'installer\CitaRoute.iss')
    & $Compiler @compilerArguments
    if($LASTEXITCODE -ne 0){throw 'Installer compilation failed.'}
    $name='CITA-Route-'+$version+'-win-x64-'+$(if($PhysicalAcceptance){'PRIVATE-PREVIEW-physical-acceptance-setup'}elseif($IsolatedTest){'isolated-test-setup'}elseif($TestBuild){'test-setup'}else{'setup'})+'.exe'
    $output=Join-Path $OutputDirectory $name
    if($PhysicalAcceptance){
        $sidecar=Join-Path $OutputDirectory ([IO.Path]::GetFileNameWithoutExtension($name)+'-payload-manifest.json')
        Copy-Item -LiteralPath (Join-Path $payload 'manifest.json') -Destination $sidecar -Force
        $receipt=[ordered]@{scope='Private physical acceptance; public client files only';installerSha256=(Get-FileHash -LiteralPath $output).Hash;payloadManifestSha256=(Get-FileHash -LiteralPath $sidecar).Hash;stageManifestSha256=(Get-FileHash -LiteralPath (Join-Path $package 'manifest.json')).Hash;nativeArchiveSha256=(Get-FileHash -LiteralPath $nativePackage).Hash;createdUtc=[DateTimeOffset]::UtcNow.ToString('o')}
        [IO.File]::WriteAllText((Join-Path $OutputDirectory ([IO.Path]::GetFileNameWithoutExtension($name)+'-receipt.json')),($receipt|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
    }
    if(-not $TestBuild -and -not $PhysicalAcceptance){
        $sums=Join-Path $OutputDirectory 'SHA256SUMS.txt'
        $lines=@();if(Test-Path -LiteralPath $sums){$lines=@([IO.File]::ReadAllLines($sums) | Where-Object {$_ -notmatch '\s+CITA-Route-.*-setup\.exe$'})}
        $lines+=((Get-FileHash -LiteralPath $output).Hash.ToLowerInvariant()+'  '+$name)
        [IO.File]::WriteAllLines($sums,$lines,(New-Object Text.UTF8Encoding($false)))
    }
    Write-Output ('INSTALLER: '+$output)
    Write-Output ('Removed duplicate GUI from installed payload: '+$savedBytes+' bytes.')
}finally{
    $prefix=[IO.Path]::GetFullPath((Join-Path $root 'runtime\installer-payload-'))
    if(-not [IO.Path]::GetFullPath($payload).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe installer staging cleanup.'}
    Remove-Item -LiteralPath $payload -Recurse -Force
}
