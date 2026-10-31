param([switch]$ArmOnly,[switch]$RoutingSample,[switch]$GuiAutoconnect,[switch]$CoreCrash,[switch]$ViaRadmin,[ValidatePattern('^[A-F0-9]{64}$')][string]$ExpectedAgentSha='97506113A245233BB7310DDCDF6CB80B5FD4C62AEC39AF9D0EA49A5FC1E3E764',[ValidatePattern('^[A-F0-9]{64}$')][string]$ExpectedAppSha='5350FAEED66D2A4AA0E709FCC2D9C197C38CFE1E3A161BC28CE9A814D3208795',[ValidatePattern('^$|^[A-F0-9]{64}$')][string]$ExpectedCoreSha='')
$ErrorActionPreference='Stop';$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if($GuiAutoconnect -and ($ArmOnly -or $RoutingSample)){throw 'GUI acceptance must run separately.'}
if($CoreCrash -and ($ArmOnly -or $RoutingSample -or $GuiAutoconnect -or !$ExpectedCoreSha)){throw 'Core crash requires a separate cycle and an explicit core pin.'}
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
function Invoke-PhysicalRm([string]$Code,[int]$TimeoutSeconds=30){Invoke-BoundedRm -Code $Code -TimeoutSeconds $TimeoutSeconds -ViaRadmin:$ViaRadmin}
function Invoke-PhysicalScp([string]$Source,[string]$Destination,[int]$TimeoutSeconds=45){Invoke-BoundedScp -Source $Source -Destination $Destination -TimeoutSeconds $TimeoutSeconds -ViaRadmin:$ViaRadmin}
$journal=Join-Path $workspace 'runtime/native-client-cycle-active.json';if(Test-Path -LiteralPath $journal){throw 'Existing client cycle needs reconciliation.'}
$paths=[ordered]@{'Controller.ps1'=(Join-Path $PSScriptRoot 'Controller.ps1');'Rescue.ps1'=(Join-Path $PSScriptRoot 'Rescue.ps1');'NativeUpgradeTransaction.ps1'=(Join-Path $workspace 'installer/NativeUpgradeTransaction.ps1');'NativeUpgradeService.ps1'=(Join-Path $workspace 'installer/NativeUpgradeService.ps1')}
if($GuiAutoconnect){$paths['Gui.ps1']=Join-Path $PSScriptRoot 'Gui.ps1'}
$pins=@{};foreach($name in $paths.Keys){$pins[$name]=(Get-FileHash -LiteralPath $paths[$name]).Hash}
# The remote tasks use Windows PowerShell 5.1, even when this launcher runs
# in PowerShell 7. Reject unsupported syntax before creating remote state.
$parseCode='$ErrorActionPreference=''Stop'';'+(($paths.Values|ForEach-Object {'$t=$null;$e=$null;$null=[Management.Automation.Language.Parser]::ParseFile('''+$_.Replace("'","''")+''',[ref]$t,[ref]$e);if($e.Count){throw ''Remote task source is not Windows PowerShell compatible.''}'}) -join ';')
$parseInfo=[Diagnostics.ProcessStartInfo]::new();$parseInfo.FileName=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';$parseInfo.UseShellExecute=$false;$parseInfo.CreateNoWindow=$true;$parseInfo.RedirectStandardOutput=$true;$parseInfo.RedirectStandardError=$true
foreach($arg in @('-NoProfile','-NonInteractive','-EncodedCommand',[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($parseCode)))){$parseInfo.ArgumentList.Add($arg)}
$parser=[Diagnostics.Process]::Start($parseInfo)
try{if(!$parser.WaitForExit(10000)){$parser.Kill();throw 'Remote source syntax check timed out.'};if($parser.ExitCode -ne 0){throw 'Remote task source is not Windows PowerShell compatible.'}}finally{$parser.Dispose()}
$recipe=Join-Path $workspace 'tools/native-control-probe/Test-PhysicalTransfer.ps1'
if((Get-FileHash -LiteralPath $recipe).Hash -cne '42CB3AA03261C76905617CAE5EBFAFE3F927229E5672B3278B4EFC9A6758BECE'){throw 'Directory recipe differs.'}
$create=[regex]::Match([IO.File]::ReadAllText($recipe),'(?s)\$create=@''\r?\n(.*?)\r?\n''@').Groups[1].Value
$id=[guid]::NewGuid().ToString('N');$remote='C:\Program Files\RoutePilot\Agent\native-cycle-'+$id
$entry=@{Run=$id;Root=$remote;State='MayCreate';ArmOnly=[bool]$ArmOnly;Pins=$pins;Utc=[datetime]::UtcNow.ToString('O')};$f=[IO.File]::Open($journal,'CreateNew','Write','Read');try{$b=[Text.Encoding]::UTF8.GetBytes(($entry|ConvertTo-Json -Depth 5 -Compress));$f.Write($b,0,$b.Length);$f.Flush($true)}finally{$f.Dispose()}
if((Invoke-PhysicalRm $create.Replace('__DIR__',$remote)).Trim() -cne 'ready'){throw 'Cycle directory unconfirmed.'}
foreach($name in $paths.Keys){Invoke-PhysicalScp $paths[$name] ('citastation@26.68.227.253:'+($remote.Replace('\','/')+'/'+$name))}
foreach($name in $paths.Keys){if((Get-FileHash -LiteralPath $paths[$name]).Hash -cne $pins[$name]){throw 'Cycle source changed during copy.'}}
$armLiteral=if($ArmOnly){'$true'}else{'$false'};$sampleLiteral=if($RoutingSample){'$true'}else{'$false'}
$guiLiteral=if($GuiAutoconnect){'$true'}else{'$false'}
$crashLiteral=if($CoreCrash){'$true'}else{'$false'}
$bootstrap=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Start.ps1')).Replace('__ROOT__',$remote).Replace('__ID__',$id).Replace('__PINS__',($pins|ConvertTo-Json -Compress)).Replace('__AGENT__',$ExpectedAgentSha).Replace('__APP__',$ExpectedAppSha).Replace('__CORE__',$ExpectedCoreSha).Replace('__CORE_CRASH__',$crashLiteral).Replace('__ARM_ONLY__',$armLiteral).Replace('__ROUTING_SAMPLE__',$sampleLiteral).Replace('__GUI__',$guiLiteral)
$file=Join-Path $workspace ('runtime/native-cycle-start-'+$id+'.ps1');[IO.File]::WriteAllText($file,$bootstrap,[Text.UTF8Encoding]::new($true));$sha=(Get-FileHash -LiteralPath $file).Hash
Invoke-PhysicalScp $file ('citastation@26.68.227.253:'+($remote.Replace('\','/')+'/Start.ps1'))
$code="if((Get-FileHash -LiteralPath '$remote\Start.ps1').Hash -cne '$sha'){throw 'Cycle bootstrap differs.'};& ([ScriptBlock]::Create([IO.File]::ReadAllText('$remote\Start.ps1')))"
$result=Invoke-PhysicalRm $code 50;$result|Set-Content -LiteralPath (Join-Path $workspace ('runtime/native-client-cycle-start-'+$id+'.json'));$result
