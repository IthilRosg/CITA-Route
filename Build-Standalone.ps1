param([string]$NativeSourceReceipt='runtime/native-corresponding-source.zip.receipt.json',[switch]$StageOnly,[switch]$IncludeZapretInternal)
$ErrorActionPreference='Stop';$root=$PSScriptRoot
if($IncludeZapretInternal -and -not $StageOnly){throw 'Zapret internal components are limited to unpublished staging.'}
. (Join-Path $root 'Build-Dotnet.ps1');. (Join-Path $root 'FileStorage.ps1')
function Get-StandaloneBuildInputs {
 $files=[Collections.Generic.List[string]]::new()
 foreach($part in @('agent','app','installer/native-updater')){foreach($file in Get-ChildItem -LiteralPath (Join-Path $root $part) -File -Recurse | Where-Object {$_.FullName -notmatch '[\\/](bin|obj)[\\/]'}){
  if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Standalone source reparse point refused'};$files.Add($file.FullName)
 }}
 foreach($name in @('product.json','Directory.Build.props','Build-Dotnet.ps1','Build-Standalone.ps1','FileStorage.ps1','AgentPackage.ps1','AgentLease.ps1','NativeMaintenance.ps1','Install-NativeAgent.ps1','Install-NativeComponents.ps1','Invoke-NativeBundleUpgrade.ps1','Uninstall-NativeAgent.ps1','DesktopRuntime.ps1','LICENSE','NOTICE','THIRD-PARTY.md','installer/desktop-runtime.json','installer/NativeUpgradeTransaction.ps1','installer/NativeUpgradePair.ps1','installer/NativeUpgradeService.ps1','installer/NativeUpgradeFlow.ps1','installer/NativeUpgradeWorker.ps1','installer/NativeUpgradeRecovery.ps1','installer/Actor.ps1','installer/Recover.ps1','installer/Start-NativeUpgrade.ps1','docs/NATIVE-PREVIEW.md')){$files.Add((Join-Path $root $name))}
 $entries=@(foreach($path in @($files|Sort-Object)){ $file=Get-Item -LiteralPath $path -Force;if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Standalone input reparse point refused'};@{path=$file.FullName.Substring($root.Length+1);sha256=(Get-FileHash -LiteralPath $file.FullName).Hash} })
 $dotnet=[IO.Path]::GetFullPath((Get-BuildDotnet));return @{files=$entries;dotnetSha256=(Get-FileHash -LiteralPath $dotnet).Hash}
}
$product=Read-JsonFile (Join-Path $root 'product.json');if($product.edition -ne 'native'){throw 'Native-only product required'}
$nativePackage='runtime/native-components.zip'
if($IncludeZapretInternal){$nativePackage='runtime/native-components-zapret-stage-'+[guid]::NewGuid().ToString('N')+'.zip';& (Join-Path $root 'Build-NativeComponents.ps1') -SourceReceipt $NativeSourceReceipt -Destination $nativePackage -IncludeZapret}
else{& (Join-Path $root 'Build-NativeComponents.ps1') -SourceReceipt $NativeSourceReceipt}
$beforeInputs=Get-StandaloneBuildInputs
$version=$product.version;$stage=Join-Path $root ('dist\CITA-Route-'+$version+'-win-x64-framework-'+[guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $stage -Force|Out-Null
$env:DOTNET_CLI_HOME=Join-Path $root 'runtime/.dotnet';$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1';$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
foreach($project in @('agent','app')){
 $name=if($project -eq 'agent'){'RoutePilot.Agent.csproj'}else{'RoutePilot.csproj'};$out=Join-Path $stage ('runtime/'+$project+'-build')
 & (Get-BuildDotnet) publish (Join-Path $root ($project+'/'+$name)) -c Release -r win-x64 -o $out --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:EnableSingleFileAnalyzer=false -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true
 if($LASTEXITCODE){throw ('Standalone publish failed: '+$project)}
}
& (Get-BuildDotnet) publish (Join-Path $root 'installer/native-updater/Cita.NativeUpdater.csproj') -c Release -r win-x64 -o (Join-Path $stage 'runtime/native-updater') --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:EnableSingleFileAnalyzer=false -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true
if($LASTEXITCODE){throw 'Standalone publish failed: native-updater'}
Copy-Item (Join-Path $stage 'runtime/app-build/RoutePilot.exe') (Join-Path $stage 'RoutePilot.exe')
foreach($name in @('FileStorage.ps1','AgentPackage.ps1','AgentLease.ps1','NativeMaintenance.ps1','Install-NativeAgent.ps1','Install-NativeComponents.ps1','Invoke-NativeBundleUpgrade.ps1','Uninstall-NativeAgent.ps1','DesktopRuntime.ps1','LICENSE','NOTICE','THIRD-PARTY.md')){Copy-Item (Join-Path $root $name) $stage}
Copy-Item (Join-Path $root 'installer/desktop-runtime.json') (Join-Path $stage 'desktop-runtime.json')
New-Item -ItemType Directory -Path (Join-Path $stage 'installer') -Force|Out-Null
foreach($name in @('NativeUpgradeTransaction.ps1','NativeUpgradePair.ps1','NativeUpgradeService.ps1','NativeUpgradeFlow.ps1','NativeUpgradeWorker.ps1','NativeUpgradeRecovery.ps1','Actor.ps1','Recover.ps1','Start-NativeUpgrade.ps1')){Copy-Item (Join-Path $root ('installer/'+$name)) (Join-Path $stage ('installer/'+$name))}
Copy-Item (Join-Path $root 'docs/NATIVE-PREVIEW.md') (Join-Path $stage 'README.md')
Copy-Item (Join-Path $root $nativePackage) (Join-Path $stage 'runtime/native-components.zip')
foreach($file in @('force-direct.txt','force-vpn.txt')){[IO.File]::WriteAllText((Join-Path $stage $file),'# Optional domain preferences.',[Text.UTF8Encoding]::new($false))}
foreach($dir in @('agent','app')){
 $target=Join-Path $stage ('source/'+$dir);New-Item -ItemType Directory -Path $target -Force|Out-Null
 Get-ChildItem (Join-Path $root $dir) -File|Where-Object {$_.Extension -in '.cs','.csproj','.xaml','.manifest'}|Copy-Item -Destination $target
}
New-Item -ItemType Directory -Path (Join-Path $stage 'source/app/Assets') -Force|Out-Null
Copy-Item (Join-Path $root 'app/Assets/RoutePilot.ico') (Join-Path $stage 'source/app/Assets')
Copy-Item (Join-Path $root 'Directory.Build.props') (Join-Path $stage 'source')
New-Item -ItemType Directory -Path (Join-Path $stage 'source/installer/native-updater') -Force|Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'installer/native-updater') -File|Where-Object {$_.Extension -in '.cs','.csproj'}|Copy-Item -Destination (Join-Path $stage 'source/installer/native-updater')
$afterInputs=Get-StandaloneBuildInputs
if(($beforeInputs|ConvertTo-Json -Depth 6 -Compress) -cne ($afterInputs|ConvertTo-Json -Depth 6 -Compress)){throw 'Standalone source or toolchain changed during publish.'}
Write-JsonFile (Join-Path $stage 'build-inputs.json') @{schema=1;files=$beforeInputs.files;dotnetSha256=$beforeInputs.dotnetSha256;limitations=@('SDK and package dependency inventory is not attested')}
$source=Read-JsonFile (Join-Path $root $NativeSourceReceipt)
$product|Add-Member distribution @{schema=1;selfContained=$false;desktopRuntimeMajor=10;desktopRuntimeMinimum='10.0.13';agentSha256=(Get-FileHash (Join-Path $stage 'runtime/agent-build/RoutePilot.Agent.exe')).Hash;nativeComponentSha256=(Get-FileHash (Join-Path $stage 'runtime/native-components.zip')).Hash;nativeComponentSourceSha256=$source.sha256;nativeExperimental=$true;zapretInternal=[bool]$IncludeZapretInternal;redistributionReady=$false} -Force
Write-JsonFile (Join-Path $stage 'product.json') $product
$files=@(Get-ChildItem $stage -File -Recurse|ForEach-Object {@{path=$_.FullName.Substring($stage.Length+1);bytes=$_.Length;sha256=(Get-FileHash $_.FullName).Hash}})
Write-JsonFile (Join-Path $stage 'manifest.json') @{version=$version;edition='native';architecture='win-x64';selfContained=$false;desktopRuntimeMajor=10;desktopRuntimeMinimum='10.0.13';zapretInternal=[bool]$IncludeZapretInternal;redistributionReady=$false;files=$files}
if($StageOnly){'STANDALONE-STAGE: '+$stage;return}
$zip=Join-Path $root ('dist/CITA-Route-'+$version+'-win-x64-framework.zip');Compress-Archive -LiteralPath $stage -DestinationPath $zip -Force
[IO.File]::WriteAllText((Join-Path $root 'dist/SHA256SUMS.txt'),(Get-FileHash $zip).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($zip)+"`n",[Text.UTF8Encoding]::new($false))
'STANDALONE: '+$zip
