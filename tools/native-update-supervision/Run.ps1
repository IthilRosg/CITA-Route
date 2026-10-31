$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
$journal=Join-Path $workspace 'runtime/native-supervision-active.json'
if(Test-Path -LiteralPath $journal){throw 'Reconcile the existing supervisor run before starting another.'}
$helper=Join-Path $workspace 'runtime/native-updater-build/Cita.NativeUpdater.exe'
$helperHash=(Get-FileHash -LiteralPath $helper).Hash
$actorHash=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Actor.ps1')).Hash
$recoveryHash=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Recover.ps1')).Hash
$recipe=Join-Path $workspace 'tools/native-control-probe/Test-PhysicalTransfer.ps1'
if((Get-FileHash -LiteralPath $recipe).Hash -cne '42CB3AA03261C76905617CAE5EBFAFE3F927229E5672B3278B4EFC9A6758BECE'){throw 'Directory recipe differs.'}
$create=[regex]::Match([IO.File]::ReadAllText($recipe),'(?s)\$create=@''\r?\n(.*?)\r?\n''@').Groups[1].Value
if(!$create){throw 'Directory recipe missing.'}
$id=[guid]::NewGuid().ToString('N');$folder='native-update-'+$id;$remote='C:\Program Files\RoutePilot\Agent\'+$folder;$task='CitaNativeSupervision-'+$id
$record=[ordered]@{Schema=1;Run=$id;Root=$remote;PrimaryTask=$task;BackupTask=($task+'-Backup');HelperSha=$helperHash;State='MayCreate';Utc=[datetime]::UtcNow.ToString('O')}
$f=[IO.File]::Open($journal,'CreateNew','Write','Read');try{$bytes=[Text.Encoding]::UTF8.GetBytes(($record|ConvertTo-Json -Compress));$f.Write($bytes,0,$bytes.Length);$f.Flush($true)}finally{$f.Dispose()}
if((Invoke-BoundedRm $create.Replace('__DIR__',$remote)).Trim() -cne 'ready'){throw 'Protected root unconfirmed.'}
foreach($row in @(@($helper,'Cita.NativeUpdater.exe'),@((Join-Path $PSScriptRoot 'Actor.ps1'),'Actor.ps1'),@((Join-Path $PSScriptRoot 'Recover.ps1'),'Recover.ps1'))){Invoke-BoundedScp $row[0] ('citastation@192.168.1.124:'+($remote.Replace('\','/')+'/'+$row[1]))}
$start=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Start.ps1')).Replace('__ROOT__',$remote).Replace('__ID__',$id).Replace('__TASK__',$task).Replace('__HELPER__',$helperHash).Replace('__ACTOR__',$actorHash).Replace('__RECOVER__',$recoveryHash)
$startPath=Join-Path $workspace ('runtime/native-supervision-start-'+$id+'.ps1')
[IO.File]::WriteAllText($startPath,$start,[Text.UTF8Encoding]::new($true));$startHash=(Get-FileHash -LiteralPath $startPath).Hash
Invoke-BoundedScp $startPath ('citastation@192.168.1.124:'+($remote.Replace('\','/')+'/Start.ps1'))
$result=Invoke-BoundedRm "if((Get-FileHash -LiteralPath '$remote\Start.ps1').Hash -cne '$startHash'){throw 'Start script differs.'};& ([ScriptBlock]::Create([IO.File]::ReadAllText('$remote\Start.ps1')))" 75
$result|Set-Content -LiteralPath (Join-Path $workspace ('runtime/native-supervision-start-result-'+$id+'.json'))
$result
