param([Parameter(Mandatory=$true)][string]$RunRoot,[Parameter(Mandatory=$true)][ValidateSet('primary','backup')][string]$Role)
$ErrorActionPreference='Stop'
$expectedParent=Join-Path $env:ProgramFiles 'RoutePilot\Agent'
$run=[IO.Path]::GetFullPath($RunRoot).TrimEnd('\')
if([IO.Path]::GetDirectoryName($run) -ine $expectedParent -or (Split-Path $run -Leaf) -cnotmatch '^cita-installer-[a-f0-9]{32}$'){throw 'Physical recovery root refused'}
foreach($path in @($run,(Join-Path $run 'plan.json'),(Join-Path $run 'RestoreWorker.ps1'),(Join-Path $run 'InstallationTransaction.ps1'))){
 if((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Physical recovery link refused'}
}
$plan=[IO.File]::ReadAllText((Join-Path $run 'plan.json'))|ConvertFrom-Json
$retired=[IO.File]::ReadAllText((Join-Path $run ('retired-'+$Role+'.json')))|ConvertFrom-Json
if($plan.Schema -ne 1 -or $plan.Run -ne (Split-Path $run -Leaf).Substring(15) -or $retired.run -ne $plan.Run -or !$retired.controllerStopped -or $retired.jobActiveAfter -ne 0){throw 'Physical recovery retirement unconfirmed'}
$config=[IO.File]::ReadAllText((Join-Path $expectedParent 'agent-config.json'))|ConvertFrom-Json
$actual='C:\Users\CITASTATION\AppData\Local\Programs\CITA Route\client'
if($config.UserSid -ne $plan.UserSid -or [IO.Path]::GetFullPath($config.AppRoot).TrimEnd('\') -ine $actual -or $plan.AppRoot -ine $actual){throw 'Physical installation ownership differs'}
$target=if($plan.Mode -eq 'Scratch'){Join-Path $run 'scratch\client'}elseif($plan.Mode -eq 'Physical'){$actual}else{throw 'Physical mode refused'}
$worker=Join-Path $run 'RestoreWorker.ps1';$transaction=Join-Path $run 'InstallationTransaction.ps1'
$file=[IO.File]::Open($worker,'Open','Read','Read')
try{
 $sha=[Security.Cryptography.SHA256]::Create();try{$digest=([BitConverter]::ToString($sha.ComputeHash($file))).Replace('-','')}finally{$sha.Dispose()}
 if($digest -ne $plan.WorkerSha256){throw 'Recovery worker digest differs'}
 $file.Position=0;$reader=[IO.StreamReader]::new($file);try{$source=$reader.ReadToEnd()}finally{$reader.Dispose()}
 . ([ScriptBlock]::Create($source))
 $result=Invoke-GuardedPublicRestore $target $plan.UserSid $run $transaction $plan.PayloadManifestSha256 $plan.PreviousManifestSha256 $plan.PreviousJournalId $plan.PreviousStateSha256 $plan.Run
 $result|ConvertTo-Json -Compress
}finally{$file.Dispose()}
