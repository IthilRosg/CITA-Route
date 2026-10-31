param([switch]$IncompatibleAgentFixture,[string]$StageName='CITA-Route-0.6.9-win-x64-framework-70e50ef0',[string]$ExpectedManifestSha='4101CC70830C3BC95E6B85947F9FFAF98BFC1C0CE449B478C4EC46141159E60F')
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
$journal=Join-Path $workspace 'runtime/native-upgrade-live-active.json'
if(Test-Path -LiteralPath $journal){throw 'Existing live native upgrade must be reconciled first.'}
if($StageName -cnotmatch '^CITA-Route-0\.6\.9-win-x64-framework-[a-f0-9]{8}$' -or $ExpectedManifestSha -cnotmatch '^[A-F0-9]{64}$'){throw 'Incoming stage identity refused.'}
$stage=Join-Path $workspace ('dist/'+$StageName)
if((Get-FileHash -LiteralPath (Join-Path $stage 'manifest.json')).Hash -cne $ExpectedManifestSha){throw 'Incoming immutable snapshot differs.'}
$manifest=[IO.File]::ReadAllText((Join-Path $stage 'manifest.json'))|ConvertFrom-Json
foreach($relative in @('runtime\agent-build\RoutePilot.Agent.exe','runtime\native-components.zip')){
    $entries=@($manifest.files|Where-Object {$_.path.Replace('/','\') -ieq $relative})
    if($entries.Count -ne 1 -or (Get-FileHash -LiteralPath (Join-Path $stage $relative)).Hash -cne $entries[0].sha256){throw 'Incoming stage file differs from its manifest.'}
}
$sources=[ordered]@{'Cita.NativeUpdater.exe'=(Join-Path $workspace 'runtime/native-updater-build/Cita.NativeUpdater.exe');'Incoming.Agent.exe'=(Join-Path $stage 'runtime/agent-build/RoutePilot.Agent.exe');'native-components.zip'=(Join-Path $stage 'runtime/native-components.zip')}
if($IncompatibleAgentFixture){
    $original=Join-Path $workspace 'runtime/physical-original-protected-agent.exe'
    if((Get-FileHash -LiteralPath $original).Hash -cne '4F1C0AF3A7FFF0DE5AEDD4645F590867EA24BDD43B3A51B8C8075EFF4F0A741E'){throw 'Known incompatible test image differs.'}
    $sources['Incoming.Agent.exe']=$original
}
foreach($name in @('Actor.ps1','Recover.ps1','NativeUpgradeWorker.ps1','NativeUpgradeRecovery.ps1','NativeUpgradeFlow.ps1','NativeUpgradeTransaction.ps1','NativeUpgradePair.ps1','NativeUpgradeService.ps1','Start-NativeUpgrade.ps1')){$sources[$name]=Join-Path $workspace ('installer/'+$name)}
foreach($name in @('Install-NativeComponents.ps1','NativeMaintenance.ps1','AgentLease.ps1')){$sources[$name]=Join-Path $workspace $name}
$pins=@{};foreach($name in $sources.Keys){$pins[$name]=(Get-FileHash -LiteralPath $sources[$name]).Hash}
$recipe=Join-Path $workspace 'tools/native-control-probe/Test-PhysicalTransfer.ps1'
if((Get-FileHash -LiteralPath $recipe).Hash -cne '42CB3AA03261C76905617CAE5EBFAFE3F927229E5672B3278B4EFC9A6758BECE'){throw 'Directory recipe differs.'}
$create=[regex]::Match([IO.File]::ReadAllText($recipe),'(?s)\$create=@''\r?\n(.*?)\r?\n''@').Groups[1].Value
if(!$create){throw 'Directory recipe missing.'}
$id=[guid]::NewGuid().ToString('N');$remote='C:\Program Files\RoutePilot\Agent\native-update-'+$id
$entry=[ordered]@{Schema=1;Run=$id;Root=$remote;State='MayCreate';Stage=$StageName;StageManifest=$ExpectedManifestSha;IncompatibleAgentFixture=[bool]$IncompatibleAgentFixture;Pins=$pins;Utc=[datetime]::UtcNow.ToString('O')}
$file=[IO.File]::Open($journal,'CreateNew','Write','Read');try{$bytes=[Text.Encoding]::UTF8.GetBytes(($entry|ConvertTo-Json -Depth 5 -Compress));$file.Write($bytes,0,$bytes.Length);$file.Flush($true)}finally{$file.Dispose()}
if((Invoke-BoundedRm $create.Replace('__DIR__',$remote)).Trim() -cne 'ready'){throw 'Live run directory unconfirmed.'}
foreach($name in $sources.Keys){Invoke-BoundedScp $sources[$name] ('citastation@192.168.1.124:'+($remote.Replace('\','/')+'/'+$name))}
foreach($name in $sources.Keys){if((Get-FileHash -LiteralPath $sources[$name]).Hash -cne $pins[$name]){throw 'Source changed during transfer; do not arm.'}}
$check='$ErrorActionPreference=''Stop'';$r='''+$remote+''';$pins='''+($pins|ConvertTo-Json -Compress)+'''|ConvertFrom-Json;foreach($p in $pins.PSObject.Properties){if((Get-FileHash -LiteralPath (Join-Path $r $p.Name)).Hash -cne $p.Value){throw ''Transferred payload differs.''}};''verified'''
if((Invoke-BoundedRm $check 30).Trim() -cne 'verified'){throw 'Transferred closure unconfirmed.'}
$start=@'
$ErrorActionPreference='Stop';$root='__ROOT__'
$log=Join-Path $env:TEMP 'cita-native-start-__ID__.log';$err=$log+'.err'
$info=[Diagnostics.ProcessStartInfo]::new();$info.FileName=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';$info.Arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $root 'Start-NativeUpgrade.ps1')+'" -RunRoot "'+$root+'" -ExpectedHelperSha __HELPER__ -NewAgentSha __AGENT__ -ArchiveSha __ZIP__';$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
$p=[Diagnostics.Process]::Start($info);$out=$p.StandardOutput.ReadToEndAsync();$errorText=$p.StandardError.ReadToEndAsync()
if(!$p.WaitForExit(55000)){throw 'Bootstrap observation expired; remote outcome unknown.'}
if(![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($out,$errorText),5000)){throw 'Bootstrap output incomplete.'}
[IO.File]::WriteAllText($log,$out.Result,[Text.UTF8Encoding]::new($false));[IO.File]::WriteAllText($err,$errorText.Result,[Text.UTF8Encoding]::new($false))
if($p.ExitCode -ne 0){[pscustomobject]@{Run='__ID__';BootstrapExit=$p.ExitCode;Outcome='InspectProtectedRunAndLocalBootstrapLog'}|ConvertTo-Json -Compress;return}
$out.Result
'@
$start=$start.Replace('__ROOT__',$remote).Replace('__ID__',$id).Replace('__HELPER__',$pins['Cita.NativeUpdater.exe']).Replace('__AGENT__',$pins['Incoming.Agent.exe']).Replace('__ZIP__',$pins['native-components.zip'])
$result=Invoke-BoundedRm $start 70
$result|Set-Content -LiteralPath (Join-Path $workspace ('runtime/native-upgrade-live-start-'+$id+'.json'))
$result
