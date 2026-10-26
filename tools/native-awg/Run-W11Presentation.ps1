param([ValidateSet(200,1000)][int[]]$Rows=@(200,1000),[ValidateRange(1,30)][int]$Seconds=30)
$ErrorActionPreference='Stop';$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$run=[guid]::NewGuid().ToString('N')
. (Join-Path $root 'runtime/vm-lab/W11-Calls.ps1')
try{
 foreach($name in @('RoutePilot.exe','RoutePilot.dll','RoutePilot.deps.json','RoutePilot.runtimeconfig.json')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root ('runtime/native-ui-clean-build/'+$name)),('C:\CitaLab\NativeAwg\NativeUiLab\'+$name))|Out-Null}
 $code=@'
$ErrorActionPreference='Stop';$report=@{success=$false;presentations=@()}
try{
 if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong guest'}
 foreach($rows in @(__ROWS__)){
  $target='C:\CitaLab\NativeAwg\Presentation-'+[guid]::NewGuid().ToString('N')
  New-Item -ItemType Directory -Path $target|Out-Null
  foreach($name in @('RoutePilot.exe','RoutePilot.dll','RoutePilot.deps.json','RoutePilot.runtimeconfig.json')){Copy-Item -LiteralPath (Join-Path 'C:\CitaLab\NativeAwg\NativeUiLab' $name) -Destination (Join-Path $target $name)}
  '# Presentation root marker; never executed.'|Set-Content (Join-Path $target 'SmartRouting.ps1') -Encoding UTF8
  $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $target 'RoutePilot.exe'));$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.Arguments='--benchmark-ui';$info.EnvironmentVariables['CITA_PRESENTATION_ROWS']=[string]$rows;$info.EnvironmentVariables['CITA_PRESENTATION_SECONDS']='__SECONDS__'
  $process=[Diagnostics.Process]::Start($info);$sessionId=$process.SessionId
  try{if($sessionId -le 0){throw 'Interactive desktop measurement required'};if(-not $process.WaitForExit((2*(__SECONDS__+2)+20)*1000)){throw 'Presentation benchmark timeout'};$report.lastExitCode=$process.ExitCode;if($process.ExitCode -ne 0){throw 'Presentation benchmark failed'};$data=Get-Content (Join-Path $target 'logs/ui-resource-fixture.json') -Raw|ConvertFrom-Json;$report.presentations+=@{rows=$rows;sessionId=$sessionId;measurements=$data}}
  finally{if(-not $process.HasExited){$process.Kill();$process.WaitForExit()};$process.Dispose()}
 }
 $report.success=$true
}catch{$report.errorType=$_.Exception.GetType().Name;$report.failure=$_.Exception.Message;$report.failedLine=$_.InvocationInfo.ScriptLineNumber}
finally{$report|ConvertTo-Json -Depth 8|Set-Content C:\CitaLab\NativeAwg\presentation-result.json -Encoding UTF8}
'@
 $script=$code.Replace('__ROWS__',($Rows -join ',')).Replace('__SECONDS__',[string]$Seconds)
 W11-Call @('runProgramInGuest',$labVmx,'-interactive','C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe','-NoProfile','-NonInteractive','-WindowStyle','Hidden','-ExecutionPolicy','Bypass','-EncodedCommand',([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))))|Out-Null
 $file=Join-Path $root ('runtime/native-presentation-'+$run+'.json');W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\presentation-result.json',$file)|Out-Null
 $report=Get-Content $file -Raw|ConvertFrom-Json;$report|Add-Member -NotePropertyName appSha256 -NotePropertyValue (Get-FileHash (Join-Path $root 'runtime/native-ui-clean-build/RoutePilot.dll')).Hash
 $report|ConvertTo-Json -Depth 8|Set-Content $file -Encoding UTF8;$report|ConvertTo-Json -Depth 8 -Compress|Write-Output;if(-not $report.success){throw 'W11 presentation measurement incomplete'}
}finally{$labCredential=$null;$labVmCredential=$null}
