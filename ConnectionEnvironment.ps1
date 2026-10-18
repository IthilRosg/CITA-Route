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
