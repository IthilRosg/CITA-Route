$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$run=[guid]::NewGuid().ToString('N')
. (Join-Path $root 'runtime/vm-lab/W11-Calls.ps1')
try{
 W11-Call @('start',$labVmx,'nogui')|Out-Null
 foreach($pair in @(@('runtime/native-awg-lab.exe','native-awg-lab.exe'),@('runtime/native-core-lab.exe','native-core-lab.exe'),@('tools/native-awg/Run-GuestTrust.ps1','Run-GuestTrust.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
 foreach($name in @('Cita.NativeAgentLab.exe','Cita.NativeAgentLab.dll','Cita.NativeAgentLab.deps.json','Cita.NativeAgentLab.runtimeconfig.json')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root ('runtime/native-agent-backend-lab/'+$name)),('C:\CitaLab\NativeAwg\AgentBackend\'+$name))|Out-Null}
 W11-Code 'if(Test-Path C:\CitaLab\NativeAwg\native-trust-result.json){Remove-Item -LiteralPath C:\CitaLab\NativeAwg\native-trust-result.json -Force};& C:\CitaLab\NativeAwg\Run-GuestTrust.ps1;exit 0'|Out-Null
 $file=Join-Path $root ('runtime/native-awg-trust-'+$run+'.json');W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\native-trust-result.json',$file)|Out-Null
 $result=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json;$result|ConvertTo-Json -Depth 5 -Compress|Write-Output
 if(-not $result.success -or -not $result.cleaned){throw 'Protected component acceptance failed'}
}finally{$labCredential=$null;$labVmCredential=$null}
