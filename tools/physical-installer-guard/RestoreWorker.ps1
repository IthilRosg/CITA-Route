# Library for the private physical-installation supervisor. The caller must
# authenticate the machine, protected run directory, exact client root and SID
# before invoking this function. This file is not a standalone elevated entry.
function Invoke-GuardedPublicRestore([string]$RootPath,[string]$UserSid,[string]$RunRoot,[string]$TransactionScript,[string]$ExpectedManifestSha256,[string]$PreviousManifestSha256,[string]$PreviousJournalId,[string]$PreviousStateSha256,[string]$ExpectedTransactionId) {
    $ErrorActionPreference='Stop'
    $RootPath=[IO.Path]::GetFullPath($RootPath).TrimEnd('\')
    $RunRoot=[IO.Path]::GetFullPath($RunRoot).TrimEnd('\')
    if((Split-Path $RootPath -Leaf) -ne 'client' -or $UserSid -notmatch '^S-1-5-21-(\d+-){3}\d+$' -or $ExpectedManifestSha256 -notmatch '^[a-fA-F0-9]{64}$' -or $PreviousManifestSha256 -notmatch '^[a-fA-F0-9]{64}$' -or $PreviousStateSha256 -notmatch '^[a-fA-F0-9]{64}$' -or $PreviousJournalId -cnotmatch '^[a-f0-9]{32}$' -or $ExpectedTransactionId -cnotmatch '^[a-f0-9]{32}$' -or $ExpectedTransactionId -ceq $PreviousJournalId){throw 'Installation recovery identity refused.'}
    foreach($path in @($RootPath,$RunRoot,$TransactionScript)){
        for($part=[IO.Path]::GetFullPath($path);$part;$part=Split-Path $part){
            if((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Installation recovery link refused.'}
        }
    }
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($TransactionScript)) -ine $RunRoot){throw 'Recovery script must be in the protected run directory.'}
    $scriptStream=[IO.File]::Open($TransactionScript,'Open','Read','Read')
    $owned=New-Object 'System.Collections.Generic.List[System.IDisposable]'
    try {
        $hash=[Security.Cryptography.SHA256]::Create()
        try{$digest=([BitConverter]::ToString($hash.ComputeHash($scriptStream))).Replace('-','')}finally{$hash.Dispose()}
        if($digest -ne '5278C3D4126FA93C484EFB081F4F6577309F79E39DE14456A88F971DF702529D'){throw 'Recovery script digest differs.'}
        $scriptStream.Position=0;$reader=[IO.StreamReader]::new($scriptStream,[Text.Encoding]::UTF8,$true,4096,$true)
        try{$scriptText=$reader.ReadToEnd()}finally{$reader.Dispose()}
        . ([ScriptBlock]::Create($scriptText))
        # This lock lives in the recovery helper itself, not the watcher.
        $owned.Add([IO.File]::Open((Join-Path $RunRoot 'restore.lock'),'OpenOrCreate','ReadWrite','None'))
        $runtime=Join-Path $RootPath 'runtime'
        if(-not(Test-Path -LiteralPath $runtime)){throw 'Installation runtime missing.'}
        foreach($name in @('connection-operation.lock','setup.lock','upgrade.lock','network-control.lock','installation-maintenance.lock')){
            $lock=Join-Path $runtime $name
            if((Test-Path -LiteralPath $lock) -and ((Get-Item -LiteralPath $lock -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Installation operation lock link refused.'}
            $owned.Add([IO.File]::Open($lock,'OpenOrCreate','ReadWrite','None'))
        }
        $journal=Read-InstallJournal $RootPath $UserSid
        if(-not $journal){throw 'Installation journal missing; outcome is unconfirmed.'}
        if($journal.phase -in @('Armed','Restoring','RecoveryFailed')){
            if($journal.id -cne $ExpectedTransactionId -or $journal.incomingManifestHash -ne $ExpectedManifestSha256){throw 'Pending transaction does not match this installer.'}
            Restore-InstallTransaction $RootPath $UserSid
            $journal=Read-InstallJournal $RootPath $UserSid
        }
        if($journal.phase -eq 'RolledBack'){
            if($journal.id -cne $ExpectedTransactionId -or $journal.incomingManifestHash -ne $ExpectedManifestSha256){throw 'Rollback journal does not match this installer.'}
            foreach($entry in $journal.files){
                $target=Get-InstallPublicPath $RootPath $entry.name
                if($entry.existed){if(-not(Test-Path -LiteralPath $target) -or (Get-InstallSha256 $target) -ne $entry.sha256){throw 'Restored public file verification failed.'}}
                elseif(Test-Path -LiteralPath $target){throw 'Unexpected public file after rollback.'}
            }
            return [pscustomobject]@{success=$true;outcome='RolledBack';files=@($journal.files).Count}
        }
        if($journal.phase -eq 'Committed'){
            $manifestPath=Get-InstallPublicPath $RootPath 'manifest.json'
            $unchanged=$journal.id -ceq $PreviousJournalId -and $journal.incomingManifestHash -eq $PreviousManifestSha256
            if($unchanged){
                if(-not(Test-Path -LiteralPath $manifestPath) -or (Get-InstallSha256 $manifestPath) -ne $PreviousManifestSha256){throw 'Previous manifest differs.'}
                $manifest=[IO.File]::ReadAllText($manifestPath)|ConvertFrom-Json
                $statePath=Join-Path $RunRoot 'previous-state.json'
                if(-not(Test-Path -LiteralPath $statePath) -or ((Get-Item -LiteralPath $statePath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Previous state snapshot missing.'}
                $stateFile=[IO.File]::Open($statePath,'Open','Read','Read')
                try{
                    if($stateFile.Length -gt 4MB){throw 'Previous state snapshot too large.'}
                    $sha=[Security.Cryptography.SHA256]::Create();try{$stateHash=([BitConverter]::ToString($sha.ComputeHash($stateFile))).Replace('-','')}finally{$sha.Dispose()}
                    if($stateHash -ne $PreviousStateSha256){throw 'Previous state snapshot differs.'}
                    $stateFile.Position=0;$stateReader=[IO.StreamReader]::new($stateFile,[Text.Encoding]::UTF8,$true,4096,$true);try{$state=$stateReader.ReadToEnd()|ConvertFrom-Json}finally{$stateReader.Dispose()}
                }finally{$stateFile.Dispose()}
                if($state.schema -ne 1 -or $state.manifestSha256 -ne $PreviousManifestSha256 -or $state.journalId -cne $PreviousJournalId -or @($state.files).Count -gt 4097){throw 'Previous state snapshot identity refused.'}
                $expectedNames=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase);$null=$expectedNames.Add('manifest.json')
                foreach($entry in $manifest.files){if($entry.path -in @('force-direct.txt','force-vpn.txt')){continue};$null=Get-InstallPublicPath $RootPath $entry.path;$null=$expectedNames.Add($entry.path.Replace('/','\'))}
                foreach($entry in $state.files){
                    $relative=[string]$entry.name
                    if(!$expectedNames.Remove($relative.Replace('/','\')) -or $entry.existed -isnot [bool]){throw 'Previous state snapshot inventory differs.'}
                    $target=Get-InstallPublicPath $RootPath $relative
                    if($entry.existed){if(-not(Test-Path -LiteralPath $target) -or $entry.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or (Get-Item -LiteralPath $target).Length -ne $entry.bytes -or (Get-InstallSha256 $target) -ne $entry.sha256){throw 'Previous public file differs.'}}
                    elseif(Test-Path -LiteralPath $target){throw 'Unexpected previous public file.'}
                }
                if($expectedNames.Count -ne 0){throw 'Previous state snapshot incomplete.'}
                return [pscustomobject]@{success=$true;outcome='PreviousCommitVerified';files=@($state.files).Count}
            }else{
            if($journal.id -cne $ExpectedTransactionId){throw 'Committed transaction does not match this installer.'}
            $sidecar=Join-Path $RunRoot 'payload-manifest.json'
            if(-not(Test-Path -LiteralPath $sidecar) -or ((Get-Item -LiteralPath $sidecar -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Pinned incoming manifest missing.'}
            $incoming=[IO.File]::Open($sidecar,'Open','Read','Read')
            try {
                if($incoming.Length -gt 4MB){throw 'Pinned incoming manifest too large.'}
                $sha=[Security.Cryptography.SHA256]::Create()
                try{$incomingHash=([BitConverter]::ToString($sha.ComputeHash($incoming))).Replace('-','')}finally{$sha.Dispose()}
                if($incomingHash -ne $ExpectedManifestSha256 -or $incomingHash -ne $journal.incomingManifestHash -or -not(Test-Path -LiteralPath $manifestPath) -or (Get-InstallSha256 $manifestPath) -ne $incomingHash){throw 'Committed manifest differs.'}
                $incoming.Position=0;$manifestReader=[IO.StreamReader]::new($incoming,[Text.Encoding]::UTF8,$true,4096,$true)
                try{$manifest=$manifestReader.ReadToEnd()|ConvertFrom-Json}finally{$manifestReader.Dispose()}
            } finally {$incoming.Dispose()}
            }
            if(@($manifest.files).Count -lt 20 -or @($manifest.files).Count -gt 4096){throw 'Committed manifest inventory refused.'}
            foreach($entry in $manifest.files){
                if($entry.path -in @('force-direct.txt','force-vpn.txt')){continue}
                $target=Get-InstallPublicPath $RootPath $entry.path
                if(-not(Test-Path -LiteralPath $target) -or (Get-Item -LiteralPath $target).Length -ne $entry.bytes -or (Get-InstallSha256 $target) -ne $entry.sha256){throw 'Committed public file verification failed.'}
            }
            return [pscustomobject]@{success=$true;outcome='Committed';files=@($manifest.files).Count}
        }
        throw 'Installation recovery outcome is unconfirmed.'
    }
    finally{for($i=$owned.Count-1;$i -ge 0;$i--){$owned[$i].Dispose()};$scriptStream.Dispose()}
}
