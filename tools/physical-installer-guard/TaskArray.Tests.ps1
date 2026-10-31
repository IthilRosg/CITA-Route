$ErrorActionPreference='Stop'
$task='CitaInstallerProof';$backup=$task+'-Backup';$denied=$task+'-DeniedController'
$two=@($task,($task+'-Backup'));$three=@($task,($task+'-Backup'),($task+'-DeniedController'))
if($two.Count -ne 2 -or $two[0] -ne $task -or $two[1] -ne $backup -or $three.Count -ne 3 -or $three[2] -ne $denied){throw 'PowerShell task array concatenation differs.'}
foreach($name in @('Run-PhysicalScratch.ps1','Reconcile-PhysicalScratch.ps1','Run-PhysicalInstall.ps1','Reconcile-PhysicalInstall.ps1')){
 $text=[IO.File]::ReadAllText((Join-Path $PSScriptRoot $name))
 if($text -match '\$task\s*,\s*\$task\s*\+'){throw ('Unparenthesized task concatenation: '+$name)}
}
'PASS two and three task identities; no unparenthesized task concatenation in runners.'
