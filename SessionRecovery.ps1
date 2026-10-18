function Test-PolicyV2 {
    $agent=Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe'
    return ([version][Diagnostics.FileVersionInfo]::GetVersionInfo($agent).FileVersion -ge [version]'0.2.3.0')
}
function Get-SessionNetworkSignature {
    if(-not(Test-PolicyV2)){
        # Compatibility for an automatic package rollback to the previous agent.
        $config=Read-JsonFile (Join-Path $runtime 'bridge.json')
        $nic=[Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object Name -eq $config.outbounds[0].bind_interface | Select-Object -First 1
        if(-not $nic){return 'missing-interface'}
        $ips=$nic.GetIPProperties()
        $text=$nic.Id+'|'+$nic.OperationalStatus+'|'+(($ips.UnicastAddresses | ForEach-Object Address | Sort-Object) -join ',')+'|'+(($ips.GatewayAddresses | ForEach-Object Address | Sort-Object) -join ',')+'|'+(($ips.DnsAddresses | Sort-Object) -join ',')
        $sha=[Security.Cryptography.SHA256]::Create()
        try{return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))).Replace('-','')}finally{$sha.Dispose()}
    }
    $signature=& (Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe') --network-signature $root
    if($LASTEXITCODE -ne 0 -or ($signature -join '').Trim() -notmatch '^(?:[A-F0-9]{64}|missing-interface:.+)$'){throw 'Could not read the canonical network signature.'}
    return ($signature -join '').Trim()
}
function Test-PolicyProcess {
    $path=Join-Path $runtime 'policy-process.json'
    if(-not(Test-Path -LiteralPath $path)){return $false}
    $identity=Read-JsonFile $path
    $process=Get-Process -Id $identity.Pid -ErrorAction SilentlyContinue
    return [bool]($process -and $process.Path -eq $identity.Path -and [Math]::Abs(($process.StartTime.ToUniversalTime()-[DateTime]::Parse($identity.StartedUtc).ToUniversalTime()).TotalMilliseconds) -lt 100)
}
function Start-PolicyController {
    $policyExe=Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe'
    $process=Start-Process -FilePath $policyExe -ArgumentList @('--policy-worker',('"'+$root+'"')) -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $root 'logs\policy-error.log') -RedirectStandardOutput (Join-Path $root 'logs\policy-output.log')
    Write-JsonFile (Join-Path $runtime 'policy-process.json') @{Pid=$process.Id;StartedUtc=$process.StartTime.ToUniversalTime().ToString('o');Path=$policyExe}
}
function Set-SessionRecovering([string]$ExpectedId,[string]$Reason) {
    $session=Read-JsonFile (Join-Path $runtime 'session.json')
    if($session.id -ne $ExpectedId -or -not $session.running){return $false}
    $session | Add-Member -NotePropertyName recovering -NotePropertyValue $true -Force
    $session | Add-Member -NotePropertyName recoveryReason -NotePropertyValue $Reason -Force
    $session | Add-Member -NotePropertyName bridgeReady -NotePropertyValue $false -Force
    Write-JsonFile (Join-Path $runtime 'session.json') $session
    return $true
}
function Get-RecoveryDelay([int]$Attempts) { return [int](@(2,5,15,30,60)[[Math]::Min([Math]::Max(0,$Attempts),4)]) }
