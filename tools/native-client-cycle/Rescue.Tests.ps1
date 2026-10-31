$ErrorActionPreference='Stop'
$file=Join-Path $PSScriptRoot 'Rescue.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($file,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Rescue script parse failed.'}
$functions=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Get-CycleAction'},$true))
if($functions.Count -ne 1){throw 'Cycle selection function missing.'}
. ([scriptblock]::Create($functions[0].Extent.Text))
$a='a'*32;$b='b'*32;$profile='A'*64;$plan=[pscustomobject]@{ConnectionId=$a;InitialGeneration=32}
function State([string]$id,[bool]$desired,[long]$generation,[string]$profileId){return [pscustomobject]@{Success=$true;Native=[pscustomobject]@{connectionId=$id;desired=$desired;generation=$generation;profileId=$profileId}}}
if((Get-CycleAction $plan (State $a $true 33 $profile) $profile) -cne 'Stop'){throw 'Owned connection was not selected.'}
if((Get-CycleAction $plan (State $a $true 40 $profile) $profile) -cne 'Stop'){throw 'Owned recovered connection was not selected.'}
if((Get-CycleAction $plan (State $a $false 40 $profile) $profile) -cne 'Poll'){throw 'Stopped owned connection was not polled.'}
if((Get-CycleAction $plan (State $b $true 33 $profile) $profile) -cne 'NoSession'){throw 'Foreign connection was selected.'}
if((Get-CycleAction $plan (State '' $false 32 $profile) $profile) -cne 'NoSession'){throw 'Unstarted connection was selected.'}
if((Get-CycleAction $plan (State $a $true 32 $profile) $profile) -cne 'GenerationNotAdvanced'){throw 'Stale generation was selected.'}
if((Get-CycleAction $plan (State $a $true 33 ('B'*64)) $profile) -cne 'ProfileChanged'){throw 'Foreign profile was selected.'}
$failed=$false;try{Get-CycleAction $plan ([pscustomobject]@{Success='true';Native=$null}) ''}catch{$failed=$true};if(!$failed){throw 'Malformed reply was accepted.'}
'PASS exact lineage selection model; no service, task, network or remote execution'
