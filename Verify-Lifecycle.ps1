$ErrorActionPreference='Stop'
$project=$PSScriptRoot
$base=Join-Path $project ('runtime\lifecycle-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $base -Force|Out-Null
function Assert($Value,[string]$Message){if(-not $Value){throw $Message}}
try{
    foreach($action in @('Start','ProxyOnly','Reconnect','RepairVpn','SyncProfile','Prepare','Stop','Status','Watch','PrepareSetup')){
        $folder=Join-Path $base $action;$runtime=Join-Path $folder 'runtime'
        New-Item -ItemType Directory -Path $runtime -Force|Out-Null
        foreach($file in @('SmartRouting.ps1','FileStorage.ps1')){Copy-Item -LiteralPath (Join-Path $project $file) -Destination $folder}
        [IO.File]::WriteAllText((Join-Path $folder 'ConnectionEnvironment.ps1'),@'
function Get-ConnectionSettings { @{CliPath='fixture';SourceProfile='fixture'} }
function Find-WireSockCli { 'fixture.exe' }
'@)
        # Halt the real entry point immediately AFTER its setup gate. No admin,
        # service, CLI, network or process-control code can be reached.
        [IO.File]::WriteAllText((Join-Path $folder 'RuleFeeds.ps1'),@'
[IO.File]::WriteAllText((Join-Path $runtime 'gate.ready'),'ready')
$deadline=[Diagnostics.Stopwatch]::StartNew()
while(-not(Test-Path -LiteralPath (Join-Path $runtime 'gate.release')) -and $deadline.Elapsed.TotalSeconds -lt 15){Start-Sleep -Milliseconds 25}
throw 'Controlled fixture end before any network or elevation operation.'
'@)
        $actual=if($action -eq 'PrepareSetup'){'Prepare'}else{$action}
        $arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $folder 'SmartRouting.ps1')+'" -Action '+$actual
        if($action -eq 'PrepareSetup'){$arguments+=' -Setup'}
        $process=Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList $arguments -RedirectStandardError (Join-Path $folder 'error.log') -RedirectStandardOutput (Join-Path $folder 'output.log')
        try{
            $deadline=[Diagnostics.Stopwatch]::StartNew()
            while(-not(Test-Path -LiteralPath (Join-Path $runtime 'gate.ready')) -and -not $process.HasExited -and $deadline.Elapsed.TotalSeconds -lt 10){Start-Sleep -Milliseconds 25}
            Assert (Test-Path -LiteralPath (Join-Path $runtime 'gate.ready')) ('Entry did not reach the fixture: '+$action)
            $blocked=$false;$probe=$null
            try{$probe=[IO.File]::Open((Join-Path $runtime 'setup.lock'),'OpenOrCreate','ReadWrite','None')}catch [IO.IOException]{$blocked=$true}finally{if($probe){$probe.Dispose()}}
            $expected=$action -notin @('Stop','Status','Watch','PrepareSetup')
            Assert ($blocked -eq $expected) ('Setup can overlap the live action after its initial check: '+$action+'; expected gate='+$expected+'; observed='+$blocked)
        }finally{
            [IO.File]::WriteAllText((Join-Path $runtime 'gate.release'),'release')
            if(-not $process.WaitForExit(4000)){$process.Kill();[void]$process.WaitForExit(2000)}
            $process.Dispose()
        }
        $probe=[IO.File]::Open((Join-Path $runtime 'setup.lock'),'OpenOrCreate','ReadWrite','None');$probe.Dispose()
    }
    . (Join-Path $project 'WireSockCli.ps1')
    function Get-WireSockObservation {
        if($script:observations.Count -eq 0){throw 'Unexpected status read.'}
        return $script:observations.Dequeue()
    }
    function Invoke-WireSock($Arguments){Assert ($Arguments.Count -eq 1 -and $Arguments[0] -eq 'disconnect') 'Unexpected fixture CLI mutation.';$script:disconnects++}
    foreach($case in @('unknown','unowned-transition','disconnected','foreign','connected','transition','still-connected','foreign-after-stop')){
        $script:observations=New-Object 'System.Collections.Generic.Queue[object]';$script:disconnects=0
        $state=switch($case){'unknown'{'Unknown'};'unowned-transition'{'Transitioning'};'disconnected'{'Disconnected'};'foreign'{'Foreign'};'transition'{'Transitioning'};default{'Connected'}}
        $script:observations.Enqueue([pscustomobject]@{state=$state;managed=($case -notin @('unknown','unowned-transition','disconnected','foreign'))})
        if($case -in @('connected','transition','still-connected','foreign-after-stop')){
            $after=switch($case){'still-connected'{'Connected'};'foreign-after-stop'{'Foreign'};default{'Disconnected'}}
            $script:observations.Enqueue([pscustomobject]@{state=$after;managed=($after -eq 'Connected')})
        }
        $failed=$false;try{Disconnect-ManagedWireSock}catch{$failed=$true}
        Assert ($failed -eq ($case -in @('unknown','unowned-transition','still-connected','foreign-after-stop'))) ('Unverified WireSock disconnect was reported as complete: '+$case)
        Assert ($script:disconnects -eq $(if($case -in @('connected','transition','still-connected','foreign-after-stop')){1}else{0})) ('Unexpected WireSock mutation: '+$case)
    }
    Write-Output 'PASS: real routing entry holds setup exclusion for Start/ProxyOnly/Reconnect/RepairVpn/SyncProfile/Prepare; Stop/status/watch and parent-owned offline preparation remain available; exceptions release ownership. No live network or elevation.'
    Write-Output 'PASS: disconnect requires verified ownership and disconnected postcondition; unknown/transitioning/foreign replacement cannot produce false success or a second disconnect.'
}finally{
    $prefix=[IO.Path]::GetFullPath((Join-Path $project 'runtime\lifecycle-tests-'))
    if(-not [IO.Path]::GetFullPath($base).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe lifecycle fixture cleanup.'}
    Remove-Item -LiteralPath $base -Recurse -Force
}
