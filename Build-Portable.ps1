[CmdletBinding()]
param([switch]$FrameworkDependent)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Build-Dotnet.ps1')
$root=$PSScriptRoot
$version=([IO.File]::ReadAllText((Join-Path $root 'product.json')) | ConvertFrom-Json).version
$dist=Join-Path $root 'dist'
$flavor=if($FrameworkDependent){'-framework'}else{''}
$stage=Join-Path $dist ('CITA-Route-'+$version+'-win-x64'+$flavor+'-'+[guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $stage,(Join-Path $stage 'runtime\agent-build'),(Join-Path $stage 'runtime\app-build'),(Join-Path $stage 'runtime\rules'),(Join-Path $stage 'rules'),(Join-Path $stage 'source\agent'),(Join-Path $stage 'source\app\Assets'),(Join-Path $stage 'licenses') -Force | Out-Null
$dotnet=Get-BuildDotnet
$env:DOTNET_CLI_HOME=Join-Path $root 'runtime\.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1';$env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:MSBuildEnableWorkloadResolver='false'
foreach($project in @('agent','app')){
    & $dotnet publish (Join-Path $root ($project+'\'+$(if($project -eq 'agent'){'RoutePilot.Agent.csproj'}else{'RoutePilot.csproj'}))) -c Release -r win-x64 -o (Join-Path $stage ('runtime\'+$project+'-build')) --self-contained $(-not $FrameworkDependent) `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:EnableSingleFileAnalyzer=false -p:RestoreIgnoreFailedSources=true -p:NuGetAudit=false
    if($LASTEXITCODE -ne 0){throw ('Portable publish failed: '+$project)}
}
Copy-Item -LiteralPath (Join-Path $stage 'runtime\app-build\RoutePilot.exe') -Destination (Join-Path $stage 'RoutePilot.exe')
foreach($exe in @('runtime\app-build\RoutePilot.exe','runtime\agent-build\RoutePilot.Agent.exe')){
    if([version](Get-Item -LiteralPath (Join-Path $stage $exe)).VersionInfo.FileVersion -ne [version]($version+'.0')){throw 'Published assembly and product versions differ.'}
}
foreach($name in @('product.json','LICENSE','NOTICE')){Copy-Item -LiteralPath (Join-Path $root $name) -Destination $stage}
Copy-Item -LiteralPath (Join-Path $root 'Directory.Build.props') -Destination (Join-Path $stage 'source')
# Explicit allowlist: never recurse into this computer's private/runtime/data/log folders.
$scripts=@('SmartRouting.ps1','FileStorage.ps1','RuleFeeds.ps1','WireSockCli.ps1','WireSockProfiles.ps1','WireSockRecovery.ps1','AgentLease.ps1','SessionRecovery.ps1','ConnectionEnvironment.ps1','Configure-Connection.ps1','AgentPackage.ps1','Install-Agent.ps1','Verify-Routing.ps1','Install-Release.ps1','DesktopRuntime.ps1','Register-BrowserFeedback.ps1')
foreach($name in $scripts){Copy-Item -LiteralPath (Join-Path $root $name) -Destination $stage}
[void][IO.Directory]::CreateDirectory((Join-Path $stage 'browser-extension'))
foreach($name in @('manifest.json','background.js','images.js','popup.html','popup.js')){Copy-Item -LiteralPath (Join-Path $root ('browser-extension\'+$name)) -Destination (Join-Path $stage ('browser-extension\'+$name))}
Copy-Item -LiteralPath (Join-Path $root 'docs\BROWSER-FEEDBACK.md') -Destination (Join-Path $stage 'browser-extension\README.md')
$routerSource=Join-Path $root 'runtime\router-engine-build\smart-router.exe'
$coreReceipt=Get-Content -LiteralPath (Join-Path $root 'runtime\router-engine-build.json') -Raw|ConvertFrom-Json
$recipe=(@('Build-RouterEngine.ps1','engine-patches\cita_dns_windows.go','engine-patches\cita_dns_windows_test.go','engine-patches\cita_fallback.go','engine-patches\cita_fallback_test.go')|ForEach-Object {(Get-FileHash -LiteralPath (Join-Path $root $_)).Hash}) -join ':'
if($coreReceipt.recipe -ne $recipe -or $coreReceipt.sha256 -ne (Get-FileHash -LiteralPath $routerSource).Hash){throw 'Rebuild the CITA routing core from the current pinned source before packaging.'}
if($coreReceipt.sourceSha256 -ne (Get-FileHash -LiteralPath (Join-Path $root 'distribution-licenses\cita-routing-core-1.14.3-cita.3-source.zip')).Hash){throw 'Corresponding routing core source archive differs from the build receipt.'}
Copy-Item -LiteralPath $routerSource -Destination (Join-Path $stage 'runtime\smart-router.exe')
Copy-Item -LiteralPath (Join-Path $root 'runtime\vpn-bridge.exe') -Destination (Join-Path $stage 'runtime')
foreach($name in @('russia_inside.srs','discord.srs')){Copy-Item -LiteralPath (Join-Path $root ('runtime\rules\'+$name)) -Destination (Join-Path $stage 'runtime\rules')}
Copy-Item -LiteralPath (Join-Path $root 'rules\telegram-cidr.txt') -Destination (Join-Path $stage 'rules')
foreach($name in @('force-direct.txt','force-vpn.txt')){[IO.File]::WriteAllText((Join-Path $stage $name),'# Add your own domains, one per line.',(New-Object Text.UTF8Encoding($true)))}
Copy-Item -LiteralPath (Join-Path $root 'PORTABLE-README.md') -Destination (Join-Path $stage 'README.md')
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path $root 'Verify-Portable.ps1') -Destination (Join-Path $stage 'source')
foreach($project in @('agent','app')){
    Get-ChildItem -LiteralPath (Join-Path $root $project) -File | Where-Object {$_.Extension -in @('.cs','.csproj','.xaml','.manifest')} | Copy-Item -Destination (Join-Path $stage ('source\'+$project))
}
Copy-Item -LiteralPath (Join-Path $root 'app\Assets\RoutePilot.ico') -Destination (Join-Path $stage 'source\app\Assets')
foreach($name in @('sing-box-LICENSE.txt','GPL-3.0.txt','sing-box-1.14.3-source.zip','cita-routing-core-1.14.3-cita.3-source.zip','dotnet-LICENSE.txt','dotnet-THIRD-PARTY-NOTICES.txt')){
    Copy-Item -LiteralPath (Join-Path $root ('distribution-licenses\'+$name)) -Destination (Join-Path $stage 'licenses')
}
# Include the engine's build settings without a user's configuration.
& $routerSource version | Set-Content -LiteralPath (Join-Path $stage 'licenses\engine-version.txt') -Encoding UTF8
$forbidden=@(Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object {$_.Extension -in @('.conf','.log') -or $_.Name -in @('policy-heartbeat.json','bridge-safety.json','resource-state.json','udp-quality.json','session.json','controller-token.txt','connection.json','traffic.json','policy-cache.json','agent-config.json','router.json','bridge.json')})
if($forbidden.Count){throw 'Private files detected in distribution staging.'}
. (Join-Path $root 'FileStorage.ps1')
$product=Read-JsonFile (Join-Path $stage 'product.json')
$product | Add-Member -NotePropertyName distribution -NotePropertyValue @{schema=1;selfContained=(-not $FrameworkDependent);desktopRuntimeMajor=10;desktopRuntimeMinimum='10.0.13';agentSha256=(Get-FileHash -LiteralPath (Join-Path $stage 'runtime\agent-build\RoutePilot.Agent.exe')).Hash} -Force
Write-JsonFile (Join-Path $stage 'product.json') $product
$files=@(Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {@{path=$_.FullName.Substring($stage.Length+1);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
Write-JsonFile (Join-Path $stage 'manifest.json') @{version=$version;architecture='win-x64';selfContained=(-not $FrameworkDependent);desktopRuntimeMajor=10;desktopRuntimeMinimum='10.0.13';files=$files;createdUtc=[DateTime]::UtcNow.ToString('o')}
$zip=Join-Path $dist ('CITA-Route-'+$version+'-win-x64'+$flavor+'.zip')
Compress-Archive -LiteralPath $stage -DestinationPath $zip -Force
$hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$sums=Join-Path $dist 'SHA256SUMS.txt';$fileName=[IO.Path]::GetFileName($zip)
$lines=@();if(Test-Path -LiteralPath $sums){$lines=@([IO.File]::ReadAllLines($sums) | Where-Object {-not $_.EndsWith('  '+$fileName,[StringComparison]::Ordinal)})}
$lines+=($hash+'  '+$fileName)
[IO.File]::WriteAllLines($sums,$lines,(New-Object Text.UTF8Encoding($false)))
Write-Output ('PORTABLE: '+$zip)
