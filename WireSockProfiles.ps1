# Profile lifecycle is kept separate so retries and rollback can be tested without a VPN connection.
function Get-WireSockConfigCanonical([string]$Path) {
    $section = ''
    $entries = New-Object 'System.Collections.Generic.List[string]'
    foreach ($rawLine in [IO.File]::ReadAllLines($Path)) {
        $line = ($rawLine -replace '^\s*#@ws:\s*','').Trim()
        if (-not $line -or $line.StartsWith('#') -or $line.StartsWith(';')) { continue }
        if ($line -match '^\[([^\]]+)\]$') {
            $section = $Matches[1].Trim().ToLowerInvariant()
            continue
        }
        if ($line -notmatch '^([^=]+)=(.*)$') { continue }
        $key = $Matches[1].Trim().ToLowerInvariant()
        $value = $Matches[2].Trim()
        if (-not $value) { continue }
        # WireSock rewrites separators and the compatibility comment prefix on export.
        $value = $value -replace '\s*,\s*',','
        if ($key -in @('allowedapps','disallowedapps')) { $value = $value.ToLowerInvariant() }
        $entries.Add($section + '/' + $key + '=' + $value)
    }
    return (($entries | Sort-Object -CaseSensitive) -join "`n")
}

function Sync-WireSockProfile([string]$Name,[string]$DesiredPath,[string]$BackupDirectory) {
    if ($Name -cne 'SmartRouting-AWG2') { throw 'Only the managed SmartRouting-AWG2 profile can be synchronized.' }
    $profileList = @(Invoke-WireSock @('list') | ForEach-Object {
        if ($_ -match '^\s*-\s+(.+?)\s*$') { $Matches[1] }
    })
    if ($profileList -notcontains $Name) {
        Invoke-WireSock @('import',$DesiredPath) | Out-Null
        Write-Host "Created managed WireSock profile: $Name."
        return
    }

    $temporaryExport = Join-Path ([IO.Path]::GetDirectoryName($DesiredPath)) ('profile-check-' + [guid]::NewGuid().ToString('N') + '.conf')
    try {
        Invoke-WireSock @('export',$Name,$temporaryExport) | Out-Null
        $current = Get-WireSockConfigCanonical $temporaryExport
        $desired = Get-WireSockConfigCanonical $DesiredPath
        if ($current -ceq $desired) {
            Write-Host "Reusing existing WireSock profile: $Name."
            return
        }

        # Retain the exact filename: WireSock derives the profile name from the imported filename.
        $backupFolder = Join-Path $BackupDirectory ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $backupFolder -Force | Out-Null
        $backupPath = Join-Path $backupFolder ($Name + '.conf')
        Copy-Item -LiteralPath $temporaryExport -Destination $backupPath
        $statusText = (Invoke-WireSock @('status')) -join "`n"
        $profileState=Get-WireSockState $statusText $Name
        if($profileState -notin @('Connected','Disconnected')){throw 'Another WireSock profile or unverified state prevents profile synchronization.'}
        if ($profileState -eq 'Connected') {
            Invoke-WireSock @('disconnect') | Out-Null
        }
        Invoke-WireSock @('delete',$Name) | Out-Null
        try {
            Invoke-WireSock @('import',$DesiredPath) | Out-Null
        } catch {
            $importFailure = $_
            try { Invoke-WireSock @('import',$backupPath) | Out-Null }
            catch { throw "Profile update failed and automatic restore failed. Backup retained at: $backupPath" }
            throw $importFailure
        }
        Write-Host "Updated managed WireSock profile: $Name. Previous configuration backed up."
    } finally {
        Remove-Item -LiteralPath $temporaryExport -Force -ErrorAction SilentlyContinue
    }
}
