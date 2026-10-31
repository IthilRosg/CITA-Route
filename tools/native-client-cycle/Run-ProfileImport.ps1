[CmdletBinding()]
param([switch]$ViaRadmin,[ValidatePattern('^[A-F0-9]{64}$')][string]$ExpectedAgentSha='97506113A245233BB7310DDCDF6CB80B5FD4C62AEC39AF9D0EA49A5FC1E3E764')
$ErrorActionPreference='Stop';$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
function Invoke-PhysicalRm([string]$Code,[int]$TimeoutSeconds=30){Invoke-BoundedRm -Code $Code -TimeoutSeconds $TimeoutSeconds -ViaRadmin:$ViaRadmin}
function Invoke-PhysicalScp([string]$Source,[string]$Destination,[int]$TimeoutSeconds=45){Invoke-BoundedScp -Source $Source -Destination $Destination -TimeoutSeconds $TimeoutSeconds -ViaRadmin:$ViaRadmin}
$journal=Join-Path $workspace 'runtime/native-profile-import-active.json'
if(Test-Path -LiteralPath $journal){throw 'Reconcile the existing import test before another run.'}
$id=[guid]::NewGuid().ToString('N');$remote='C:\Program Files\RoutePilot\Agent\native-profile-test-'+$id
$testSource=Join-Path $workspace ('runtime/native-profile-test-'+$id+'.ps1');(Get-Content -Raw (Join-Path $PSScriptRoot 'Test-ProfileImport.ps1')).Replace('__AGENT__',$ExpectedAgentSha)|Set-Content -Encoding utf8 $testSource
$paths=[ordered]@{'Test-ProfileImport.ps1'=$testSource;'NativeUpgradeTransaction.ps1'=(Join-Path $workspace 'installer/NativeUpgradeTransaction.ps1');'NativeUpgradeService.ps1'=(Join-Path $workspace 'installer/NativeUpgradeService.ps1')}
$pins=@{};foreach($name in $paths.Keys){$pins[$name]=(Get-FileHash -LiteralPath $paths[$name]).Hash}
$entry=@{Run=$id;Root=$remote;Pins=$pins;State='MayCreate';Utc=[datetime]::UtcNow.ToString('O')}
$f=[IO.File]::Open($journal,'CreateNew','Write','Read');try{$b=[Text.Encoding]::UTF8.GetBytes(($entry|ConvertTo-Json -Depth 5 -Compress));$f.Write($b,0,$b.Length);$f.Flush($true)}finally{$f.Dispose()}
$recipe=Join-Path $workspace 'tools/native-control-probe/Test-PhysicalTransfer.ps1'
if((Get-FileHash -LiteralPath $recipe).Hash -cne '42CB3AA03261C76905617CAE5EBFAFE3F927229E5672B3278B4EFC9A6758BECE'){throw 'Protected directory recipe changed.'}
$create=[regex]::Match([IO.File]::ReadAllText($recipe),'(?s)\$create=@''\r?\n(.*?)\r?\n''@').Groups[1].Value
if((Invoke-PhysicalRm $create.Replace('__DIR__',$remote)).Trim() -cne 'ready'){throw 'Import test directory unconfirmed.'}
foreach($name in $paths.Keys){Invoke-PhysicalScp $paths[$name] ('citastation@26.68.227.253:'+($remote.Replace('\','/')+'/'+$name));if((Get-FileHash -LiteralPath $paths[$name]).Hash -cne $pins[$name]){throw 'Import source changed during transfer.'}}
$bootstrap=@'
$ErrorActionPreference='Stop';Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force;$r='__ROOT__';$pins='__PINS__'|ConvertFrom-Json;$handles=[Collections.Generic.List[object]]::new()
function Protect($p){$item=Get-Item -LiteralPath $p -Force;if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Import linked path refused.'};$acl=if($item.PSIsContainer){[Security.AccessControl.DirectorySecurity]::new()}else{[Security.AccessControl.FileSecurity]::new()};$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'));$acl.SetAccessRuleProtection($true,$false);$inherit=if($item.PSIsContainer){'ContainerInherit,ObjectInherit'}else{'None'};foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl',$inherit,'None','Allow'))};Set-Acl -LiteralPath $p -AclObject $acl}
try{Protect $r;foreach($pin in $pins.PSObject.Properties){$p=Join-Path $r $pin.Name;Protect $p;$handles.Add([IO.File]::Open($p,'Open','Read','Read'));if((Get-FileHash -LiteralPath $p).Hash -cne $pin.Value){throw 'Import source pin differs.'}};& (Join-Path $r 'Test-ProfileImport.ps1') -RunRoot $r}finally{foreach($handle in $handles){$handle.Dispose()}}
'@
$code=$bootstrap.Replace('__ROOT__',$remote).Replace('__PINS__',($pins|ConvertTo-Json -Compress))
Invoke-PhysicalRm $code 180
'Import worker returned; inspect its saved result before cleanup.'
