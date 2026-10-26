param([string]$NativeSourceReceipt='runtime/native-corresponding-source.zip.receipt.json')
$ErrorActionPreference='Stop';$root=$PSScriptRoot
. (Join-Path $root 'Build-Dotnet.ps1');. (Join-Path $root 'FileStorage.ps1')
$product=Read-JsonFile (Join-Path $root 'product.json');if($product.edition -ne 'native'){throw 'Native-only product required'}
& (Join-Path $root 'Build-NativeComponents.ps1') -SourceReceipt $NativeSourceReceipt
$version=$product.version;$stage=Join-Path $root ('dist\CITA-Route-'+$version+'-win-x64-framework-'+[guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $stage -Force|Out-Null
$env:DOTNET_CLI_HOME=Join-Path $root 'runtime/.dotnet';$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1';$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
foreach($project in @('agent','app')){
 $name=if($project -eq 'agent'){'RoutePilot.Agent.csproj'}else{'RoutePilot.csproj'};$out=Join-Path $stage ('runtime/'+$project+'-build')
 & (Get-BuildDotnet) publish (Join-Path $root ($project+'/'+$name)) -c Release -r win-x64 -o $out --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:EnableSingleFileAnalyzer=false -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true
 if($LASTEXITCODE){throw ('Standalone publish failed: '+$project)}
}
Copy-Item (Join-Path $stage 'runtime/app-build/RoutePilot.exe') (Join-Path $stage 'RoutePilot.exe')
foreach($name in @('FileStorage.ps1','AgentPackage.ps1','AgentLease.ps1','NativeMaintenance.ps1','Install-NativeAgent.ps1','Install-NativeComponents.ps1','Uninstall-NativeAgent.ps1','DesktopRuntime.ps1','LICENSE','NOTICE','THIRD-PARTY.md')){Copy-Item (Join-Path $root $name) $stage}
Copy-Item (Join-Path $root 'installer/desktop-runtime.json') (Join-Path $stage 'desktop-runtime.json')
Copy-Item (Join-Path $root 'docs/NATIVE-PREVIEW.md') (Join-Path $stage 'README.md')
Copy-Item (Join-Path $root 'runtime/native-components.zip') (Join-Path $stage 'runtime/native-components.zip')
foreach($file in @('force-direct.txt','force-vpn.txt')){[IO.File]::WriteAllText((Join-Path $stage $file),'# Optional domain preferences.',[Text.UTF8Encoding]::new($false))}
foreach($dir in @('agent','app')){
 $target=Join-Path $stage ('source/'+$dir);New-Item -ItemType Directory -Path $target -Force|Out-Null
 Get-ChildItem (Join-Path $root $dir) -File|Where-Object {$_.Extension -in '.cs','.csproj','.xaml','.manifest'}|Copy-Item -Destination $target
}
New-Item -ItemType Directory -Path (Join-Path $stage 'source/app/Assets') -Force|Out-Null
Copy-Item (Join-Path $root 'app/Assets/RoutePilot.ico') (Join-Path $stage 'source/app/Assets')
Copy-Item (Join-Path $root 'Directory.Build.props') (Join-Path $stage 'source')
$source=Read-JsonFile (Join-Path $root $NativeSourceReceipt)
$product|Add-Member distribution @{schema=1;selfContained=$false;desktopRuntimeMajor=10;desktopRuntimeMinimum='10.0.13';agentSha256=(Get-FileHash (Join-Path $stage 'runtime/agent-build/RoutePilot.Agent.exe')).Hash;nativeComponentSha256=(Get-FileHash (Join-Path $stage 'runtime/native-components.zip')).Hash;nativeComponentSourceSha256=$source.sha256;nativeExperimental=$true} -Force
Write-JsonFile (Join-Path $stage 'product.json') $product
$files=@(Get-ChildItem $stage -File -Recurse|ForEach-Object {@{path=$_.FullName.Substring($stage.Length+1);bytes=$_.Length;sha256=(Get-FileHash $_.FullName).Hash}})
Write-JsonFile (Join-Path $stage 'manifest.json') @{version=$version;edition='native';architecture='win-x64';selfContained=$false;desktopRuntimeMajor=10;desktopRuntimeMinimum='10.0.13';files=$files}
$zip=Join-Path $root ('dist/CITA-Route-'+$version+'-win-x64-framework.zip');Compress-Archive -LiteralPath $stage -DestinationPath $zip -Force
[IO.File]::WriteAllText((Join-Path $root 'dist/SHA256SUMS.txt'),(Get-FileHash $zip).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($zip)+"`n",[Text.UTF8Encoding]::new($false))
'STANDALONE: '+$zip
