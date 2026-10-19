[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Root,
      [Parameter(Mandatory=$true)][int]$OwnerPid,
      [Parameter(Mandatory=$true)][string]$ReadyFile,
      [Parameter(Mandatory=$true)][string]$ReleaseFile,
      [Parameter(Mandatory=$true)][version]$Version,
      [ValidateSet('Install','Uninstall')][string]$Mode='Install')
$ErrorActionPreference='Stop'
$held=New-Object 'System.Collections.Generic.List[System.IDisposable]'
$owner=$null
try{
    $owner=Get-Process -Id $OwnerPid -ErrorAction Stop
    $rootPath=[IO.Path]::GetFullPath($Root).TrimEnd('\')
    if((Split-Path $rootPath -Leaf) -ne 'client'){throw 'Неверная папка установки.'}
    # An installer must never follow a link into another working directory.
    for($part=$rootPath;$part;$part=Split-Path $part){
        if((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Папка установки содержит перенаправление. Выбери обычную папку.'}
    }
    if(Test-Path -LiteralPath $rootPath){
        $productPath=Join-Path $rootPath 'product.json'
        if(Test-Path -LiteralPath $productPath){
            $product=[IO.File]::ReadAllText($productPath) | ConvertFrom-Json
            if($product.name -ne 'CITA Route'){throw 'В этой папке находится другое приложение.'}
            if($Mode -eq 'Install' -and [version]$product.version -gt $Version){throw 'Уже установлена более новая версия. Понижение версии отменено.'}
        }elseif(@(Get-ChildItem -LiteralPath $rootPath -Force | Where-Object Name -ne 'runtime').Count){throw 'Папка не является установкой CITA Route.'}
    }
    $runtime=Join-Path $rootPath 'runtime'
    New-Item -ItemType Directory -Path $runtime -Force | Out-Null
    # Match the updater's lock order. Keep the locks until Setup/Uninstall exits,
    # including a crash; a new connection cannot start during file replacement.
    foreach($name in @('connection-operation.lock','setup.lock','upgrade.lock','network-control.lock','installation-maintenance.lock')){
        try{$held.Add([IO.File]::Open((Join-Path $runtime $name),'OpenOrCreate','ReadWrite','None'))}
        catch{throw 'Другая операция управляет подключением или обновлением. Дождись её завершения.'}
    }
    $sessionPath=Join-Path $runtime 'session.json'
    if(Test-Path -LiteralPath $sessionPath){
        try{$session=[IO.File]::ReadAllText($sessionPath) | ConvertFrom-Json;if($session.running -isnot [bool]){throw 'Invalid state'}}
        catch{throw 'Не удалось прочитать состояние подключения. Проверь его в CITA Route.'}
        if($session.running){throw 'Выбери «Отключить и выйти» в CITA Route, затем повтори установку или удаление.'}
    }
    # Check only this installation. Never stop another copy or a VPN service.
    $executables=@((Join-Path $rootPath 'RoutePilot.exe'),(Join-Path $runtime 'app-build\RoutePilot.exe'),(Join-Path $runtime 'smart-router.exe'),(Join-Path $runtime 'vpn-bridge.exe'))
    foreach($process in @(Get-Process -Name 'RoutePilot','smart-router','vpn-bridge' -ErrorAction SilentlyContinue)){
        try{if($process.Path -in $executables){throw 'Закрой эту копию CITA Route через «Отключить и выйти», затем повтори действие.'}}
        finally{$process.Dispose()}
    }
    if($owner.HasExited){throw 'Установщик уже завершён.'}
    [IO.File]::WriteAllText($ReadyFile,'READY')
    while(-not $owner.HasExited -and -not (Test-Path -LiteralPath $ReleaseFile)){Start-Sleep -Milliseconds 100}
}catch{
    [IO.File]::WriteAllText($ReadyFile,$_.Exception.Message,(New-Object Text.UTF8Encoding($true)))
    exit 1
}finally{
    for($index=$held.Count-1;$index -ge 0;$index--){$held[$index].Dispose()}
    if($owner){$owner.Dispose()}
}
[IO.File]::WriteAllText($ReadyFile,'DONE')
