[CmdletBinding()]
param([ValidateRange(10,7200)][int]$Seconds=60,[ValidateRange(2,60)][int]$IntervalSeconds=10)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
New-Item -ItemType Directory -Path (Join-Path $root 'logs') -Force | Out-Null
$cores=[int](Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors
$samples=New-Object 'System.Collections.Generic.List[object]'
$clock=[Diagnostics.Stopwatch]::StartNew()
do{
    $rows=@(Get-CimInstance Win32_PerfRawData_PerfProc_Process | Where-Object {$_.Name -match '^(RoutePilot(?:\.Agent)?|smart-router|vpn-bridge|wiresock-connect-service)(#\d+)?$'} |
        Select-Object Name,IDProcess,PercentProcessorTime,Timestamp_Sys100NS,ElapsedTime,WorkingSet,PrivateBytes,HandleCount,ThreadCount,IOReadBytesPersec,IOWriteBytesPersec)
    $samples.Add([pscustomobject]@{utc=[DateTime]::UtcNow.ToString('o');rows=$rows})
    if($clock.Elapsed.TotalSeconds -ge $Seconds){break}
    Start-Sleep -Seconds ([Math]::Min($IntervalSeconds,[Math]::Max(1,[int]($Seconds-$clock.Elapsed.TotalSeconds))))
}while($true)
$results=@(foreach($id in @($samples.rows.IDProcess | Sort-Object -Unique)){
    $rows=@($samples | ForEach-Object {$_.rows | Where-Object IDProcess -eq $id})
    if($rows.Count -lt 2){continue}
    $first=$rows[0];$last=$rows[-1]
    $ticks=[double]$last.Timestamp_Sys100NS-[double]$first.Timestamp_Sys100NS
    if($ticks -le 0 -or $first.ElapsedTime -ne $last.ElapsedTime){continue}
    $cpu=100*([double]$last.PercentProcessorTime-[double]$first.PercentProcessorTime)/$ticks
    [pscustomobject]@{pid=$id;name=$last.Name;seconds=[Math]::Round($ticks/1e7,1);cpuMachinePercent=[Math]::Round($cpu/$cores,3);cpuOneCorePercent=[Math]::Round($cpu,2);
        workingSetMiB=[Math]::Round([double]$last.WorkingSet/1MB,1);privateMiB=[Math]::Round([double]$last.PrivateBytes/1MB,1);
        privateDeltaMiB=[Math]::Round(([double]$last.PrivateBytes-[double]$first.PrivateBytes)/1MB,1);handles=$last.HandleCount;handleDelta=([long]$last.HandleCount-[long]$first.HandleCount);threads=$last.ThreadCount}
})
$report=[pscustomobject]@{kind='passive-current-workload';startedUtc=$samples[0].utc;finishedUtc=$samples[-1].utc;logicalProcessors=$cores;components=$results;
    notes=@('No load generation, VPN connection, restart or route changes.','Working sets include shared pages; private bytes are committed memory, not physical RAM.','Short-lived CLI/PowerShell helpers are not fully attributed.','A short sample cannot establish absence of leaks.');samples=$samples}
$path=Join-Path $root ('logs\resources-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'.json')
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
$results | Format-Table name,cpuMachinePercent,workingSetMiB,privateMiB,handleDelta
Write-Output ('REPORT: '+$path)
