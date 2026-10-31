function Get-InstallSha256([string]$Path) {
    $stream=[IO.File]::OpenRead($Path);$hash=[Security.Cryptography.SHA256]::Create()
    try{return ([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-','')}finally{$hash.Dispose();$stream.Dispose()}
}
# Public-file transaction only. User data, rules, profiles and network settings
# are excluded. The caller holds the installation's ordinary operation locks.
function Get-InstallPublicPath([string]$RootPath,[string]$Name) {
    $normalized=$Name.Replace('/','\')
    if(-not $normalized -or [IO.Path]::IsPathRooted($normalized) -or $normalized -match '(^|\\)\.\.?($|\\)' -or $normalized.Contains(':') -or
       $normalized -match '^(?i:data|private|logs|artifacts)(\\|$)' -or $normalized -match '^(?i:runtime)\\(?i:updates|installation-backup-[^\\]+)(\\|$)' -or
       $normalized -match '^(?i:runtime)\\(?i:installation-transaction\.json|installation-maintenance\.lock)$' -or $normalized -match '(?i)\.conf$' -or
       $normalized -in @('force-direct.txt','force-vpn.txt')) { throw 'Недопустимый путь публичного файла установки.' }
    $target=[IO.Path]::GetFullPath((Join-Path $RootPath $normalized))
    if(-not $target.StartsWith($RootPath+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Путь выходит за папку клиента.'}
    for($part=$target;$part -and $part.Length -ge $RootPath.Length;$part=Split-Path $part){
        if((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Публичные файлы установки содержат перенаправление.'}
    }
    if((Test-Path -LiteralPath $target) -and (Get-Item -LiteralPath $target -Force).PSIsContainer){throw 'Вместо файла установки существует папка.'}
    return $target
}
function Write-InstallJournal([string]$Path,$Value) {
    $temporary=[IO.Path]::Combine([IO.Path]::GetDirectoryName($Path),('.'+[guid]::NewGuid().ToString('N')+'.tmp'))
    try {
        $bytes=[Text.Encoding]::UTF8.GetBytes(($Value|ConvertTo-Json -Depth 8))
        $stream=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
        try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
        if(Test-Path -LiteralPath $Path){[IO.File]::Replace($temporary,$Path,[NullString]::Value)}else{[IO.File]::Move($temporary,$Path)}
    }finally{Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue}
}
function Read-InstallJournal([string]$RootPath,[string]$Sid) {
    $path=Join-Path $RootPath 'runtime\installation-transaction.json'
    if(-not(Test-Path -LiteralPath $path)){return $null}
    if((Get-Item -LiteralPath $path -Force).Length -gt 4MB -or ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Недопустимый журнал восстановления установки.'}
    $value=[IO.File]::ReadAllText($path)|ConvertFrom-Json
    if($value.schema -ne 1 -or $value.id -notmatch '^[a-f0-9]{32}$' -or $value.root -ine $RootPath -or $value.userSid -ne $Sid -or
       $value.backup -ine (Join-Path $RootPath ('runtime\installation-backup-'+$value.id)) -or @($value.files).Count -gt 4096 -or
       $value.phase -notin @('Armed','Restoring','RecoveryFailed','RolledBack','Committed')){throw 'Журнал восстановления не принадлежит этой установке.'}
    foreach($entry in $value.files){$null=Get-InstallPublicPath $RootPath $entry.name;if($entry.existed -isnot [bool] -or ($entry.existed -and $entry.sha256 -notmatch '^[a-fA-F0-9]{64}$')){throw 'Недопустимая запись восстановления.'}}
    return $value
}
function Restore-InstallTransaction([string]$RootPath,[string]$Sid) {
    $value=Read-InstallJournal $RootPath $Sid
    if(-not $value -or $value.phase -in @('Committed','RolledBack')){return}
    $journal=Join-Path $RootPath 'runtime\installation-transaction.json'
    try {
        # Validate all backups before modifying any target. Keep them on failure.
        foreach($entry in $value.files){
            if(-not $entry.existed){continue}
            $saved=Get-InstallPublicPath $value.backup $entry.name
            if(-not(Test-Path -LiteralPath $saved) -or (Get-InstallSha256 $saved) -ne $entry.sha256){throw 'Резервная копия публичного файла повреждена.'}
        }
        $value.phase='Restoring';Write-InstallJournal $journal $value
        # Restore the GUI last, retaining its recovery gate until the other
        # public files are restored. Operation locks stay held throughout.
        foreach($entry in @($value.files|Sort-Object @{Expression={$_.name -ieq 'RoutePilot.exe'}})){
            $target=Get-InstallPublicPath $RootPath $entry.name
            if($entry.existed){
                [void][IO.Directory]::CreateDirectory((Split-Path $target));$temporary=$target+'.'+[guid]::NewGuid().ToString('N')+'.restore'
                try{[IO.File]::Copy((Get-InstallPublicPath $value.backup $entry.name),$temporary);if((Get-InstallSha256 $temporary) -ne $entry.sha256){throw 'Хеш восстановленного файла отличается.'};if(Test-Path -LiteralPath $target){[IO.File]::Replace($temporary,$target,[NullString]::Value)}else{[IO.File]::Move($temporary,$target)}}finally{Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue}
            }elseif(Test-Path -LiteralPath $target){Remove-Item -LiteralPath $target -Force}
        }
        $value.phase='RolledBack';Write-InstallJournal $journal $value
    }catch{
        $value.phase='RecoveryFailed';Write-InstallJournal $journal $value
        throw 'Не удалось полностью восстановить предыдущую установку. Резервная копия сохранена. Закрой программы, использующие её файлы, и повтори установщик.'
    }
}
function Start-InstallTransaction([string]$RootPath,[string]$Sid,[string]$IncomingManifest,[version]$Version,[string]$TransactionId='') {
    if($TransactionId -and $TransactionId -cnotmatch '^[a-f0-9]{32}$'){throw 'Недопустимый идентификатор операции установки.'}
    if($TransactionId){$existing=Read-InstallJournal $RootPath $Sid;if($existing -and $existing.id -ceq $TransactionId){throw 'Идентификатор операции уже использован.'}}
    Clear-InstallBackup $RootPath $Sid
    if(-not $IncomingManifest -or (Get-Item -LiteralPath $IncomingManifest).Length -gt 4MB){throw 'Отсутствует проверяемый список файлов новой установки.'}
    $incoming=[IO.File]::ReadAllText($IncomingManifest)|ConvertFrom-Json
    if([version]$incoming.version -ne $Version -or $incoming.architecture -ne 'win-x64' -or @($incoming.files).Count -lt 20 -or @($incoming.files).Count -gt 4096){throw 'Список файлов новой установки не соответствует версии.'}
    $names=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $null=$names.Add('manifest.json')
    $oldManifest=Join-Path $RootPath 'manifest.json'
    $manifests=@($incoming)
    if(Test-Path -LiteralPath $oldManifest){if((Get-Item -LiteralPath $oldManifest).Length -gt 4MB){throw 'Список предыдущей установки слишком велик.'};$manifests+=([IO.File]::ReadAllText($oldManifest)|ConvertFrom-Json)}
    foreach($manifest in $manifests){foreach($file in $manifest.files){if($file.path -in @('force-direct.txt','force-vpn.txt')){continue};$null=Get-InstallPublicPath $RootPath $file.path;$null=$names.Add($file.path.Replace('/','\'))}}
    if($names.Count -gt 4096){throw 'Слишком много публичных файлов установки.'}
    $id=if($TransactionId){$TransactionId}else{[guid]::NewGuid().ToString('N')};$backup=Join-Path $RootPath ('runtime\installation-backup-'+$id)
    if(Test-Path -LiteralPath $backup){throw 'Резервная копия операции уже существует.'}
    [void][IO.Directory]::CreateDirectory($backup);$entries=@()
    foreach($name in $names){
        $target=Get-InstallPublicPath $RootPath $name;$exists=Test-Path -LiteralPath $target;$entry=@{name=$name;existed=[bool]$exists;sha256=''}
        if($exists){
            $saved=Get-InstallPublicPath $backup $name;[void][IO.Directory]::CreateDirectory((Split-Path $saved))
            # A locked/unwritable old file rejects setup before READY/copying.
            $source=[IO.File]::Open($target,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
            try{$destination=[IO.File]::Create($saved);try{$source.CopyTo($destination);$destination.Flush($true)}finally{$destination.Dispose()}}finally{$source.Dispose()}
            $entry.sha256=(Get-InstallSha256 $saved)
        }
        $entries+=$entry
    }
    $value=@{schema=1;id=$id;root=$RootPath;userSid=$Sid;backup=$backup;phase='Armed';files=$entries;version=$Version.ToString();incomingManifestHash=(Get-InstallSha256 $IncomingManifest)}
    Write-InstallJournal (Join-Path $RootPath 'runtime\installation-transaction.json') $value
}
function Clear-InstallBackup([string]$RootPath,[string]$Sid) {
    $previous=Read-InstallJournal $RootPath $Sid
    if(-not $previous -or $previous.phase -notin @('Committed','RolledBack') -or -not(Test-Path -LiteralPath $previous.backup)){return}
    # Only our exact UUID directory may be deleted, with no links inside it.
    $resolved=[IO.Path]::GetFullPath($previous.backup)
    if($resolved -ine (Join-Path $RootPath ('runtime\installation-backup-'+$previous.id))){throw 'Недопустимая папка резервной копии.'}
    if(((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
       @(Get-ChildItem -LiteralPath $resolved -Recurse -Force|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Резервная копия содержит перенаправление.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
function Complete-InstallTransaction([string]$RootPath,[string]$Sid,[string]$IncomingManifest,[string]$ReleaseFile) {
    $value=Read-InstallJournal $RootPath $Sid
    if(-not $value -or $value.phase -ne 'Armed' -or (Get-InstallSha256 $IncomingManifest) -ne $value.incomingManifestHash){throw 'Проверяемая установка изменилась.'}
    $manifest=[IO.File]::ReadAllText($IncomingManifest)|ConvertFrom-Json
    foreach($entry in $manifest.files){
        if($entry.path -in @('force-direct.txt','force-vpn.txt')){continue}
        $target=Get-InstallPublicPath $RootPath $entry.path
        if((Get-Item -LiteralPath $target).Length -ne $entry.bytes -or (Get-InstallSha256 $target) -ne $entry.sha256){throw 'Не все файлы новой установки прошли проверку.'}
    }
    if((Get-InstallSha256 (Join-Path $RootPath 'manifest.json')) -ne $value.incomingManifestHash -or [IO.File]::ReadAllText($ReleaseFile).Trim() -ne 'COMMIT'){throw 'Подтверждение установки отменено.'}
    $value.phase='Committed';Write-InstallJournal (Join-Path $RootPath 'runtime\installation-transaction.json') $value
    # A cleanup refusal must not roll back a fully verified committed install.
    try{Clear-InstallBackup $RootPath $Sid}catch{}
}
