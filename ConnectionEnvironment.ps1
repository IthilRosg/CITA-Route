# Shared by the GUI discovery command, setup and the routing lifecycle. No keys are returned.
function Get-ConnectionSettings([string]$Root) {
    $path=Join-Path $Root 'data\connection.json'
    if(Test-Path -LiteralPath $path){return Read-JsonFile $path}
    # Migration of this computer only. A clean distribution has no session or selected profile.
    $source=''
    $sessionPath=Join-Path $Root 'runtime\session.json'
    if(Test-Path -LiteralPath $sessionPath){$source=(Read-JsonFile $sessionPath).sourceProfile}
    return [pscustomobject]@{SourceProfile=$source;CliPath='';ExpectedEgressIp=''}
}

# The protected service owns this journal. Only the matching installation may
# control its startup route; a second portable copy cannot adopt it.
function Get-StartupRouteState([string]$Root,[string]$StateFile='') {
    if(-not $StateFile){$StateFile=Join-Path $env:ProgramData 'RoutePilot\startup-state.json'}
    if(-not(Test-Path -LiteralPath $StateFile)){return $null}
    if((Get-Item -LiteralPath $StateFile).Length -gt 16384){throw 'Startup state is invalid.'}
    $state=Read-JsonFile $StateFile
    if($state.Schema -ne 1 -or -not $state.SourceRoot){throw 'Startup state is invalid.'}
    if([IO.Path]::GetFullPath($state.SourceRoot).TrimEnd('\') -ine [IO.Path]::GetFullPath($Root).TrimEnd('\')){return $null}
    if($state.Phase -in @('WaitingForNetwork','Starting','Connected','Stopping','StopFailed','NeedsAttention')){return $state}
    return $null
}

function Find-WireSockCli([string]$Preferred='') {
    $candidates=New-Object 'System.Collections.Generic.List[string]'
    if($Preferred){$candidates.Add($Preferred)}
    foreach($base in @($env:ProgramW6432,$env:ProgramFiles,${env:ProgramFiles(x86)})){
        if($base){$candidates.Add((Join-Path $base 'WireSock Secure Connect\command-line\wiresock-connect-cli.exe'))}
    }
    foreach($key in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')){
        foreach($entry in @(Get-ItemProperty $key -ErrorAction SilentlyContinue | Where-Object {$_.DisplayName -like 'WireSock*'})){
            if($entry.InstallLocation){$candidates.Add((Join-Path $entry.InstallLocation 'command-line\wiresock-connect-cli.exe'))}
        }
    }
    foreach($path in @($candidates | Select-Object -Unique)){
        if(Test-Path -LiteralPath $path -PathType Leaf){
            $full=[IO.Path]::GetFullPath($path)
            if([IO.Path]::GetFileName($full) -ine 'wiresock-connect-cli.exe'){continue}
            return $full
        }
    }
    return ''
}

function Assert-ProtectedWireSockCli([string]$Path) {
    $full=[IO.Path]::GetFullPath($Path)
    $allowed=$false
    foreach($base in @($env:ProgramW6432,$env:ProgramFiles,${env:ProgramFiles(x86)})){
        if($base -and $full.StartsWith(([IO.Path]::GetFullPath($base).TrimEnd('\')+'\'),[StringComparison]::OrdinalIgnoreCase)){$allowed=$true}
    }
    if(-not $allowed){throw 'WireSock CLI must be installed under Program Files before installing the system service.'}
    for($item=Get-Item -LiteralPath $full;$item;$item=$item.Parent){
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'WireSock installation contains a reparse point.'}
        if($item -is [IO.FileInfo]){$item=$item.Directory; if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'WireSock installation contains a reparse point.'}}
    }
    if((Get-AuthenticodeSignature -LiteralPath $full).Status -ne 'Valid'){throw 'WireSock CLI digital signature could not be verified.'}
}

function Get-WireSockProfileNames {
    @(Invoke-WireSock @('list') | ForEach-Object {if($_ -match '^\s*-\s+(.+?)\s*$'){$Matches[1]}} |
        Where-Object {$_ -ne 'SmartRouting-AWG2' -and $_ -notlike 'RoutePilot-Probe-*'} | Sort-Object -Unique)
}

function Get-ProfileEndpoint([string]$Text,[string]$PinnedIp='') {
    if([regex]::Matches($Text,'(?im)^\s*\[Peer\]\s*$').Count -ne 1){throw 'Select a configuration with exactly one peer.'}
    if([regex]::Matches($Text,'(?im)^\s*Endpoint\s*=').Count -ne 1){throw 'Exactly one VPN endpoint is required.'}
    $match=[regex]::Match($Text,'(?im)^\s*Endpoint\s*=\s*([^\s:#]+):(\d{1,5})\s*(?:[#;].*)?$')
    if(-not $match.Success){throw 'A single IPv4 or hostname endpoint is required; IPv6-only VPN servers are not supported yet.'}
    $hostName=$match.Groups[1].Value; $port=[int]$match.Groups[2].Value
    if($port -lt 1 -or $port -gt 65535){throw 'Invalid VPN endpoint port.'}
    $address=$null
    if([Net.IPAddress]::TryParse($hostName,[ref]$address)){
        if($address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork){throw 'An IPv4 VPN endpoint is required.'}
    }else{
        if($hostName -notmatch '^(?=.{1,253}$)([a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?\.)+[a-zA-Z]{2,63}$'){throw 'Invalid VPN endpoint hostname.'}
        if($PinnedIp){$address=[Net.IPAddress]::Parse($PinnedIp)}
        else{$address=@([Net.Dns]::GetHostAddresses($hostName) | Where-Object {$_.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork} | Sort-Object IPAddressToString | Select-Object -First 1)[0]}
        if(-not $address){throw 'The VPN server has no resolvable IPv4 address.'}
    }
    if($address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or [Net.IPAddress]::IsLoopback($address) -or $address.GetAddressBytes()[0] -eq 0 -or $address.GetAddressBytes()[0] -ge 224){throw 'Invalid VPN endpoint address.'}
    [pscustomobject]@{Host=$hostName;Ip=$address.ToString();Port=$port;SourceEndpoint=($hostName+':'+$port)}
}

function Protect-PrivateDirectory([string]$Path,[string]$UserSid='') {
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    if((Get-Item -LiteralPath $Path).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Private configuration directory is a reparse point.'}
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    foreach($sid in @('S-1-5-18','S-1-5-32-544',[Security.Principal.WindowsIdentity]::GetCurrent().User.Value,$UserSid) | Where-Object {$_} | Select-Object -Unique){
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
    }
    if($PSVersionTable.PSEdition -eq 'Core'){[IO.FileSystemAclExtensions]::SetAccessControl((New-Object IO.DirectoryInfo($Path)),$acl)}
    else{[IO.Directory]::SetAccessControl($Path,$acl)}
}

# Explicit Connect may release supported competing connections. Never delete profiles.
function Get-ConflictProfile([string]$Status) {
    $matches=[regex]::Matches($Status,'(?im)^.*?\b(?:profile|\u043f\u0440\u043e\u0444\u0438\u043b\u044f)[ \t]+([^\r\n]+?)[ \t]*\r?$')
    if($matches.Count -eq 1){return $matches[0].Groups[1].Value.Trim()}
    return ''
}
function Get-ConflictVpnusService {
    $service=Get-CimInstance Win32_Service -Filter "Name='VpnusService'" -ErrorAction SilentlyContinue
    if(-not $service){return $null}
    $expected=Join-Path $env:ProgramFiles 'VPNUS\service\vpnus-service.exe'
    if($service.PathName.Trim('"') -ine $expected){throw 'VPNUS service identity is not recognized; automatic release was refused.'}
    return $service
}
function Wait-ConflictWireSockDown {
    $deadline=[DateTime]::UtcNow.AddSeconds(12)
    do {
        Assert-ConnectionOperation
        $status=Read-WireSockStatus
        if((Get-WireSockState $status 'SmartRouting-AWG2') -eq 'Disconnected'){return}
        Start-Sleep -Milliseconds 250
    }while([DateTime]::UtcNow -lt $deadline)
    throw 'The competing WireSock connection did not stop.'
}
function Release-ConnectionConflicts([string]$Root) {
    Assert-NoAgentRun;Assert-ConnectionOperation
    $journalPath=Join-Path $Root 'runtime\connection-handoff.json'
    if(Test-Path -LiteralPath $journalPath){
        $previous=Read-JsonFile $journalPath
        if($previous.operationId -notmatch '^[a-fA-F0-9]{32}$'){throw 'Invalid connection handoff journal.'}
        $currentOperation=$OperationId
        try{$OperationId=$previous.operationId;Restore-ConnectionHandoff $Root}finally{$OperationId=$currentOperation}
    }
    # The existing coordinator is also used by setup and the regular watchdog.
    $leased=$false
    try {
        if(Get-Service RoutePilotRescue -ErrorAction SilentlyContinue){Invoke-AgentCoordinator 'begin-operation';$leased=$true}
        if(Get-Process -Name smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'Stop the current RoutePilot engine before switching connections.'}
        Write-ConnectionProgress 'ReleasingConflicts'
        $status=Read-WireSockStatus;$state=Get-WireSockState $status 'SmartRouting-AWG2'
        if($state -eq 'Unknown'){throw 'WireSock state could not be verified. Automatic release was refused.'}
        $profile=Get-ConflictProfile $status
        if($state -ne 'Disconnected' -and -not $profile){throw 'WireSock profile identity could not be verified.'}
        $vpnus=Get-NetAdapter -Name VPNUS -ErrorAction SilentlyContinue
        # Validate every detected provider before interrupting the first one.
        if($vpnus -and $vpnus.Status -eq 'Up'){
            if(-not(Get-ConflictVpnusService)){throw 'The VPNUS connection has no supported service control.'}
        }
        $journal=@{version=1;operationId=$OperationId;profile=$(if($state -ne 'Disconnected'){$profile}else{''});wireSockReleased=$false;vpnusStopped=$false;updatedUtc=[DateTime]::UtcNow.ToString('o')}
        if($state -eq 'Disconnected' -and (-not $vpnus -or $vpnus.Status -ne 'Up')){return}
        Write-JsonFile $journalPath $journal
        if($state -ne 'Disconnected'){
            # Record intent first: even cancellation after the command needs restoration.
            $journal.wireSockReleased=$true;Write-JsonFile $journalPath $journal
            try{Read-HelperOutput $cli 'disconnect'|Out-Null}catch{
                if((Get-WireSockState (Read-WireSockStatus) 'SmartRouting-AWG2') -ne 'Disconnected'){throw}
            }
            Wait-ConflictWireSockDown
        }
        Assert-ConnectionOperation
        if($vpnus -and $vpnus.Status -eq 'Up'){
            $service=Get-ConflictVpnusService
            if(-not $service){throw 'The VPNUS connection has no supported service control.'}
            $journal.vpnusStopped=$true;Write-JsonFile $journalPath $journal
            Stop-Service -Name VpnusService -ErrorAction Stop
            $deadline=[DateTime]::UtcNow.AddSeconds(12);$down=0
            do {
                Assert-ConnectionOperation
                $adapter=Get-NetAdapter -Name VPNUS -ErrorAction SilentlyContinue
                $current=Get-Service VpnusService -ErrorAction Stop
                if(((-not $adapter) -or $adapter.Status -ne 'Up') -and $current.Status -eq 'Stopped'){$down++}else{$down=0}
                if($down -ge 3){break}
                Start-Sleep -Milliseconds 300
            }while([DateTime]::UtcNow -lt $deadline)
            if($down -lt 3){throw 'VPNUS reconnected or failed to stop. Connection switching was cancelled.'}
        }
    }finally{if($leased){Invoke-AgentCoordinator 'end-operation'}}
}
function Complete-ConnectionHandoff([string]$Root) {
    $path=Join-Path $Root 'runtime\connection-handoff.json'
    if(-not(Test-Path -LiteralPath $path)){return}
    $journal=Read-JsonFile $path
    if($journal.operationId -ne $OperationId){throw 'Connection handoff generation changed.'}
    Remove-Item -LiteralPath $path -Force
}
function Restore-ConnectionHandoff([string]$Root) {
    $path=Join-Path $Root 'runtime\connection-handoff.json'
    if(-not(Test-Path -LiteralPath $path)){return}
    $journal=Read-JsonFile $path
    if($journal.version -ne 1 -or $journal.operationId -ne $OperationId){throw 'Connection handoff generation changed; restore was refused.'}
    Assert-NoAgentRun
    $leased=$false
    try {
        if(Get-Service RoutePilotRescue -ErrorAction SilentlyContinue){Invoke-AgentCoordinator 'begin-operation';$leased=$true}
        if(Get-Process -Name smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'A newer route is active; handoff restore was refused.'}
        $failures=New-Object 'System.Collections.Generic.List[string]'
        if($journal.wireSockReleased -and $journal.profile){
            $status=Read-WireSockStatus;$state=Get-WireSockState $status 'SmartRouting-AWG2'
            if($state -ne 'Disconnected' -and (Get-ConflictProfile $status) -ne $journal.profile){throw 'WireSock changed to another profile; it was not overwritten during restore.'}
            if($state -eq 'Disconnected'){
                try {
                    $profiles=@(Get-WireSockProfileNames)+@('SmartRouting-AWG2')
                    if($profiles -notcontains $journal.profile){throw 'The previous WireSock profile no longer exists.'}
                    # Quote for CreateProcess, without a shell; keep profile text one argument.
                    $argument='"'+([regex]::Replace([regex]::Replace([string]$journal.profile,'(\\*)"','$1$1\"'),'(\\+)$','$1$1'))+'"'
                    Read-HelperOutput $cli ('connect '+$argument+' -exit')|Out-Null
                }catch{$failures.Add($_.Exception.Message)}
            }
        }
        if($journal.vpnusStopped){
            try{$null=Get-ConflictVpnusService;Start-Service -Name VpnusService -ErrorAction Stop}catch{$failures.Add($_.Exception.Message)}
        }
        if($failures.Count){throw ('Previous VPN restore needs attention: '+($failures -join '; '))}
        Remove-Item -LiteralPath $path -Force
    }finally{if($leased){Invoke-AgentCoordinator 'end-operation'}}
}
