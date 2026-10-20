function Publish-AgentPackage {
    param([object[]]$Files,[string]$InstallRoot,[scriptblock]$Stop,[scriptblock]$Start,[scriptblock]$Prepare={},[scriptblock]$Restore={})
    $InstallRoot=[IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
    $parent=Split-Path $InstallRoot
    $name=Split-Path $InstallRoot -Leaf
    $id=[guid]::NewGuid().ToString('N')
    $stage=Join-Path $parent ($name+'-stage-'+$id)
    $backup=Join-Path $parent ($name+'-backup-'+$id)
    $failed=Join-Path $parent ($name+'-failed-'+$id)
    $stopped=$false; $promoted=$false; $saved=$false
    # Derived siblings are verified before any move or recursive cleanup.
    foreach ($path in @($stage,$backup,$failed)) {
        if ((Split-Path ([IO.Path]::GetFullPath($path))) -ne $parent) { throw 'Invalid package transaction path.' }
    }
    try {
        New-Item -ItemType Directory -Path $stage | Out-Null
        foreach ($file in $Files) {
            if ($file.Name -match '(^[\\/]|(^|[\\/])\.\.([\\/]|$)|:)' -or -not $file.Name) { throw 'Invalid package filename.' }
            $source=Get-Item -LiteralPath $file.Source -Force
            if ($source.PSIsContainer -or ($source.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid package source.' }
            $destination=Join-Path $stage $file.Name
            New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
            Copy-Item -LiteralPath $source.FullName -Destination $destination
            if ((Get-FileHash -LiteralPath $source.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) { throw 'Staged package hash differs from its source.' }
        }
        # All source reads and hashes finish while the old rescue service is still running.
        $stopped=$true
        & $Stop
        if (Test-Path -LiteralPath $InstallRoot) { Move-Item -LiteralPath $InstallRoot -Destination $backup; $saved=$true }
        Move-Item -LiteralPath $stage -Destination $InstallRoot
        $promoted=$true
        & $Prepare
        & $Start
    } catch {
        $problem=$_
        if ($stopped) {
            try {
                if ($promoted) { & $Stop; Move-Item -LiteralPath $InstallRoot -Destination $failed }
                if ($saved) { Move-Item -LiteralPath $backup -Destination $InstallRoot; $saved=$false }
                & $Restore
                if (Test-Path -LiteralPath $InstallRoot) { & $Start }
            } catch { throw ('Package update failed: '+$problem.Exception.Message+'; rollback failed: '+$_.Exception.Message) }
        }
        throw $problem
    } finally {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    }
    if ($saved) {
        # The package is committed. Cleanup must not make callers roll their
        # selected profile back while the new protected service stays installed.
        try{Remove-Item -LiteralPath $backup -Recurse -Force}catch{Write-Warning 'Protected update succeeded; its old backup could not be removed.'}
    }
}
