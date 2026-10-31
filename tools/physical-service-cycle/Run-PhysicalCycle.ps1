[CmdletBinding()]
param([switch]$ViaRadmin)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')

function Invoke-PhysicalRm([string]$Code,[int]$TimeoutSeconds=30){
    Invoke-BoundedRm -Code $Code -TimeoutSeconds $TimeoutSeconds -ViaRadmin:$ViaRadmin
}
function Invoke-PhysicalScp([string]$Source,[string]$Destination,[int]$TimeoutSeconds=45){
    Invoke-BoundedScp -Source $Source -Destination $Destination -TimeoutSeconds $TimeoutSeconds -ViaRadmin:$ViaRadmin
}
function Write-SystemWrapper([string]$Path,[string]$RemoteRoot,[string]$ScriptName,[string]$ResultName){
    $text=@'
$ErrorActionPreference='Stop'
$root='__ROOT__';$script=Join-Path $root '__SCRIPT__';$result=Join-Path $root '__RESULT__';$temporary=$result+'.tmp'
try{
 $output=(& $script 2>&1|Out-String).Trim()
 $payload=@{ok=$true;output=$output}|ConvertTo-Json -Compress
 [IO.File]::WriteAllText($temporary,$payload,[Text.UTF8Encoding]::new($false));Move-Item -LiteralPath $temporary -Destination $result -Force
}catch{
 $payload=@{ok=$false;error=$_.Exception.Message;exception=$_.Exception.ToString();line=$_.InvocationInfo.ScriptLineNumber}|ConvertTo-Json -Compress
 [IO.File]::WriteAllText($temporary,$payload,[Text.UTF8Encoding]::new($false));Move-Item -LiteralPath $temporary -Destination $result -Force
 exit 1
}
'@
    $text=$text.Replace('__ROOT__',$RemoteRoot).Replace('__SCRIPT__',$ScriptName).Replace('__RESULT__',$ResultName)
    [IO.File]::WriteAllText($Path,$text,[Text.UTF8Encoding]::new($true))
}
function Invoke-SystemScript([string]$RemoteRoot,[string]$RemoteWrapperName,[string]$ResultName,[string]$TaskName,[int]$TimeoutSeconds=160){
    $resultPath=Join-Path $RemoteRoot $ResultName
    $wrapperPath=Join-Path $RemoteRoot $RemoteWrapperName
    $create=@'
$ErrorActionPreference='Stop'
$task='__TASK__';$action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "__WRAPPER__"')
$principal=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$trigger=New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(10)
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Trigger $trigger -Force|Out-Null
Start-ScheduledTask -TaskName $task
'queued'
'@
    $create=$create.Replace('__TASK__',$TaskName).Replace('__WRAPPER__',$wrapperPath)
    $queued=([string](Invoke-PhysicalRm $create 30)).Trim();if($queued -ne 'queued'){throw 'SYSTEM task was not queued.'}
    $completed=$false
    try{
        for($i=0;$i -lt $TimeoutSeconds;$i++){
            $probe=@"
if(Test-Path -LiteralPath '$resultPath'){Get-Content -LiteralPath '$resultPath' -Raw}else{'__WAIT__'}
"@
            $raw=([string](Invoke-PhysicalRm $probe 20)).Trim()
            if($raw -and $raw -ne '__WAIT__'){
                $envelope=$raw|ConvertFrom-Json
                if($envelope.ok -ne $true){throw ('SYSTEM script failed: '+[string]$envelope.error)}
                $completed=$true;return $envelope
            }
            $state=([string](Invoke-PhysicalRm "if(Get-ScheduledTask -TaskName '$TaskName' -ErrorAction SilentlyContinue){(Get-ScheduledTask -TaskName '$TaskName').State}else{'Missing'}" 15)).Trim()
            if($state -eq 'Missing'){throw 'SYSTEM task disappeared before completion evidence.'}
            Start-Sleep -Seconds 1
        }
        throw 'SYSTEM script completion timed out; remote outcome is unknown.'
    }finally{
        if($completed){try{Invoke-PhysicalRm "Unregister-ScheduledTask -TaskName '$TaskName' -Confirm:`$false -ErrorAction SilentlyContinue" 20|Out-Null}catch{}}
    }
}

