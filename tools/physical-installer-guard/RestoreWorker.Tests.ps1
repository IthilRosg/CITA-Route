$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$parent=Join-Path $workspace 'runtime/physical-installer-guard-tests'
[void][IO.Directory]::CreateDirectory($parent)
$test=Join-Path $parent ([guid]::NewGuid().ToString('N'))
$client=Join-Path $test 'client';$run=Join-Path $test 'run';$sid='S-1-5-21-1-2-3-1001'
[void][IO.Directory]::CreateDirectory((Join-Path $client 'runtime'));[void][IO.Directory]::CreateDirectory($run)
$transaction=Join-Path $run 'InstallationTransaction.ps1'
Copy-Item -LiteralPath (Join-Path $workspace 'installer/InstallationTransaction.ps1') -Destination $transaction
. (Join-Path $PSScriptRoot 'RestoreWorker.ps1')
try {
    $expected='0'*64;$previous='1'*64;$oldId='2'*32;$stateHash='4'*64;$expectedTxn='a'*32
    $failed=$false;try{Invoke-GuardedPublicRestore $client $sid $run $transaction $expected $previous $oldId $stateHash $expectedTxn|Out-Null}catch{$failed=$true};if(!$failed){throw 'Missing journal accepted'}
    $id=[guid]::NewGuid().ToString('N');$expectedTxn=$id;$backup=Join-Path $client ('runtime/installation-backup-'+$id)
    [void][IO.Directory]::CreateDirectory($backup)
    [IO.File]::WriteAllText((Join-Path $backup 'example.txt'),'before')
    [IO.File]::WriteAllText((Join-Path $client 'example.txt'),'after')
    $sha=(Get-FileHash -LiteralPath (Join-Path $backup 'example.txt')).Hash
    $j=@{schema=1;id=$id;root=$client;userSid=$sid;backup=$backup;phase='Armed';files=@(@{name='example.txt';existed=$true;sha256=$sha});version='0.0.0';incomingManifestHash=('0'*64)}
    $journal=Join-Path $client 'runtime/installation-transaction.json'
    [IO.File]::WriteAllText($journal,($j|ConvertTo-Json -Depth 5))
    $failed=$false;try{Invoke-GuardedPublicRestore $client $sid $run $transaction ('5'*64) $previous $oldId $stateHash $expectedTxn|Out-Null}catch{$failed=$true}
    if(!$failed -or [IO.File]::ReadAllText((Join-Path $client 'example.txt')) -ne 'after'){throw 'Foreign pending journal accepted'}
    $failed=$false;try{Invoke-GuardedPublicRestore $client $sid $run $transaction $expected $previous $oldId $stateHash ('b'*32)|Out-Null}catch{$failed=$true}
    if(!$failed -or [IO.File]::ReadAllText((Join-Path $client 'example.txt')) -ne 'after'){throw 'Same-payload foreign transaction accepted'}
    $failed=$false;try{Invoke-GuardedPublicRestore $client $sid $run $transaction $expected $previous $id $stateHash $expectedTxn|Out-Null}catch{$failed=$true}
    if(!$failed){throw 'Previous journal accepted as new transaction'}
    $lock=[IO.File]::Open((Join-Path $client 'runtime/setup.lock'),'OpenOrCreate','ReadWrite','None')
    try{$failed=$false;try{Invoke-GuardedPublicRestore $client $sid $run $transaction $expected $previous $oldId $stateHash $expectedTxn|Out-Null}catch{$failed=$true};if(!$failed){throw 'Occupied installation lock accepted'}}finally{$lock.Dispose()}
    [IO.File]::WriteAllText((Join-Path $backup 'example.txt'),'corrupt')
    $failed=$false;try{Invoke-GuardedPublicRestore $client $sid $run $transaction $expected $previous $oldId $stateHash $expectedTxn|Out-Null}catch{$failed=$true}
    if(!$failed -or [IO.File]::ReadAllText((Join-Path $client 'example.txt')) -ne 'after'){throw 'Bad backup was applied'}
    [IO.File]::WriteAllText((Join-Path $backup 'example.txt'),'before')
    $j.phase='Armed';[IO.File]::WriteAllText($journal,($j|ConvertTo-Json -Depth 5))
    $result=Invoke-GuardedPublicRestore $client $sid $run $transaction $expected $previous $oldId $stateHash $expectedTxn
    if(!$result.success -or $result.outcome -ne 'RolledBack' -or [IO.File]::ReadAllText((Join-Path $client 'example.txt')) -ne 'before'){throw 'Rollback mismatch'}
    [IO.File]::WriteAllText((Join-Path $client 'example.txt'),'tampered')
    $failed=$false;try{Invoke-GuardedPublicRestore $client $sid $run $transaction $expected $previous $oldId $stateHash $expectedTxn|Out-Null}catch{$failed=$true};if(!$failed){throw 'Damaged rollback accepted'}
    $files=@();for($i=0;$i -lt 20;$i++){
        $name='file-'+$i+'.txt';$path=Join-Path $client $name;[IO.File]::WriteAllText($path,'new')
        $files+=@{path=$name;bytes=(Get-Item -LiteralPath $path).Length;sha256=(Get-FileHash -LiteralPath $path).Hash}
    }
    $manifest=Join-Path $client 'manifest.json';[IO.File]::WriteAllText($manifest,(@{files=$files}|ConvertTo-Json -Depth 5))
    Copy-Item -LiteralPath $manifest -Destination (Join-Path $run 'payload-manifest.json')
    $j.phase='Committed';$j.incomingManifestHash=(Get-FileHash -LiteralPath $manifest).Hash
    $expected=$j.incomingManifestHash
    [IO.File]::WriteAllText($journal,($j|ConvertTo-Json -Depth 5))
    $result=Invoke-GuardedPublicRestore $client $sid $run $transaction $expected $previous $oldId $stateHash $expectedTxn
    if(!$result.success -or $result.outcome -ne 'Committed'){throw 'Committed verification mismatch'}
    $failed=$false;try{Invoke-GuardedPublicRestore $client $sid $run $transaction ('3'*64) $previous $oldId $stateHash $expectedTxn|Out-Null}catch{$failed=$true};if(!$failed){throw 'Wrong incoming manifest identity accepted'}
    [IO.File]::WriteAllText((Join-Path $client 'file-0.txt'),'bad')
    $failed=$false;try{Invoke-GuardedPublicRestore $client $sid $run $transaction $expected $previous $oldId $stateHash $expectedTxn|Out-Null}catch{$failed=$true};if(!$failed){throw 'Damaged committed install accepted'}
    [IO.File]::WriteAllText((Join-Path $client 'file-0.txt'),'new')
    # An old manifest can legitimately describe stale content; the protected
    # snapshot must use actual bytes, not the manifest's declared hashes.
    $files[0].sha256='A'*64;[IO.File]::WriteAllText($manifest,(@{files=$files}|ConvertTo-Json -Depth 5))
    $oldManifestHash=(Get-FileHash -LiteralPath $manifest).Hash;$j.incomingManifestHash=$oldManifestHash;[IO.File]::WriteAllText($journal,($j|ConvertTo-Json -Depth 5))
    $stateEntries=@($files|ForEach-Object {$actual=Join-Path $client $_.path;@{name=$_.path;existed=$true;bytes=(Get-Item -LiteralPath $actual).Length;sha256=(Get-FileHash -LiteralPath $actual).Hash}})
    $stateEntries+=@{name='manifest.json';existed=$true;bytes=(Get-Item -LiteralPath $manifest).Length;sha256=$oldManifestHash}
    $statePath=Join-Path $run 'previous-state.json';[IO.File]::WriteAllText($statePath,(@{schema=1;manifestSha256=$oldManifestHash;journalId=$id;files=$stateEntries}|ConvertTo-Json -Depth 5));$stateHash=(Get-FileHash -LiteralPath $statePath).Hash
    $result=Invoke-GuardedPublicRestore $client $sid $run $transaction $expected $oldManifestHash $id $stateHash ('a'*32)
    if(!$result.success -or $result.outcome -ne 'PreviousCommitVerified'){throw 'Unchanged prior commit misclassified'}
    [IO.File]::WriteAllText((Join-Path $client 'file-0.txt'),'changed')
    $failed=$false;try{Invoke-GuardedPublicRestore $client $sid $run $transaction $expected $oldManifestHash $id $stateHash ('a'*32)|Out-Null}catch{$failed=$true};if(!$failed){throw 'Changed previous snapshot accepted'}
    $fresh=Join-Path $test 'fresh/client';[void][IO.Directory]::CreateDirectory((Join-Path $fresh 'runtime'))
    $incoming=Join-Path $run 'incoming-test.json';[IO.File]::WriteAllText($incoming,(@{version='0.6.9.0';architecture='win-x64';files=$files}|ConvertTo-Json -Depth 5))
    . $transaction
    $transactionId=[guid]::NewGuid().ToString('N')
    Start-InstallTransaction $fresh $sid $incoming ([version]'0.6.9.0') $transactionId
    $newJournal=Read-InstallJournal $fresh $sid
    if($newJournal.id -cne $transactionId -or $newJournal.phase -ne 'Armed'){throw 'Explicit transaction id was not used'}
    $failed=$false;try{Start-InstallTransaction $fresh $sid $incoming ([version]'0.6.9.0') $transactionId}catch{$failed=$true}
    if(!$failed -or (Read-InstallJournal $fresh $sid).id -cne $transactionId){throw 'Reused transaction id was accepted'}
    $other=Join-Path $test 'other/client';[void][IO.Directory]::CreateDirectory((Join-Path $other 'runtime'))
    $occupiedId=[guid]::NewGuid().ToString('N');[void][IO.Directory]::CreateDirectory((Join-Path $other ('runtime/installation-backup-'+$occupiedId)))
    $failed=$false;try{Start-InstallTransaction $other $sid $incoming ([version]'0.6.9.0') $occupiedId}catch{$failed=$true};if(!$failed){throw 'Existing backup id accepted'}
    $ordinary=Join-Path $test 'ordinary/client';[void][IO.Directory]::CreateDirectory((Join-Path $ordinary 'runtime'))
    Start-InstallTransaction $ordinary $sid $incoming ([version]'0.6.9.0')
    if((Read-InstallJournal $ordinary $sid).id -cnotmatch '^[a-f0-9]{32}$'){throw 'Ordinary transaction id was not generated'}
    'PASS: missing journal refused, verified rollback, tampered rollback rejected, verified commit, damaged commit rejected.'
} finally {
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($test)) -ine [IO.Path]::GetFullPath($parent) -or (Split-Path $test -Leaf) -cnotmatch '^[a-f0-9]{32}$'){throw 'Unsafe test cleanup'}
    if(Test-Path -LiteralPath $test){Remove-Item -LiteralPath $test -Recurse -Force}
}
