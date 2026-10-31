$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$expected=Get-Content (Join-Path $root 'runtime/zapret-preflight-transfer.json') -Raw|ConvertFrom-Json
. (Join-Path $root 'runtime/invoke-rm-lab.ps1')
$code=@'
$ErrorActionPreference='Stop';$root='C:\CITA-Lab\ZapretPreflight'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong physical station'}
if((Get-FileHash "$root\zapret-module-lab.zip").Hash -ne '__ZIP__' -or (Get-FileHash "$root\Cita.ZapretPreflight.exe").Hash -ne '__TOOL__'){throw 'Transfer digest mismatch'}
Expand-Archive -LiteralPath "$root\zapret-module-lab.zip" -DestinationPath "$root\module" -Force
$signature=Get-AuthenticodeSignature -LiteralPath "$root\module\WinDivert64.sys"
if($signature.Status -ne 'Valid'){throw ('Driver signature rejected: '+$signature.Status)}
$before=@(Get-CimInstance Win32_SystemDriver|Where-Object {$_.Name -like 'WinDivert*'}|Select-Object Name,State,PathName)
if($before.Count){throw 'Existing WinDivert driver: scoped preflight must not change another owner'}
$info=New-Object Diagnostics.ProcessStartInfo;$info.FileName="$root\Cita.ZapretPreflight.exe";$info.Arguments="$root\module";$info.WorkingDirectory=$root;$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
$p=$null;$exit=-1;$text='';$err='';$observed=@()
try
{
 $p=[Diagnostics.Process]::Start($info);$output=$p.StandardOutput.ReadToEndAsync();$errorOutput=$p.StandardError.ReadToEndAsync()
 if(!$p.WaitForExit(60000)){$p.Kill();throw 'Preflight deadline'};$exit=$p.ExitCode;$text=$output.Result;$err=$errorOutput.Result
}
finally
{
 if($p){$p.Dispose()}
 $observed=@(Get-CimInstance Win32_SystemDriver|Where-Object {$_.Name -like 'WinDivert*'}|Select-Object Name,State,PathName)
 # winws2 initializes Windows WinDivert even with --intercept=0. Do not treat
 # that flag as a side-effect-free parameter check. Retire ONLY our new path.
 $owned=Get-CimInstance Win32_SystemDriver -Filter "Name='WinDivert'"
 if($owned)
 {
  if($owned.PathName -cne '\??\C:\CITA-Lab\ZapretPreflight\module\WinDivert64.sys'){throw 'Foreign WinDivert path; no driver cleanup attempted'}
  if(@(Get-CimInstance Win32_Process|Where-Object {$_.Name -in @('winws.exe','winws2.exe','Cita.ZapretPreflight.exe')}).Count){throw 'Worker still active; driver retirement refused'}
  & sc.exe stop WinDivert|Out-Null;if($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1062){throw 'Owned driver stop failed'}
  if(Get-CimInstance Win32_SystemDriver -Filter "Name='WinDivert'"){& sc.exe delete WinDivert|Out-Null;if($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1060){throw 'Owned driver cleanup failed'}}
 }
}
$after=@(Get-CimInstance Win32_SystemDriver|Where-Object {$_.Name -like 'WinDivert*'}|Select-Object Name,State,PathName)
$result=@{exit=$exit;driverSignature=$signature.Status.ToString();driverStateChangedDuringCheck=($observed.Count -ne 0);cleanupConfirmed=($after.Count -eq 0);before=$before;observed=$observed;after=$after;output=$text;error=$err}
$result|ConvertTo-Json -Depth 6|Set-Content "$root\preflight-result.json" -Encoding UTF8
$result|ConvertTo-Json -Depth 6
'@
$code=$code.Replace('__ZIP__',$expected.zip).Replace('__TOOL__',$expected.tool)
Invoke-Rm $code|Set-Content (Join-Path $root 'runtime/zapret-physical-preflight.json') -Encoding utf8
Get-Content (Join-Path $root 'runtime/zapret-physical-preflight.json')