$journal=Join-Path $workspace 'runtime/service-cycle-active.json'
if(Test-Path -LiteralPath $journal){throw 'Previous service cycle requires reconciliation.'}
$helper=Join-Path $workspace 'runtime/physical-service-cycle-candidate-v11/Cita.PhysicalServiceCycle.exe'
$hash=(Get-FileHash -LiteralPath $helper -Algorithm SHA256).Hash
if($hash -cne '5466FCD2E410997601F006F8B19C9E1EFE3D2512D974AF16648F6B63755729F1'){throw 'Service cycle helper differs.'}
$recipe=Join-Path $workspace 'tools/native-control-probe/Test-PhysicalTransfer.ps1'
if((Get-FileHash -LiteralPath $recipe).Hash -ne '42CB3AA03261C76905617CAE5EBFAFE3F927229E5672B3278B4EFC9A6758BECE'){throw 'Protected directory recipe differs.'}
$pattern='(?s)\$create=@''\r?\n(.*?)\r?\n''@'
$create=[regex]::Match([IO.File]::ReadAllText($recipe),$pattern).Groups[1].Value
if(!$create){throw 'Protected directory recipe missing.'}
$id=[guid]::NewGuid().ToString('N');$folder='cita-cycle-'+$id;$remote='C:\Program Files\RoutePilot\Agent\'+$folder;$task='CitaServiceCycle-'+$id;$backup=$task+'-Backup'
$transport='LAN';if($ViaRadmin){$transport='RadminVPN'}
$entry=[ordered]@{schema=1;run=$id;root=$remote;primaryTask=$task;backupTask=$backup;helperSha256=$hash;transport=$transport;state='MayCreate';utc=[DateTimeOffset]::UtcNow.ToString('o')}
$f=[IO.File]::Open($journal,'CreateNew','Write','Read');try{$bytes=[Text.Encoding]::UTF8.GetBytes(($entry|ConvertTo-Json -Compress));$f.Write($bytes,0,$bytes.Length);$f.Flush($true)}finally{$f.Dispose()}
if((Invoke-PhysicalRm $create.Replace('__DIR__',$remote)).Trim() -ne 'ready'){throw 'Protected cycle directory is unconfirmed.'}
Invoke-PhysicalScp $helper ("citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/$folder/Cita.PhysicalServiceCycle.exe")
$startPath=Join-Path $workspace ("runtime/service-cycle-start-"+$id+'.ps1')
$start=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Start-PhysicalCycle.ps1')).Replace('__ROOT__',$remote).Replace('__ID__',$id).Replace('__TASK__',$task).Replace('__HELPER__',$hash)
[IO.File]::WriteAllText($startPath,$start,[Text.UTF8Encoding]::new($true));$startHash=(Get-FileHash -LiteralPath $startPath).Hash
Invoke-PhysicalScp $startPath ("citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/$folder/start.ps1")
$startWrapperPath=Join-Path $workspace ("runtime/service-cycle-system-start-"+$id+'.ps1');Write-SystemWrapper $startWrapperPath $remote 'start.ps1' 'system-start-result.json';$startWrapperHash=(Get-FileHash -LiteralPath $startWrapperPath).Hash
Invoke-PhysicalScp $startWrapperPath ("citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/$folder/system-start.ps1")
$startEnvelope=Invoke-SystemScript $remote 'system-start.ps1' 'system-start-result.json' ($task+'-SystemStart') 170
if((Invoke-PhysicalRm "if((Get-FileHash -LiteralPath '$remote\start.ps1').Hash -ne '$startHash'){throw 'Start script differs'};if((Get-FileHash -LiteralPath '$remote\system-start.ps1').Hash -ne '$startWrapperHash'){throw 'SYSTEM wrapper differs'};'confirmed'" 40).Trim() -ne 'confirmed'){throw 'Start scripts differ.'}
$started=([string]$startEnvelope.output).Trim()|ConvertFrom-Json
if($started.run -cne $id -or !$started.primaryStopped -or !$started.controllerStopped){throw 'Injected fault unconfirmed; journal retained.'}
$collectPath=Join-Path $workspace ("runtime/service-cycle-collect-"+$id+'.ps1')
$collect=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Collect-PhysicalCycle.ps1')).Replace('__ROOT__',$remote).Replace('__ID__',$id).Replace('__TASK__',$task).Replace('__HELPER__',$hash)
[IO.File]::WriteAllText($collectPath,$collect,[Text.UTF8Encoding]::new($true));$collectHash=(Get-FileHash -LiteralPath $collectPath).Hash
Invoke-PhysicalScp $collectPath ("citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/$folder/collect.ps1")
$collectWrapperPath=Join-Path $workspace ("runtime/service-cycle-system-collect-"+$id+'.ps1');Write-SystemWrapper $collectWrapperPath $remote 'collect.ps1' 'system-collect-result.json';$collectWrapperHash=(Get-FileHash -LiteralPath $collectWrapperPath).Hash
Invoke-PhysicalScp $collectWrapperPath ("citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/$folder/system-collect.ps1")
$collectEnvelope=Invoke-SystemScript $remote 'system-collect.ps1' 'system-collect-result.json' ($task+'-SystemCollect') 120
$result=([string]$collectEnvelope.output).Trim()|ConvertFrom-Json
if((Invoke-PhysicalRm "if((Get-FileHash -LiteralPath '$remote\collect.ps1').Hash -ne '$collectHash'){throw 'Collect script differs'};if((Get-FileHash -LiteralPath '$remote\system-collect.ps1').Hash -ne '$collectWrapperHash'){throw 'SYSTEM wrapper differs'};'confirmed'" 40).Trim() -ne 'confirmed'){throw 'Collect scripts differ.'}
if($result.run -cne $id -or !$result.success){throw 'Recovery unconfirmed; journal retained.'}
$result|Add-Member -NotePropertyName controllerTermination -NotePropertyValue $started.controllerTermination
$result|Add-Member -NotePropertyName transport -NotePropertyValue $transport
$resultPath=Join-Path $workspace ('runtime/service-cycle-result-'+$id+'.json');$result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $resultPath -Encoding UTF8
$entry.state='VerifiedRetained';$entry.resultSha256=(Get-FileHash -LiteralPath $resultPath).Hash;$update=$journal+'.'+[guid]::NewGuid().ToString('N')+'.new'
try{$stream=[IO.File]::Open($update,'CreateNew','Write','Read');try{$bytes=[Text.Encoding]::UTF8.GetBytes(($entry|ConvertTo-Json -Compress));$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()};[IO.File]::Replace($update,$journal,[NullString]::Value)}finally{if(Test-Path -LiteralPath $update){Remove-Item -LiteralPath $update -Force}}
Remove-Item -LiteralPath $startPath,$collectPath,$startWrapperPath,$collectWrapperPath -Force
$result
