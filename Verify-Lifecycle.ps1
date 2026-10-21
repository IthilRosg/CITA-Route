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
function Get-StartupRouteState { $null }
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
    . (Join-Path $project 'FileStorage.ps1')
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $project 'SmartRouting.ps1'),[ref]$tokens,[ref]$errors)
    foreach($name in @('Test-HelperAdapterCollision','Test-HelperTunReady','Start-Helper')){
        $node=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
        if(-not $node){throw ('Missing lifecycle function: '+$name)}
        . ([scriptblock]::Create($node.Extent.Text))
    }
    $collision='configure tun interface: (create adapter: Cannot create a file when that file already exists. | open existing adapter: Element not found.)'
    Assert (Test-HelperAdapterCollision $collision) 'Exact observed Wintun collision was not recognized.'
    foreach($message in @('Access denied','Cannot create a file when that file already exists.','configure tun interface: permission denied')){Assert (-not(Test-HelperAdapterCollision $message)) 'An unrelated error authorized a tunnel restart.'}
    function Assert-ConnectionOperation {}
    function Start-Sleep {param($Milliseconds)}
    function Start-Process {
        param($FilePath,$ArgumentList,$WorkingDirectory,$WindowStyle,[switch]$PassThru,$RedirectStandardError,$RedirectStandardOutput)
        $script:starts++
        [IO.File]::WriteAllText($RedirectStandardError,$(if($script:case -in @('collision','cancel-retry','always-collision')){$script:collision}else{'Access denied'}))
        $script:fake=[pscustomobject]@{Id=5000+$script:starts;HasExited=$false;Ticks=0}
        $script:fake|Add-Member ScriptMethod Refresh {
            $this.Ticks++
            if($script:case -ne 'late-ready' -and ($script:starts -eq 1 -or $script:case -eq 'always-collision')){$this.HasExited=$this.Ticks -ge 3}
            if($this.HasExited -and $script:case -eq 'cancel-retry'){[IO.File]::WriteAllText((Join-Path $script:runtime 'stop.request'),'stop')}
        }
        $script:fake|Add-Member ScriptMethod WaitForExit {param($Timeout) $true}
        return $script:fake
    }
    function Get-NetTCPConnection {param($State,$ErrorAction) [pscustomobject]@{OwningProcess=$script:fake.Id;LocalPort=20830}}
    function Get-NetAdapter {
        param($Name,[switch]$IncludeHidden,$ErrorAction)
        if($script:fake -and -not $script:fake.HasExited -and ($script:case -eq 'late-ready' -or $script:starts -gt 1) -and $script:fake.Ticks -ge 3){[pscustomobject]@{Status='Up';ifIndex=77}}
    }
    function Get-NetIPAddress {param($InterfaceIndex,$AddressFamily,$ErrorAction) [pscustomobject]@{IPAddress='172.30.125.1';AddressState='Preferred'}}
    function Stop-Process {param([Parameter(ValueFromPipeline)]$InputObject,[switch]$Force) process {throw 'An already exited fixture must not be killed or another process stopped.'}}
    foreach($script:case in @('late-ready','unknown-error','collision','cancel-retry','always-collision')){
        $root=Join-Path $base ('helper-'+$script:case);$runtime=Join-Path $root 'runtime'
        New-Item -ItemType Directory -Path $runtime,(Join-Path $root 'logs') -Force|Out-Null
        $routerExe=Join-Path $runtime 'smart-router.exe';$script:fake=$null;$script:starts=0
        $config=Join-Path $runtime 'router.json'
        Write-JsonFile $config @{inbounds=@(@{type='mixed';listen='127.0.0.1';listen_port=20830},@{type='tun';interface_name='SmartRouting';address=@('172.30.125.1/30')})}
        $script:collision=$collision
        $failed=$false;$result=$null;try{$result=Start-Helper $routerExe $config}catch{$failed=$true}
        $expectedStarts=if($script:case -in @('collision','always-collision')){2}else{1}
        Assert ($script:starts -eq $expectedStarts) ('Unexpected helper restart count: '+$script:case)
        Assert ($failed -eq ($script:case -in @('unknown-error','cancel-retry','always-collision'))) ('False helper readiness: '+$script:case)
        if(-not $failed){Assert ($result.Ticks -ge 3 -and -not $result.HasExited) 'An early local listener was treated as a fully ready tunnel.'}
    }
    Write-Output 'PASS: actual helper readiness requires its configured tunnel address; only the observed exited-router adapter collision permits one retry; unrelated failures and Stop cannot restart it.'
    Write-Output 'PASS: real routing entry holds setup exclusion for Start/ProxyOnly/Reconnect/RepairVpn/SyncProfile/Prepare; Stop/status/watch and parent-owned offline preparation remain available; exceptions release ownership. No live network or elevation.'
    Write-Output 'PASS: disconnect requires verified ownership and disconnected postcondition; unknown/transitioning/foreign replacement cannot produce false success or a second disconnect.'
}finally{
    $prefix=[IO.Path]::GetFullPath((Join-Path $project 'runtime\lifecycle-tests-'))
    if(-not [IO.Path]::GetFullPath($base).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe lifecycle fixture cleanup.'}
    Remove-Item -LiteralPath $base -Recurse -Force
}
