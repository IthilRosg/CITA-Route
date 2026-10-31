param([string]$RunRoot,[ValidateSet('primary','backup')][string]$Role)
$ErrorActionPreference='Stop'
$runScript=[IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$allowed=[IO.Path]::GetDirectoryName($runScript)
if((Split-Path $allowed -Leaf) -ne 'physical-installer-guard-tests' -or (Split-Path (Split-Path $allowed) -Leaf) -ne 'runtime'){throw 'Scratch script location refused'}
$run=[IO.Path]::GetFullPath($RunRoot).TrimEnd('\')
if($run -ine $runScript -or [IO.Path]::GetDirectoryName($run) -ine $allowed -or (Split-Path $run -Leaf) -cnotmatch '^[a-f0-9]{32}$'){throw 'Scratch root refused'}
$sid='S-1-5-21-1-2-3-1001';$root=Join-Path $run 'client';$worker=Join-Path $run 'RestoreWorker.ps1';$transaction=Join-Path $run 'InstallationTransaction.ps1'
if(!$Role){throw 'Scratch watcher role missing'}
[IO.File]::WriteAllText((Join-Path $run ('helper-'+$Role+'.started')),[string]$PID)
$env:CITA_SCRATCH_ROLE=$Role
. ([ScriptBlock]::Create([IO.File]::ReadAllText($worker)))
$result=Invoke-GuardedPublicRestore $root $sid $run $transaction ('0'*64) ('1'*64) ('2'*32) ('3'*64) (Split-Path $run -Leaf)
[IO.File]::WriteAllText((Join-Path $run ('helper-'+$Role+'.done')),'done')
$result|ConvertTo-Json -Compress
