$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $root 'NativeMaintenance.ps1')
$fixture=Join-Path $root ('runtime\native-maintenance-unit-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture|Out-Null
$script:calls=0;$script:mode='forbid';$script:id=('a'*32)
function Invoke-NativeMaintenanceRpc([string]$Command,[string]$Operation='') {
    $script:calls++
    if($script:mode -eq 'forbid'){throw 'Unexpected coordinator call'}
    if($script:mode -eq 'offline'){throw 'Offline'}
    if($script:mode -eq 'unavailable'){return @{Native=@{Phase='Unavailable'}}}
    if($Command -eq 'native-stop'){
        if($Operation -ne $script:id){throw 'Wrong operation'}
        if($script:mode -eq 'refused'){throw 'Refused'}
        return @{Success=$true}
    }
    if($script:calls -eq 1){return @{Native=@{Phase='Connected';Desired=$true;Operation=$script:id}}}
    return @{Native=@{Phase='Disconnected';Desired=$false;Operation=$(if($script:mode -eq 'changed'){'b'*32}else{$script:id})}}
}
Stop-NativeForMaintenance $fixture
if($script:calls){throw 'Absent native configuration queried coordinator'}
$settings=Join-Path $fixture 'service.json'
'{"Enabled":false}'|Set-Content -LiteralPath $settings -Encoding UTF8
Stop-NativeForMaintenance $fixture
'{"Enabled":true}'|Set-Content -LiteralPath $settings -Encoding UTF8
foreach($case in @('success','offline','unavailable','refused','changed')) {
    $script:mode=$case;$script:calls=0;$failed=$false
    try{Stop-NativeForMaintenance $fixture}catch{$failed=$true}
    if(($case -eq 'success') -eq $failed){throw ('Unexpected maintenance outcome: '+$case)}
    if($case -eq 'success' -and $script:calls -ne 3){throw 'Disconnect not confirmed'}
}
'{}'|Set-Content -LiteralPath $settings -Encoding UTF8
$failed=$false;try{Stop-NativeForMaintenance $fixture}catch{$failed=$true}
if(-not $failed){throw 'Malformed settings accepted'}
[IO.File]::Delete($settings);[IO.Directory]::Delete($fixture)
'PASS: absent/disabled configuration, confirmed stop, offline/refused/changed state and malformed settings.'
