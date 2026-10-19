[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$base=Join-Path $root ('runtime\installation-guard-test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $base -Force | Out-Null
function Assert($Value,[string]$Message){if(-not $Value){throw $Message}}
function Begin-Guard([string]$Folder,[int]$Owner=$PID,[string]$Mode='Install'){
    $id=[guid]::NewGuid().ToString('N');$ready=Join-Path $base ($id+'.ready');$release=$ready+'.release'
    $arguments=@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',('"'+(Join-Path $root 'installer\InstallationGuard.ps1')+'"'),'-Root',('"'+$Folder+'"'),'-OwnerPid',$Owner,'-Version','0.5.3','-Mode',$Mode,'-ReadyFile',('"'+$ready+'"'),'-ReleaseFile',('"'+$release+'"'))
    $process=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList $arguments -PassThru
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    while(-not(Test-Path -LiteralPath $ready) -and -not $process.HasExited -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 50}
    $state=if(Test-Path -LiteralPath $ready){[IO.File]::ReadAllText($ready)}else{''}
    return [pscustomobject]@{Process=$process;Ready=$ready;Release=$release;State=$state}
}
function Finish-Guard($Guard){
    [IO.File]::WriteAllText($Guard.Release,'STOP')
    Assert ($Guard.Process.WaitForExit(5000)) 'Maintenance guard did not release promptly.'
    $Guard.Process.Dispose()
}
function New-Fixture([string]$Name){
    $folder=Join-Path $base ($Name+'\client');New-Item -ItemType Directory -Path $folder -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $folder 'product.json'),'{"name":"CITA Route","version":"0.5.3"}')
    return $folder
}
try{
    $folder=New-Fixture 'Новая папка с пробелами'
    $guard=Begin-Guard $folder
    try{
        Assert ($guard.State -eq 'READY') ('Clean installation refused: '+$guard.State)
        foreach($name in @('connection-operation.lock','setup.lock','upgrade.lock','network-control.lock','installation-maintenance.lock')){
            $blocked=$false;try{$probe=[IO.File]::Open((Join-Path $folder ('runtime\'+$name)),'Open','ReadWrite','None');$probe.Dispose()}catch{$blocked=$true}
            Assert $blocked ('Missing held installation lock: '+$name)
        }
        $other=Begin-Guard $folder
        try{Assert ($other.State -ne 'READY' -and $other.State.Length -gt 0) 'Concurrent maintenance was not rejected.'}finally{Finish-Guard $other}
    }finally{Finish-Guard $guard}
    $probe=[IO.File]::Open((Join-Path $folder 'runtime\network-control.lock'),'Open','ReadWrite','None');$probe.Dispose()
    foreach($scenario in @('active','corrupt','updating','downgrade')){
        $folder=New-Fixture $scenario;New-Item -ItemType Directory -Path (Join-Path $folder 'runtime') -Force | Out-Null
        if($scenario -eq 'active'){[IO.File]::WriteAllText((Join-Path $folder 'runtime\session.json'),'{"running":true}')}
        if($scenario -eq 'corrupt'){[IO.File]::WriteAllText((Join-Path $folder 'runtime\session.json'),'{}')}
        if($scenario -eq 'downgrade'){[IO.File]::WriteAllText((Join-Path $folder 'product.json'),'{"name":"CITA Route","version":"0.5.4"}')}
        $busy=$null
        try{
            if($scenario -eq 'updating'){$busy=[IO.File]::Open((Join-Path $folder 'runtime\connection-operation.lock'),'OpenOrCreate','ReadWrite','None')}
            $guard=Begin-Guard $folder
            try{Assert ($guard.State -ne 'READY' -and $guard.State.Length -gt 0) ('Unsafe state accepted: '+$scenario)}finally{Finish-Guard $guard}
        }finally{if($busy){$busy.Dispose()}}
        if($scenario -eq 'downgrade'){
            $guard=Begin-Guard $folder -Mode Uninstall
            try{Assert ($guard.State -eq 'READY') 'An older uninstaller must still remove an in-app upgraded installation.'}finally{Finish-Guard $guard}
        }
    }
    $folder=New-Fixture 'crashed-owner'
    $owner=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 60' -PassThru
    $guard=Begin-Guard $folder -Owner $owner.Id
    try{
        Assert ($guard.State -eq 'READY') 'Owner lifecycle fixture did not start.'
        $owner.Kill();$owner.WaitForExit();$owner.Dispose()
        Assert ($guard.Process.WaitForExit(5000)) 'Crashed installer left maintenance locks held.'
    }finally{Finish-Guard $guard}
    Write-Output 'PASS: maintenance lock lifetime, contention, active/malformed sessions, Unicode paths, downgrade protection, upgraded uninstall and owner crash. Network unchanged.'
}finally{
    $prefix=[IO.Path]::GetFullPath((Join-Path $root 'runtime\installation-guard-test-'))
    if(-not [IO.Path]::GetFullPath($base).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe guard-test cleanup.'}
    Remove-Item -LiteralPath $base -Recurse -Force
}
