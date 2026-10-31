param([Parameter(Mandatory)][string]$RunRoot)
$ErrorActionPreference='Stop';$ctx=$null;$locks=[Collections.Generic.List[object]]::new();$rescueProcesses=[Collections.Generic.List[object]]::new();$image=$null;$serviceProcess=$null
function Write-ControllerRecord([string]$Name,$Value){
    $path=Join-Path $ctx.Root $Name;$temp=$path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try{
        [IO.File]::WriteAllText($temp,($Value|ConvertTo-Json -Depth 6 -Compress),[Text.UTF8Encoding]::new($false))
        $acl=[Security.AccessControl.FileSecurity]::new();$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'));$acl.SetAccessRuleProtection($true,$false)
        foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','Allow'))};Set-Acl -LiteralPath $temp -AclObject $acl
        if([IO.File]::Exists($path)){[IO.File]::Replace($temp,$path,[NullString]::Value)}else{[IO.File]::Move($temp,$path)}
    }finally{if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}}
}
function Call-CycleAgent([string]$Command,[string]$RejectedConnectionId=''){
    $s=Get-NativeUpgradeServiceSnapshot $ctx.Lease
    if($s.State -cne 'Running' -or $s.ProcessId -ne $ctx.ServicePid -or $serviceProcess.HasExited){throw 'Cycle service identity changed.'}
    $request=@{Command=$Command}
    if($Command -ceq 'native-start'){$request.RunId=$ctx.Plan.InitialOperation;$request.ConnectionId=$ctx.Plan.ConnectionId}
    elseif($Command -ceq 'native-stop-session'){
        if($RejectedConnectionId){if($RejectedConnectionId -cnotmatch '^[a-f0-9]{32}$' -or $RejectedConnectionId -ceq $ctx.Plan.ConnectionId){throw 'Wrong-session fixture differs.'};$request.ConnectionId=$RejectedConnectionId}
        else{$request.ConnectionId=$ctx.Plan.ConnectionId}
    }
    elseif($Command -cin @('native-trace-on','native-trace-read','native-trace-off','native-resources')){$request.RunId=$script:traceOperation}
    elseif($Command -cnotin @('native-status','native-capabilities')){throw 'Cycle command scope refused.'}
    $reply=[Cita.NativeUpgradePipe]::Exchange(($request|ConvertTo-Json -Compress),$ctx.ServicePid)|ConvertFrom-Json
    if($serviceProcess.HasExited -or (Get-NativeUpgradeServiceSnapshot $ctx.Lease).ProcessId -ne $ctx.ServicePid){throw 'Cycle service changed during RPC.'}
    return $reply
}
function Capture-CycleState($Reply){
    if($Reply.Success -isnot [bool] -or !$Reply.Success -or !$Reply.Native){throw 'Cycle native state unavailable.'}
    $n=$Reply.Native
    Write-ControllerRecord 'state.json' @{Run=$ctx.Plan.Run;Phase=$n.phase;Desired=$n.desired;ConnectionId=$n.connectionId;Generation=$n.generation;ErrorCode=$n.errorCode;ErrorStage=$n.errorStage;CoreState=$n.runtime.coreState;CorePid=$n.runtime.corePid;CoreStartupIssue=$n.runtime.coreStartupIssue;AutomaticRouting=$n.runtime.automaticRouting;AutomaticFailureStage=$n.runtime.automaticFailureStage;AutomaticFailureType=$n.runtime.automaticFailureType;Repairs=$n.runtime.repairs;NetworkEvents=$n.runtime.networkEvents;Utc=[datetime]::UtcNow.ToString('O')}
    return $n
}
try{
    $root=[IO.Path]::GetFullPath($RunRoot).TrimEnd('\');$agent=Join-Path $env:ProgramFiles 'RoutePilot\Agent';$leaf=[IO.Path]::GetFileName($root)
    if([IO.Path]::GetDirectoryName($root) -ine $agent -or $leaf -cnotmatch '^native-cycle-[a-f0-9]{32}$' -or $PSCommandPath -ine (Join-Path $root 'Controller.ps1')){throw 'Cycle controller location refused.'}
    . "$PSScriptRoot\NativeUpgradeTransaction.ps1";. "$PSScriptRoot\NativeUpgradeService.ps1"
    $plan=[IO.File]::ReadAllText((Join-Path $root 'plan.json'))|ConvertFrom-Json
    $ctx=[pscustomobject]@{Root=$root;Plan=$plan;Lease=$null;ServicePid=0}
    if($plan.Run -cne $leaf.Substring(13) -or $plan.ConnectionId -cnotmatch '^[a-f0-9]{32}$'){throw 'Cycle plan identity differs.'}
    $self=[Diagnostics.Process]::GetCurrentProcess()
    try{Write-ControllerRecord 'controller.json' @{Run=$plan.Run;Pid=$PID;StartedUtcTicks=$self.StartTime.ToUniversalTime().Ticks;Image=$self.MainModule.FileName}}finally{$self.Dispose()}
    if((Get-CimInstance Win32_ComputerSystemProduct).UUID -cne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong cycle station.'}
    $config=[IO.File]::ReadAllText((Join-Path $agent 'agent-config.json'))|ConvertFrom-Json
    if($config.UserSid -cne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value){throw 'Cycle owner differs.'}
    $lockNames=@('connection-operation.lock','setup.lock','upgrade.lock','network-control.lock')
    # The real application deliberately refuses startup while installation is
    # locked. A GUI test must not impersonate an installation in progress.
    if(!$plan.GuiMode){$lockNames+='installation-maintenance.lock'}
    foreach($name in $lockNames){$path=Join-Path (Join-Path $config.AppRoot 'runtime') $name;Assert-NativeUpgradePath $path;$locks.Add([IO.File]::Open($path,'OpenOrCreate','ReadWrite','None'))}
    $ctx.Lease=Open-NativeUpgradeLease $agent
    $image=[IO.File]::Open((Join-Path $agent 'RoutePilot.Agent.exe'),'Open','Read','Read')
    if((Get-NativeUpgradeHash (Join-Path $agent 'RoutePilot.Agent.exe')) -cne $plan.AgentSha){throw 'Cycle agent image differs.'}
    $s=Get-NativeUpgradeServiceSnapshot $ctx.Lease;$ctx.ServicePid=$s.ProcessId
    $serviceProcess=Get-Process -Id $s.ProcessId -ErrorAction Stop;$null=$serviceProcess.Handle
    if($s.State -cne 'Running' -or $serviceProcess.HasExited -or $serviceProcess.Path -ine (Join-Path $agent 'RoutePilot.Agent.exe')){throw 'Cycle service process differs.'}
    Initialize-NativeUpgradePipe
    $caps=Call-CycleAgent 'native-capabilities'
    if(!$caps.Success -or $caps.Native.connectionProtocol -ne 1){throw 'Cycle connection protocol unavailable.'}
    $initial=Capture-CycleState (Call-CycleAgent 'native-status')
    if($initial.desired -or $initial.phase -cne 'Disconnected' -or $initial.operation -cne $plan.InitialOperation -or $initial.generation -ne $plan.InitialGeneration){throw 'Cycle initial state changed.'}
    foreach($role in @('primary','backup')){
        $armed=[IO.File]::ReadAllText((Join-Path $root ('rescue-armed-'+$role+'.json')))|ConvertFrom-Json
        if($armed.Run -cne $plan.Run -or $armed.ConnectionId -cne $plan.ConnectionId -or [DateTimeOffset]::UtcNow -ge [DateTimeOffset]::Parse($plan.DeadlineUtc)){throw 'Cycle rescue admission expired.'}
        $rescue=Get-Process -Id $armed.Pid -ErrorAction Stop;$rescueProcesses.Add($rescue);$null=$rescue.Handle
        if($rescue.HasExited -or $rescue.StartTime.ToUniversalTime().Ticks -ne $armed.StartedUtcTicks -or $rescue.Path -ine (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe')){throw 'Cycle rescue process disappeared.'}
    }
    if($rescueProcesses[0].Id -eq $rescueProcesses[1].Id -or @($rescueProcesses|Where-Object {$_.HasExited}).Count){throw 'Cycle rescues are not independently live.'}
    if($plan.GuiMode){
        . (Join-Path $root 'Gui.ps1');Invoke-GuiCycle $ctx $rescueProcesses
        Write-ControllerRecord 'cleanup-request.json' @{Run=$plan.Run};return
    }
    $reply=Call-CycleAgent 'native-start'
    if($reply.Success -isnot [bool] -or !$reply.Success){throw 'Cycle native start refused.'}
    $state=Capture-CycleState $reply
    if($state.connectionId -cne $plan.ConnectionId){throw 'Cycle connection identity changed.'}
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while($timer.Elapsed.TotalSeconds -lt 45 -and $state.phase -cne 'Connected'){
        if(@($rescueProcesses|Where-Object {$_.HasExited}).Count){throw 'A cycle rescue was lost.'}
        if($state.connectionId -cne $plan.ConnectionId -or !$state.desired -or $state.phase -ceq 'RecoveryFailed'){throw 'Cycle connection failed.'}
        Start-Sleep -Milliseconds 300;$state=Capture-CycleState (Call-CycleAgent 'native-status')
    }
    if($state.phase -cne 'Connected'){throw 'Cycle connection deadline exceeded.'}
    $connectMs=$timer.ElapsedMilliseconds
    $generation=$state.generation
    $wrongStop=Call-CycleAgent 'native-stop-session' ([guid]::NewGuid().ToString('N'))
    if($wrongStop.Success -isnot [bool] -or $wrongStop.Success){throw 'Wrong-session stop was accepted.'}
    $state=Capture-CycleState (Call-CycleAgent 'native-status')
    if($state.phase -cne 'Connected' -or !$state.desired -or $state.connectionId -cne $plan.ConnectionId -or $state.generation -ne $generation){throw 'Wrong-session stop changed the live session.'}
    $state=Capture-CycleState (Call-CycleAgent 'native-start')
    if($state.phase -cne 'Connected' -or !$state.desired -or $state.connectionId -cne $plan.ConnectionId -or $state.generation -ne $generation){throw 'Same-session replay changed the live session.'}
    Add-Type -AssemblyName System.Net.Http
    $coreFault=$null
    if($plan.CoreCrash){
        $oldPid=[int]$state.runtime.corePid;$corePath=Join-Path $agent 'native\native-core.exe'
        $old=Get-Process -Id $oldPid -ErrorAction Stop
        try{
            $null=$old.Handle;$metadata=Get-CimInstance Win32_Process -Filter ('ProcessId='+$oldPid)
            if($old.HasExited -or $old.Path -ine $corePath -or $metadata.ParentProcessId -ne $ctx.ServicePid -or (Get-FileHash -LiteralPath $corePath).Hash -cne $plan.CoreSha){throw 'Fault target is not the owned pinned core.'}
            $oldTicks=$old.StartTime.ToUniversalTime().Ticks
            Write-ControllerRecord 'fault-checkpoint.json' @{Run=$plan.Run;CorePid=$oldPid;StartedUtcTicks=$oldTicks;CoreSha=$plan.CoreSha;ConnectionId=$plan.ConnectionId}
            $recovery=[Diagnostics.Stopwatch]::StartNew();$old.Kill();if(!$old.WaitForExit(5000)){throw 'Fault-target exit unconfirmed.'}
        }finally{$old.Dispose()}
        $leak='Unavailable';$probeHandler=[Net.Http.HttpClientHandler]::new();$probeHandler.UseProxy=$false;$probeHandler.AllowAutoRedirect=$false
        $probe=[Net.Http.HttpClient]::new($probeHandler);$probe.Timeout=[TimeSpan]::FromSeconds(2);$probe.MaxResponseContentBufferSize=256
        try{
            try{
                $text=$probe.GetStringAsync('https://api.ipify.org/').GetAwaiter().GetResult().Trim();$ip=$null
                if($text.Length -gt 64 -or ![Net.IPAddress]::TryParse($text,[ref]$ip)){throw 'Invalid exit response.'}
                $digest=[Security.Cryptography.SHA256]::Create();try{$hash=([BitConverter]::ToString($digest.ComputeHash([Text.Encoding]::UTF8.GetBytes($ip.ToString())))).Replace('-','')}finally{$digest.Dispose()}
                $leak=if($hash -ieq $plan.ExpectedExitHash){'VpnVerified'}else{'UnexpectedExit'}
            }catch{$leak='Unavailable'}
        }finally{$probe.Dispose();$probeHandler.Dispose()}
        if($leak -ceq 'UnexpectedExit'){throw 'Protected traffic used an unexpected exit during recovery.'}
        $phases=[Collections.Generic.List[string]]::new()
        do{
            if(@($rescueProcesses|Where-Object {$_.HasExited}).Count){throw 'Fault-test rescue disappeared.'}
            $state=Capture-CycleState (Call-CycleAgent 'native-status')
            if($state.connectionId -cne $plan.ConnectionId -or !$state.desired -or $state.phase -ceq 'RecoveryFailed'){throw 'Fault recovery lost the desired session.'}
            $phase=[string]$state.runtime.coreState;if(!$phases.Contains($phase)){$phases.Add($phase)}
            if($state.runtime.automaticRouting -ceq 'Unavailable'){throw 'Automatic routing did not recover.'}
            if($state.phase -ceq 'Connected' -and $phase -ceq 'Active' -and [int]$state.runtime.corePid -ne $oldPid -and $state.runtime.automaticRouting -cin @('Observing','CheckingDirect','CheckingZapret','Throttled','VpnFallback') -and [int]$state.runtime.networkEvents.coreResets -gt 0){break}
            if($recovery.Elapsed.TotalSeconds -gt 30){throw 'Core recovery deadline exceeded.'};Start-Sleep -Milliseconds 200
        }while($true)
        $coreFault=@{OldPid=$oldPid;NewPid=[int]$state.runtime.corePid;RecoveryMs=$recovery.ElapsedMilliseconds;States=$phases;DuringRecoveryExit=$leak;SameConnection=$true;DesiredPreserved=$true;Generation=$state.generation}
    }
    $handler=[Net.Http.HttpClientHandler]::new();$handler.UseProxy=$false;$handler.UseCookies=$false;$handler.AllowAutoRedirect=$false
    $http=[Net.Http.HttpClient]::new($handler);$http.Timeout=[TimeSpan]::FromSeconds(6);$http.MaxResponseContentBufferSize=256
    $probes=[Collections.Generic.List[object]]::new()
    $exitMatch=$null;$exitError=$null
    $urls=@('https://example.com/','https://www.youtube.com/generate_204')
    $samples=[Collections.Generic.List[object]]::new();$trace=@();$script:traceOperation=$state.operation
    if($plan.RoutingSample){
        $urls+=@('https://discord.com/','https://www.deepl.com/','https://www.ryuugames.com/','https://srnky.com/')
        $null=Call-CycleAgent 'native-trace-on'
    }
    $rounds=if($plan.RoutingSample){2}else{1}
    try{for($round=1;$round -le $rounds;$round++){
        if($round -gt 1 -and $plan.RoutingSample){
            # A pool would keep round-one VPN streams alive. A fresh handler
            # is required to measure the route selected for NEW connections.
            $http.Dispose();$handler.Dispose()
            $handler=[Net.Http.HttpClientHandler]::new();$handler.UseProxy=$false;$handler.UseCookies=$false;$handler.AllowAutoRedirect=$false
            $http=[Net.Http.HttpClient]::new($handler);$http.Timeout=[TimeSpan]::FromSeconds(6);$http.MaxResponseContentBufferSize=256
        }
    foreach($url in $urls){
        if($timer.Elapsed.TotalSeconds -gt 85){break}
        if(@($rescueProcesses|Where-Object {$_.HasExited}).Count){throw 'A cycle rescue was lost.'}
        $clock=[Diagnostics.Stopwatch]::StartNew();$response=$null
        try{$request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Head,$url);try{$response=$http.SendAsync($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult();$probes.Add(@{Host=([uri]$url).Host;Round=$round;Status=[int]$response.StatusCode;Ms=$clock.ElapsedMilliseconds})}finally{$request.Dispose()}}catch{$probes.Add(@{Host=([uri]$url).Host;Round=$round;ErrorType=$_.Exception.GetType().Name;Ms=$clock.ElapsedMilliseconds})}finally{if($response){$response.Dispose()}}
    }
    if($plan.RoutingSample){
        for($pause=0;$pause -lt 25 -and $timer.Elapsed.TotalSeconds -lt 85;$pause++){
            if(@($rescueProcesses|Where-Object {$_.HasExited}).Count){throw 'A cycle rescue was lost.'};Start-Sleep -Milliseconds 1000
            $state=Capture-CycleState (Call-CycleAgent 'native-status')
            if($state.connectionId -cne $plan.ConnectionId -or !$state.desired){throw 'Routing sample lost its session.'}
            $script:traceOperation=$state.operation;$resources=Call-CycleAgent 'native-resources'
            $r=$state.runtime;$samples.Add(@{Ms=$timer.ElapsedMilliseconds;CoreState=$r.coreState;AutomaticRouting=$r.automaticRouting;ZapretState=$r.zapretState;ZapretReason=$r.zapretProbeReason;ZapretFailure=$r.zapretFailure;ZapretConnector=$r.zapretConnector;NetworkEvents=$r.networkEvents;Resources=$resources.Native;DirectReason=$r.lastPathProbeReason;Observed=$r.observedDestinations;Identified=$r.identifiedDestinations;Checks=$r.calibrationChecks;Passed=$r.calibrationPassed;Failed=$r.calibrationFailed;Errors=$r.calibrationErrors;Rules=$r.calibratedDomains;DirectDials=$r.policyDirectDials})
        }
    }
    }
    try{
        $exitText=$http.GetStringAsync('https://api.ipify.org/').GetAwaiter().GetResult().Trim();$ip=$null
        if($exitText.Length -gt 64 -or ![Net.IPAddress]::TryParse($exitText,[ref]$ip)){throw 'Exit response is not an address.'}
        $sha=[Security.Cryptography.SHA256]::Create();try{$exitHash=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($ip.ToString())))).Replace('-','')}finally{$sha.Dispose()}
        $exitMatch=($exitHash -ieq $plan.ExpectedExitHash)
    }catch{$exitError=$_.Exception.GetType().Name}
    }finally{$http.Dispose();$handler.Dispose()}
    $state=Capture-CycleState (Call-CycleAgent 'native-status')
    if($plan.RoutingSample){$script:traceOperation=$state.operation;$traceReply=Call-CycleAgent 'native-trace-read';$hosts=@($urls|ForEach-Object {([uri]$_).Host});$trace=@($traceReply.Native.traceEntries|Where-Object {$_.domain -cin $hosts}|Select-Object domain,application,event,unixMilliseconds)}
    $r=$state.runtime
    Write-ControllerRecord 'connected-result.json' @{Run=$plan.Run;ConnectionId=$plan.ConnectionId;Generation=$state.generation;CoreFault=$coreFault;Probes=$probes;Samples=$samples;Trace=$trace;ConnectMs=$connectMs;TotalMs=$timer.ElapsedMilliseconds;ExpectedExitMatch=$exitMatch;ExitProbeError=$exitError;WrongSessionStopRejected=$true;SameSessionStartIdempotent=$true;Routing=@{CoreState=$r.coreState;AutomaticRouting=$r.automaticRouting;ZapretState=$r.zapretState;ZapretIssue=$state.zapretIssue}}
    $stop=Call-CycleAgent 'native-stop-session';if(!$stop.Success){throw 'Cycle exact stop refused.'}
    Write-ControllerRecord 'cleanup-request.json' @{Run=$plan.Run}
}catch{
    if($ctx){if(!$ctx.Plan.GuiMode){try{$null=Call-CycleAgent 'native-stop-session'}catch{}};try{Write-ControllerRecord 'controller-failure.json' @{Run=$ctx.Plan.Run;ErrorType=$_.Exception.GetType().Name;Script=[IO.Path]::GetFileName($_.InvocationInfo.ScriptName);Line=$_.InvocationInfo.ScriptLineNumber};Write-ControllerRecord 'cleanup-request.json' @{Run=$ctx.Plan.Run}}catch{}}
    exit 1
}finally{foreach($process in $rescueProcesses){$process.Dispose()};if($serviceProcess){$serviceProcess.Dispose()};if($image){$image.Dispose()};if($ctx -and $ctx.Lease){$ctx.Lease.Handle.Dispose()};foreach($file in $locks){$file.Dispose()}}
