param([switch]$CrashController,[switch]$ResetLink)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$run=[guid]::NewGuid().ToString('N')
. (Join-Path $root 'runtime/vm-lab/W11-Calls.ps1')
if($CrashController -and $ResetLink){throw 'Use separate bounded scenarios'}
try{
 W11-Call @('start',$labVmx,'nogui')|Out-Null
 foreach($pair in @(@('runtime/native-guard-lab.exe','native-guard-lab.exe'),@('tools/native-guard-lab/Run-Guest.ps1','Run-GuestWfp.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
 if($CrashController){
 W11-Code 'if(Test-Path C:\CitaLab\NativeAwg\native-wfp-result.json){Remove-Item -LiteralPath C:\CitaLab\NativeAwg\native-wfp-result.json -Force};$p=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\CitaLab\NativeAwg\Run-GuestWfp.ps1 -CrashController" -PassThru;$null=$p.Handle;if(-not $p.WaitForExit(60000)){throw "Crash fixture deadline"};$r=Get-Content C:\CitaLab\NativeAwg\native-wfp-result.json -Raw|ConvertFrom-Json;if(-not $r.intentionallyInterrupted -or $r.fixtureRun -notmatch "^[a-f0-9]{32}$"){throw "No intentional crash marker"};$f="C:\CitaLab\NativeAwg\native-wfp-rescue-"+$r.fixtureRun+".json";for($i=0;$i -lt 120;$i++){if(Test-Path -LiteralPath $f){break};Start-Sleep -Milliseconds 500};$rescue=Get-Content -LiteralPath $f -Raw|ConvertFrom-Json;$r|Add-Member -NotePropertyName rescue -NotePropertyValue $rescue;$c=[Net.Sockets.TcpClient]::new();try{$t=$c.ConnectAsync("1.1.1.1",443);$restored=[bool]($t.Wait(2500) -and $c.Connected)}finally{$c.Dispose()};$r|Add-Member -NotePropertyName restoredAfterRescue -NotePropertyValue $restored;$r|Add-Member -NotePropertyName cleaned -NotePropertyValue $rescue.success;$r.success=[bool]($r.blockedV4 -and $r.blockedV6 -and $r.blockedUdpV6 -and $rescue.success -and $restored);$r|ConvertTo-Json -Depth 6|Set-Content C:\CitaLab\NativeAwg\native-wfp-result.json -Encoding UTF8;exit 0'|Out-Null
 }elseif($ResetLink){W11-Code 'if(Test-Path C:\CitaLab\NativeAwg\native-wfp-result.json){Remove-Item -LiteralPath C:\CitaLab\NativeAwg\native-wfp-result.json -Force};& C:\CitaLab\NativeAwg\Run-GuestWfp.ps1 -ResetLink;exit 0'|Out-Null
 }else{W11-Code 'if(Test-Path C:\CitaLab\NativeAwg\native-wfp-result.json){Remove-Item -LiteralPath C:\CitaLab\NativeAwg\native-wfp-result.json -Force};& C:\CitaLab\NativeAwg\Run-GuestWfp.ps1;exit 0'|Out-Null}
 $file=Join-Path $root ('runtime/native-wfp-'+$run+'.json');W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\native-wfp-result.json',$file)|Out-Null
 $result=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json;$matched=$result.imageSha256 -eq (Get-FileHash (Join-Path $root 'runtime/native-guard-lab.exe')).Hash
 $result|Add-Member -NotePropertyName hashesMatched -NotePropertyValue $matched;$result|Add-Member -NotePropertyName runnerSha256 -NotePropertyValue (Get-FileHash (Join-Path $PSScriptRoot 'Run-Guest.ps1')).Hash;$result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $file -Encoding UTF8;$result|ConvertTo-Json -Depth 5 -Compress|Write-Output
 if(-not $result.success -or -not $result.cleaned -or -not $matched){throw 'Persistent guard acceptance incomplete'}
}finally{$labCredential=$null;$labVmCredential=$null}
