[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PackageDirectory,[string]$Compiler='',
      [string]$DownloadProxy='',[switch]$TestBuild)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$version=([IO.File]::ReadAllText((Join-Path $root 'product.json')) | ConvertFrom-Json).version
$artwork=Join-Path $root 'runtime\installer-artwork'
& (Join-Path $root 'installer\Create-Branding.ps1') -OutputDirectory $artwork
$package=[IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\')
$manifest=[IO.File]::ReadAllText((Join-Path $package 'manifest.json')) | ConvertFrom-Json
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
    $compilerArguments=@('/Qp',('/DPayloadDir='+$payload),('/DArtworkDir='+$artwork),('/DProductVersion='+$version),('/DOutputDir='+(Join-Path $root 'dist')))
    if($nativeOnly){$compilerArguments+='/DNativeStandalone=1'}
    if(-not $manifest.selfContained){$compilerArguments+=@('/DNeedsDesktopRuntime=1',('/DRuntimeUrl='+$runtime.url),('/DRuntimeSha512='+$runtime.sha512),('/DRuntimeVersion='+$runtime.version))}
    if($TestBuild){$compilerArguments+='/DTestBuild=1'}
    $compilerArguments+=(Join-Path $root 'installer\CitaRoute.iss')
    & $Compiler @compilerArguments
    if($LASTEXITCODE -ne 0){throw 'Installer compilation failed.'}
    $name='CITA-Route-'+$version+'-win-x64-'+$(if($TestBuild){'test-setup'}else{'setup'})+'.exe'
    $output=Join-Path $root ('dist\'+$name)
    if(-not $TestBuild){
        $sums=Join-Path $root 'dist\SHA256SUMS.txt'
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
