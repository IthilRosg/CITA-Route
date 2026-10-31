$ErrorActionPreference='Stop'
# Exercise the actual protocol/command selection functions without executing
# the production entry, SCM, IPC, or a supervisor worker.
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Recover.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Recovery entry parse failed.'}
foreach($name in @('Get-NativeRecoveryProtocol','Get-NativeRecoveryStatusCommand')){
 $matches=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true))
 if($matches.Count -ne 1){throw 'Recovery selection function missing.'}
 . ([scriptblock]::Create($matches[0].Extent.Text))
}
function Reject([scriptblock]$action){$failed=$false;try{& $action}catch{$failed=$true};if(!$failed){throw 'Unsafe protocol was accepted.'}}
$old=[pscustomobject]@{Success=$true;Native=[pscustomobject]@{setupProtocol=1}}
$new=[pscustomobject]@{Success=$true;Native=[pscustomobject]@{maintenanceGateProtocol=1;maintenanceValidationProtocol=1}}
if((Get-NativeRecoveryProtocol $old) -cne 'Normal' -or (Get-NativeRecoveryStatusCommand 'Normal') -cne 'native-status'){throw 'Legacy recovery protocol differs.'}
if((Get-NativeRecoveryProtocol $new) -cne 'Maintenance' -or (Get-NativeRecoveryStatusCommand 'Maintenance') -cne 'native-maintenance-status'){throw 'Maintenance recovery protocol differs.'}
foreach($bad in @(
 [pscustomobject]@{Success=$false;Native=[pscustomobject]@{}},
 [pscustomobject]@{Success=$true;Native=[pscustomobject]@{maintenanceGateProtocol=1}},
 [pscustomobject]@{Success=$true;Native=[pscustomobject]@{maintenanceGateProtocol='1';maintenanceValidationProtocol=1}},
 [pscustomobject]@{Success=$true;Native=[pscustomobject]@{maintenanceGateProtocol=1;maintenanceValidationProtocol=2}}
)){Reject {Get-NativeRecoveryProtocol $bad}}
Reject {Get-NativeRecoveryStatusCommand 'unexpected'}
'PASS adaptive recovery protocol model; no SCM or IPC'
