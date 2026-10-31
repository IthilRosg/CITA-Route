param([string]$ChildRoot='',[int]$ExpectedCount=0,[string]$ExpectedPhase='Disconnected')
$ErrorActionPreference='Stop'
. "$PSScriptRoot\NativeUpgradeWorker.ps1"
if($ChildRoot){
    $dto=Read-NativeUpgradeWorkerJson ([pscustomobject]@{Root=$ChildRoot}) 'upgrade-input.json'
    $rows=Convert-NativeUpgradeEntries @($dto.OldNative) $true
    if(@($rows).Count -ne $ExpectedCount -or $dto.OldNative[0] -is [array] -or $dto.ExpectedPhase -cne $ExpectedPhase){throw 'Cross-process native input shape or expected phase differs.'}
    'Worker input JSON accepted '+$ExpectedCount+' entries';exit 0
}
$base=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../runtime')).TrimEnd('\');$root=Join-Path $base ('input-boundary-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
try{
    $fixture=Join-Path $root 'manifest-pins';[void][IO.Directory]::CreateDirectory($fixture)
    [IO.File]::WriteAllText((Join-Path $fixture 'agent.exe'),'pinned agent')
    $row=[pscustomobject]@{path='agent.exe';sha256=(Get-NativeUpgradeHash (Join-Path $fixture 'agent.exe'))}
    $manifest=[pscustomobject]@{files=@($row)};$sources=@{'Incoming.Agent.exe'='agent.exe'}
    $pins=Get-NativeBundleSourcePins $fixture $manifest $sources
    if($pins['Incoming.Agent.exe'] -cne $row.sha256){throw 'Regex validation lost the manifest digest.'}
    $manifest.files=@($row,$row);$rejected=$false;try{$null=Get-NativeBundleSourcePins $fixture $manifest $sources}catch{$rejected=$true};if(!$rejected){throw 'Duplicate bundle input accepted.'}
    $manifest.files=@($row);[IO.File]::WriteAllText((Join-Path $fixture 'agent.exe'),'changed agent')
    $rejected=$false;try{$null=Get-NativeBundleSourcePins $fixture $manifest $sources}catch{$rejected=$true};if(!$rejected){throw 'Changed bundle input accepted.'}
    foreach($count in @(1,3)){
        $phase=if($count -eq 1){'Unconfigured'}else{'Disconnected'}
        Assert-NativePreparationState ([pscustomobject]@{Phase=$phase;Desired=$false})
        $dir=Join-Path $root ([string]$count);[void][IO.Directory]::CreateDirectory((Join-Path $dir 'native'))
        [IO.File]::WriteAllText((Join-Path $dir 'RoutePilot.Agent.exe'),'fixture image')
        for($n=0;$n -lt $count;$n++){[IO.File]::WriteAllText((Join-Path $dir ('native\file-'+$n+'.bin')),'fixture native')}
        $lease=Open-NativeUpgradeLease $dir
        try{
            $dto=New-NativeUpgradeInput $lease ('a'*32) $dir ([pscustomobject]@{ProcessId=1;StartedUtcTicks=1L;StartType='Automatic'}) ([pscustomobject]@{Desired=$false;Operation=('b'*32);Generation=7}) ('C'*64) ('D'*64) $phase
            [IO.File]::WriteAllText((Join-Path $dir 'upgrade-input.json'),($dto|ConvertTo-Json -Depth 8 -Compress),[Text.UTF8Encoding]::new($false))
        }finally{$lease.Handle.Dispose()}
        $info=[Diagnostics.ProcessStartInfo]::new();$info.FileName=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';$info.Arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -ChildRoot "'+$dir+'" -ExpectedCount '+$count+' -ExpectedPhase '+$phase;$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
        $p=[Diagnostics.Process]::Start($info);$output=$p.StandardOutput.ReadToEndAsync();$errorText=$p.StandardError.ReadToEndAsync()
        try{if(!$p.WaitForExit(20000)){throw 'Input boundary child observation expired.'};if(![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($output,$errorText),5000) -or $p.ExitCode -ne 0){throw 'Input boundary child failed.'};$output.Result.Trim()}finally{$p.Dispose()}
    }
}finally{
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($root)) -ine $base -or [IO.Path]::GetFileName($root) -cnotmatch '^input-boundary-[a-f0-9]{32}$'){throw 'Input boundary cleanup escaped root.'}
    Assert-NativeUpgradePath $root
    foreach($item in Get-ChildItem -LiteralPath $root -Recurse -Force){if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Input fixture linked.'}}
    [IO.Directory]::Delete($root,$true)
}
'Native upgrade input producer -> separate Windows PowerShell worker PASS'
