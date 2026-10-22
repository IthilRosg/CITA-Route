#requires -Version 5.1
[CmdletBinding()]
param([string]$Root=$PSScriptRoot,[string]$AgentPath,[switch]$Remove)
$ErrorActionPreference='Stop'
$Root=[IO.Path]::GetFullPath($Root).TrimEnd('\')
if(-not (Test-Path -LiteralPath (Join-Path $Root 'data') -PathType Container)){throw 'Укажи папку установленного клиента CITA Route.'}
if(-not $AgentPath){$AgentPath=Join-Path $env:ProgramFiles 'RoutePilot\Agent\RoutePilot.Agent.exe';if(-not (Test-Path -LiteralPath $AgentPath)){$AgentPath=Join-Path $Root 'runtime\agent-build\RoutePilot.Agent.exe'}}
$AgentPath=[IO.Path]::GetFullPath($AgentPath)
if(-not (Test-Path -LiteralPath $AgentPath -PathType Leaf)){throw 'Агент CITA Route не найден.'}
if(-not $Remove -and (Test-Path -LiteralPath (Join-Path $Root 'runtime\session.json'))){$session=Get-Content -LiteralPath (Join-Path $Root 'runtime\session.json') -Raw|ConvertFrom-Json;if($session.running){throw 'Сначала отключи маршрут в CITA Route. После настройки подключись снова.'}}
$output=Join-Path ([IO.Path]::GetTempPath()) ('cita-browser-'+[guid]::NewGuid().ToString('N')+'.out')
$errorFile=$output+'.err';$verb=if($Remove){'--unregister-browser-feedback'}else{'--register-browser-feedback'}
try{
 $run=Start-Process -FilePath $AgentPath -ArgumentList @($verb,('"'+$Root+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput $output -RedirectStandardError $errorFile
 $null=$run.Handle
 try{if(-not $run.WaitForExit(30000)){$run.Kill();throw 'Настройка адаптера не завершилась вовремя.'};$run.Refresh();if($run.ExitCode -ne 0){throw ('Не удалось настроить адаптер. '+[IO.File]::ReadAllText($errorFile))}}finally{$run.Dispose()}
 if($Remove){'Регистрация браузерного адаптера удалена.'}else{'Адаптер настроен для текущего пользователя. Подключись в CITA Route, загрузи папку browser-extension в Chrome/Edge и включи расширение.'}
}finally{foreach($file in @($output,$errorFile)){if(Test-Path -LiteralPath $file){Remove-Item -LiteralPath $file -Force}}}
