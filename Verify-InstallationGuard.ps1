[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$base=Join-Path $root ('runtime\installation-guard-test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $base -Force | Out-Null
$guards=New-Object 'System.Collections.Generic.List[object]'
$testFailure=$null
$bodyFailed=$false
$incoming=Join-Path $base 'incoming.json'
$entries=@(1..22|ForEach-Object {@{path=('public-'+$_+'.txt');bytes=0;sha256=('0'*64)}})
@{version='0.5.3';architecture='win-x64';files=$entries}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $incoming -Encoding UTF8
function Assert($Value,[string]$Message){if(-not $Value){throw $Message}}
function Begin-Guard([string]$Folder,[int]$Owner=$PID,[string]$Mode='Install'){
    $id=[guid]::NewGuid().ToString('N');$ready=Join-Path $base ($id+'.ready');$release=$ready+'.release'
    $arguments=@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',('"'+(Join-Path $root 'installer\InstallationGuard.ps1')+'"'),'-Root',('"'+$Folder+'"'),'-OwnerPid',$Owner,'-Version','0.5.3','-Mode',$Mode,'-ReadyFile',('"'+$ready+'"'),'-ReleaseFile',('"'+$release+'"'),'-IncomingManifest',('"'+$incoming+'"'))
    $process=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList $arguments -PassThru
    $null=$process.Handle
    $guard=[pscustomobject]@{Process=$process;Ready=$ready;Release=$release;State='';Finished=$false}
    $guards.Add($guard)
    $deadline=[DateTime]::UtcNow.AddSeconds(30)
    $state=''
    do{
        try{if(Test-Path -LiteralPath $ready){$state=[IO.File]::ReadAllText($ready)}}catch [IO.IOException]{}
        if($state -or $process.HasExited){break}
        Start-Sleep -Milliseconds 50
    }while([DateTime]::UtcNow -lt $deadline)
    $guard.State=$state
    if(-not $state){throw ('Maintenance fixture did not report readiness within 30 seconds. Folder: '+(Split-Path (Split-Path $Folder) -Leaf))}
    return $guard
}
function Finish-Guard($Guard){
    if($Guard.Finished){return}
    [IO.File]::WriteAllText($Guard.Release,'STOP')
    Assert ($Guard.Process.WaitForExit(15000)) ('Maintenance guard did not release within 15 seconds. Last state: '+$Guard.State)
    $Guard.Finished=$true
    $Guard.Process.Dispose()
}
function New-Fixture([string]$Name){
    $folder=Join-Path $base ($Name+'\client');New-Item -ItemType Directory -Path $folder -Force | Out-Null
    # The incoming version is newer; unrelated guard checks must not be
    # short-circuited by the equal-version confirmation requirement.
    [IO.File]::WriteAllText((Join-Path $folder 'product.json'),'{"name":"CITA Route","version":"0.5.2"}')
    return $folder
}
try{
    $equal=New-Fixture 'equal-version'
    [IO.File]::WriteAllText((Join-Path $equal 'product.json'),'{"name":"CITA Route","version":"0.5.3"}')
    $guard=Begin-Guard $equal
    try{Assert ($guard.State -eq 'VERSION_SAME|0.5.3.0') 'Equal version must require explicit confirmation.'}finally{Finish-Guard $guard}
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
    $retained=New-Fixture 'retained-data'
    $guard=Begin-Guard $retained -Mode Uninstall
    try{Assert ($guard.State -eq 'READY') 'Retained-data uninstall was refused.'}finally{Finish-Guard $guard}
    $marker=Join-Path $retained 'data\installation-owner.json'
    Assert (Test-Path -LiteralPath $marker) 'Uninstall did not retain data ownership.'
    Remove-Item -LiteralPath (Join-Path $retained 'product.json')
    $guard=Begin-Guard $retained
    try{Assert ($guard.State -eq 'READY') 'Installation after uninstall rejected its own retained data.'}finally{Finish-Guard $guard}
    $metadata=[IO.File]::ReadAllText($marker)|ConvertFrom-Json
    foreach($mismatch in @('root','userSid','schema')){
        $copy=$metadata|ConvertTo-Json|ConvertFrom-Json
        if($mismatch -eq 'root'){$copy.root=$retained+'-copy'}
        if($mismatch -eq 'userSid'){$copy.userSid='S-1-5-18'}
        if($mismatch -eq 'schema'){$copy.schema=99}
        [IO.File]::WriteAllText($marker,($copy|ConvertTo-Json))
        $guard=Begin-Guard $retained
        try{Assert ($guard.State -ne 'READY') ('Unrelated retained data was accepted: '+$mismatch)}finally{Finish-Guard $guard}
    }
    [IO.File]::WriteAllText($marker,'invalid-json')
    $guard=Begin-Guard $retained
    try{Assert ($guard.State -ne 'READY') 'Malformed retained-data ownership was accepted.'}finally{Finish-Guard $guard}
    $foreign=New-Fixture 'unrelated-retained-directory'
    Remove-Item -LiteralPath (Join-Path $foreign 'product.json')
    [IO.File]::WriteAllText((Join-Path $foreign 'unrelated.txt'),'do-not-touch')
    $guard=Begin-Guard $foreign
    try{Assert ($guard.State -ne 'READY') 'Unmarked unrelated directory was accepted as retained personal data.'}finally{Finish-Guard $guard}
    Assert ([IO.File]::ReadAllText((Join-Path $foreign 'unrelated.txt')) -eq 'do-not-touch') 'Refused installation changed unrelated data.'
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
    Write-Output 'PASS: maintenance lock lifetime, contention, active/malformed sessions, Unicode paths, downgrade protection, upgraded uninstall, retained-data reinstall/owner guards and owner crash. Network unchanged.'
}catch{
    $testFailure=$_
    $bodyFailed=$true
    throw
}finally{
    # A readiness/assertion timeout must not leave our child holding fixture
    # locks, or recursive cleanup masks the actual failed assertion on slow CI.
    foreach($pending in $guards){
        if($pending.Finished){continue}
        try{Finish-Guard $pending}
        catch{
            try{if(-not $pending.Process.HasExited){$pending.Process.Kill();[void]$pending.Process.WaitForExit(5000)}}finally{$pending.Process.Dispose();$pending.Finished=$true}
            if(-not $testFailure){$testFailure=$_}
            Write-Warning 'An owned maintenance fixture required termination; its original failure is retained.'
        }
    }
    $prefix=[IO.Path]::GetFullPath((Join-Path $root 'runtime\installation-guard-test-'))
    if(-not [IO.Path]::GetFullPath($base).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe guard-test cleanup.'}
    try{Remove-Item -LiteralPath $base -Recurse -Force}
    catch{if(-not $testFailure){throw};Write-Warning ('Fixture cleanup did not finish; original error: '+$testFailure.Exception.Message)}
    if($testFailure -and -not $bodyFailed){throw $testFailure}
}
