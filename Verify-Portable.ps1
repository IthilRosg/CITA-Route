param([string]$PackageDirectory='',[switch]$AllowFrameworkDependent,[string]$PackageArchive='')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
. (Join-Path $root 'FileStorage.ps1')
. (Join-Path $root 'ConnectionEnvironment.ps1')
function Assert($Value,[string]$Message){if(-not $Value){throw $Message}}
$fixture=Join-Path $root ('runtime\portable-test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture,(Join-Path $fixture 'data'),(Join-Path $fixture 'runtime') -Force | Out-Null
try{
    $fresh=Get-ConnectionSettings $fixture
    Assert (-not $fresh.SourceProfile) 'A clean computer must have no preselected personal profile.'
    Write-JsonFile (Join-Path $fixture 'runtime\session.json') @{sourceProfile='Local WG';running=$true}
    Assert ((Get-ConnectionSettings $fixture).SourceProfile -eq 'Local WG') 'Local session migration failed.'
    Write-JsonFile (Join-Path $fixture 'data\connection.json') @{SourceProfile='Friend AWG';CliPath='C:\Program Files\WireSock\command-line\wiresock-connect-cli.exe'}
    Assert ((Get-ConnectionSettings $fixture).SourceProfile -eq 'Friend AWG') 'An explicit selection must override a historic session.'
    $config="[Interface]`nAddress = 10.0.0.2/32`n[Peer]`nEndpoint = 203.0.113.20:51820`nAllowedIPs = 0.0.0.0/0"
    $endpoint=Get-ProfileEndpoint $config
    Assert ($endpoint.Ip -eq '203.0.113.20' -and $endpoint.Port -eq 51820) 'Endpoint parsing remains bound to the original server.'
    $pinned=Get-ProfileEndpoint ($config.Replace('203.0.113.20','offline.example.com')) '203.0.113.30'
    Assert ($pinned.Ip -eq '203.0.113.30' -and $pinned.SourceEndpoint -eq 'offline.example.com:51820') 'Reconnect must reuse the protected endpoint without depending on DNS availability.'
    foreach($invalid in @(($config.Replace('51820','65536')),($config+"`n[Peer]`nEndpoint=1.1.1.1:1"),($config.Replace('203.0.113.20:51820','[2001:db8::1]:51820')))){
        $refused=$false;try{$null=Get-ProfileEndpoint $invalid}catch{$refused=$true}
        Assert $refused 'An unsupported configuration was accepted.'
    }
    function Read-HelperOutput($Image,$Arguments){@('Profiles:',' - Friend WG',' - MAIN',' - SmartRouting-AWG2',' - RoutePilot-Probe-012345',' - Friend WG') -join "`r`n"}
    $names=@(Get-WireSockProfileNames)
    Assert ($names.Count -eq 2 -and $names -contains 'Friend WG' -and $names -contains 'MAIN') 'Managed or duplicate profiles leaked into discovery.'
    foreach($name in @('ConnectionEnvironment.ps1','Configure-Connection.ps1','SmartRouting.ps1','Install-Agent.ps1','Build-Portable.ps1')){
        $parseErrors=$null;$tokens=$null
        $null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root $name),[ref]$tokens,[ref]$parseErrors)
        Assert (-not $parseErrors) ('PowerShell syntax: '+$name)
    }
    if($PackageDirectory){
        $package=[IO.Path]::GetFullPath($PackageDirectory)
        Assert (Test-Path -LiteralPath (Join-Path $package 'manifest.json')) 'Package manifest missing.'
        $manifest=Read-JsonFile (Join-Path $package 'manifest.json')
        if(-not $manifest.selfContained){
            . (Join-Path $root 'DesktopRuntime.ps1')
            Assert ($AllowFrameworkDependent -and (Test-PackageDesktopRuntime $manifest)) 'Framework package requires the declared machine-wide .NET Desktop 10 x64 patch.'
        }
        foreach($file in $manifest.files){
            $path=[IO.Path]::GetFullPath((Join-Path $package $file.path))
            Assert ($path.StartsWith($package.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) 'Manifest path escaped package.'
            Assert ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $file.sha256) ('Package hash mismatch: '+$file.path)
        }
        $receipt=(Read-JsonFile (Join-Path $package 'product.json')).distribution
        Assert ($receipt.schema -eq 1 -and $receipt.selfContained -is [bool] -and $receipt.selfContained -eq $manifest.selfContained) 'Distribution receipt missing or disagrees with package format.'
        Assert ($receipt.agentSha256 -eq (Get-FileHash -LiteralPath (Join-Path $package 'runtime\agent-build\RoutePilot.Agent.exe')).Hash) 'Distribution receipt does not match the actual agent.'
        . (Join-Path $package 'AgentPackage.ps1')
        Assert-AgentRuntimeReady $package
        $bad=@(Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object {$_.Extension -in @('.conf','.log') -or $_.Name -in @('policy-heartbeat.json','bridge-safety.json','resource-state.json','udp-quality.json','session.json','controller-token.txt','connection.json','traffic.json','policy-cache.json','agent-config.json','router.json','bridge.json')})
        Assert ($bad.Count -eq 0) 'Personal runtime files found in package.'
        Assert (-not (Test-Path -LiteralPath (Join-Path $package 'private'))) 'Private folder found in package.'
        $inventory=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $package 'Configure-Connection.ps1') -Action Discover | ConvertFrom-Json
        Assert (-not $inventory.SelectedProfile) 'Clean package inherited the current user profile.'
        & (Join-Path $package 'runtime\agent-build\RoutePilot.Agent.exe') --self-test
        Assert ($LASTEXITCODE -eq 0) 'Self-contained agent failed.'
        $previousArchiveTest=$env:CITA_RELEASE_ARCHIVE_TEST
        try{
            if($PackageArchive){$env:CITA_RELEASE_ARCHIVE_TEST=[IO.Path]::GetFullPath($PackageArchive)}
            else{Remove-Item Env:CITA_RELEASE_ARCHIVE_TEST -ErrorAction SilentlyContinue}
            $app=Start-Process -FilePath (Join-Path $package 'RoutePilot.exe') -ArgumentList '--self-test' -WindowStyle Hidden -PassThru -Wait
        }finally{if($null -eq $previousArchiveTest){Remove-Item Env:CITA_RELEASE_ARCHIVE_TEST -ErrorAction SilentlyContinue}else{$env:CITA_RELEASE_ARCHIVE_TEST=$previousArchiveTest}}
        if($app.ExitCode -ne 0){
            $appLog=Join-Path $package 'logs\app-self-test.log'
            if(Test-Path -LiteralPath $appLog){Get-Content -LiteralPath $appLog -Tail 50 | Write-Output}
        }
        Assert ($app.ExitCode -eq 0) 'Self-contained GUI failed.'
    }
    Write-Output 'PASS: clean installation defaults, profile discovery, endpoint portability, invalid config rejection, script syntax and optional package verification. No network settings changed.'
}finally{
    $prefix=[IO.Path]::GetFullPath((Join-Path $root 'runtime\portable-test-'))
    if(-not [IO.Path]::GetFullPath($fixture).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture cleanup.'}
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
