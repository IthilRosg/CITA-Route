param([Parameter(Mandatory)][string]$Stage)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'));$stagePath=[IO.Path]::GetFullPath($Stage).TrimEnd('\')
if([IO.Path]::GetDirectoryName($stagePath) -ine (Join-Path $workspace 'dist') -or [IO.Path]::GetFileName($stagePath) -cnotmatch '^CITA-Route-0\.6\.9-win-x64-framework-[a-f0-9]{8}$'){throw 'Stage location refused.'}
$active=Join-Path $workspace 'runtime/native-upgrade-stage-tests-active.json'
if(Test-Path -LiteralPath $active){throw 'Existing test observation requires reconciliation.'}
$manifest=Join-Path $stagePath 'manifest.json';$manifestHash=(Get-FileHash -LiteralPath $manifest).Hash
$entry=@{Stage=$stagePath;ManifestSha=$manifestHash;State='Starting';Utc=[datetime]::UtcNow.ToString('O')}
$f=[IO.File]::Open($active,'CreateNew','Write','Read');try{$bytes=[Text.Encoding]::UTF8.GetBytes(($entry|ConvertTo-Json -Compress));$f.Write($bytes,0,$bytes.Length);$f.Flush($true)}finally{$f.Dispose()}
$results=[Collections.Generic.List[object]]::new()
foreach($row in @(@('updater','runtime\native-updater\Cita.NativeUpdater.exe'),@('agent','runtime\agent-build\RoutePilot.Agent.exe'),@('app','RoutePilot.exe'))){
    $path=Join-Path $stagePath $row[1];$sha=(Get-FileHash -LiteralPath $path).Hash
    $info=[Diagnostics.ProcessStartInfo]::new();$info.FileName=$path;$info.Arguments='--self-test';$info.WorkingDirectory=$stagePath;$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $p=[Diagnostics.Process]::Start($info);if(!$p){throw 'Test process missing.'}
    $stdout=$p.StandardOutput.ReadToEndAsync();$stderr=$p.StandardError.ReadToEndAsync()
    $record=[ordered]@{Name=$row[0];Image=$path;ImageSha=$sha;Pid=$p.Id;StartedUtc=$p.StartTime.ToUniversalTime().ToString('O');State='Running';Stage=$stagePath;ManifestSha=$manifestHash}
    $record|ConvertTo-Json|Set-Content -LiteralPath $active
    try{
        $timer=[Diagnostics.Stopwatch]::StartNew()
        while(!$p.WaitForExit(200)){
            if($timer.Elapsed.TotalSeconds -gt 180){$p.Kill($true);if(!$p.WaitForExit(10000)){throw 'Test process retirement unconfirmed.'};throw 'Test exceeded its deadline; outcome failed.'}
        }
        if(![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout,$stderr),5000)){throw 'Test output incomplete.'}
        $record.State='Completed';$record.ExitCode=$p.ExitCode;$record.ElapsedMs=$timer.ElapsedMilliseconds
        $record|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $workspace ('runtime/native-upgrade-stage-test-'+$row[0]+'.json'))
        [IO.File]::WriteAllText((Join-Path $workspace ('runtime/native-upgrade-stage-test-'+$row[0]+'.log')),$stdout.Result+[Environment]::NewLine+$stderr.Result,[Text.UTF8Encoding]::new($false))
        $results.Add($record);if($p.ExitCode -ne 0){throw ('Staged self-test failed: '+$row[0])}
        $row[0]+' self-test exit 0'
    }finally{$p.Dispose()}
}
if((Get-FileHash -LiteralPath $manifest).Hash -cne $manifestHash){throw 'Stage manifest changed during tests.'}
$inventory=[IO.File]::ReadAllText($manifest)|ConvertFrom-Json
foreach($file in $inventory.files){$path=[IO.Path]::GetFullPath((Join-Path $stagePath $file.path));if(!$path.StartsWith($stagePath+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path).Hash -cne $file.sha256){throw 'Staged file changed during tests.'}}
$count=@(Get-ChildItem -LiteralPath $stagePath -File -Recurse).Count
if($count -ne @($inventory.files).Count+1){throw 'Unexpected files appeared in immutable stage.'}
$result=[ordered]@{Stage=$stagePath;ManifestSha=$manifestHash;SelfTests=$results;VerifiedFiles=@($inventory.files).Count;Utc=[datetime]::UtcNow.ToString('O');Scope='Staged artifact self-tests; no installed-service, routing or release acceptance'}
$result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $workspace 'runtime/native-upgrade-staged-bundle-result.json')
Move-Item -LiteralPath $active -Destination (Join-Path $workspace ('runtime/native-upgrade-stage-tests-completed-'+[IO.Path]::GetFileName($stagePath)+'.json'))
'Staged bundle self-tests and unchanged inventory PASS'
