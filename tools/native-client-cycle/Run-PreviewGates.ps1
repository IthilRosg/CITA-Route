[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$StationAddress,
    [switch]$RoutingSample
)

$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$run=[guid]::NewGuid().ToString('N')
$folder=Join-Path $root ('runtime/preview-gates-'+$run)
New-Item -ItemType Directory -Path $folder -Force|Out-Null
$result=[ordered]@{schema=1;run=$run;stationAddress=$StationAddress;startedUtc=[DateTime]::UtcNow.ToString('o');success=$false;steps=@()}
function Invoke-Step([string]$Name,[string]$Script,[string[]]$Arguments){
    $log=Join-Path $folder ($Name+'.log')
    $clock=[Diagnostics.Stopwatch]::StartNew()
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root $Script) @Arguments *> $log
    $exit=$LASTEXITCODE
    $result.steps+=,[ordered]@{name=$Name;exit=$exit;milliseconds=$clock.ElapsedMilliseconds;log=[IO.Path]::GetFileName($log)}
    if($exit -ne 0){throw "$Name failed with exit $exit; inspect $log"}
}
try{
    Invoke-Step 'profile-import' 'tools/native-client-cycle/Run-ProfileImport.ps1' @('-StationAddress',$StationAddress)
    $cycle=@('-GuiAutoconnect','-StationAddress',$StationAddress)
    if($RoutingSample){$cycle+='-RoutingSample'}
    Invoke-Step 'gui-autoconnect-and-smoke' 'tools/native-client-cycle/Run.ps1' $cycle
    Invoke-Step 'service-cycle' 'tools/physical-service-cycle/Run-PhysicalCycle.ps1' @('-StationAddress',$StationAddress)
    $result.success=$true
}catch{$result.error=$_.Exception.Message}
finally{
    $result.finishedUtc=[DateTime]::UtcNow.ToString('o')
    $result|ConvertTo-Json -Depth 8|Set-Content (Join-Path $folder 'result.json') -Encoding utf8
    $result|ConvertTo-Json -Depth 8|Set-Content (Join-Path $root 'runtime/preview-gates-last.json') -Encoding utf8
}
if(-not $result.success){exit 1}
$result|ConvertTo-Json -Depth 8
