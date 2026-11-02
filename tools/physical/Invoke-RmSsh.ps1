[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Command,
    [string[]]$Address=@('192.168.1.124','26.68.227.253'),
    [string]$User='citastation',
    [string]$KeyPath=(Join-Path $env:USERPROFILE '.ssh\cita_test_station_ed25519'),
    [string]$KnownHostsPath=(Join-Path $env:USERPROFILE '.ssh\cita_test_station_known_hosts'),
    [int]$Attempts=5
)
$ErrorActionPreference='Stop'
if(-not(Test-Path -LiteralPath $KeyPath -PathType Leaf)){throw 'SSH key is missing.'}
if(-not(Test-Path -LiteralPath $KnownHostsPath -PathType Leaf)){throw 'Known-hosts file is missing.'}
$common=@('-i',$KeyPath,'-o',('UserKnownHostsFile='+$KnownHostsPath),'-o','HostKeyAlias=26.68.227.253','-o','StrictHostKeyChecking=yes','-o','ServerAliveInterval=5','-o','ServerAliveCountMax=2','-o','ConnectionAttempts=1','-o','ConnectTimeout=5')
$last=''
for($attempt=1;$attempt -le $Attempts;$attempt++){
    foreach($targetHost in $Address){
        $args=[Collections.Generic.List[string]]::new();$common|ForEach-Object{$args.Add($_)};$args.Add(($User+'@'+$targetHost));$args.Add($Command)
        $output=& ssh.exe @args 2>&1
        if($LASTEXITCODE -eq 0){$output;exit 0}
        $last=($output|Out-String).Trim()
    }
    if($attempt -lt $Attempts){Start-Sleep -Seconds ([Math]::Min(2*$attempt,8))}
}
throw ('RM SSH unavailable after '+$Attempts+' attempts. Last error: '+$last)
