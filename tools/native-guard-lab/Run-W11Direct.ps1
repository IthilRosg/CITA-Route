param([switch]$Policy,[switch]$Automatic,[switch]$Performance,[switch]$Coverage,[string]$AgentFolder='runtime/native-agent-backend-lab')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$run=[guid]::NewGuid().ToString('N')
$agentRoot=[IO.Path]::GetFullPath((Join-Path $root $AgentFolder));if(-not $agentRoot.StartsWith((Join-Path $root 'runtime')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected controller test directory'}
. (Join-Path $root 'runtime/vm-lab/W11-Calls.ps1')
try {
 W11-Call @('start',$labVmx,'nogui')|Out-Null
 foreach($pair in @(@('runtime/native-guard-lab.exe','native-guard-lab.exe'),@('runtime/native-core-lab.exe','native-core-lab.exe'),@('runtime/native-awg-lab.exe','native-awg-lab.exe'),@('tools/native-guard-lab/Run-GuestDirect.ps1','Run-GuestDirect.ps1'))){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root $pair[0]),('C:\CitaLab\NativeAwg\'+$pair[1]))|Out-Null}
 foreach($name in @('Cita.NativeAgentLab.exe','Cita.NativeAgentLab.dll','Cita.NativeAgentLab.deps.json','Cita.NativeAgentLab.runtimeconfig.json')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $agentRoot $name),('C:\CitaLab\NativeAwg\AgentBackend\'+$name))|Out-Null}
 $mode=if($Coverage){' -Coverage'}elseif($Performance){' -Performance'}elseif($Automatic){' -Automatic'}elseif($Policy){' -Policy'}else{''};W11-Code ('& C:\CitaLab\NativeAwg\Run-GuestDirect.ps1 -Run '+$run+$mode+';exit 0')|Out-Null
 $prefix=if($Coverage){'native-coverage-'}elseif($Performance){'native-performance-'}elseif($Automatic){'native-policy-auto-'}elseif($Policy){'native-policy-session-'}else{'native-direct-'}
 $file=Join-Path $root ('runtime/'+$prefix+$run+'.json');W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\NativeAwg\native-direct-result.json',$file)|Out-Null
 $result=Get-Content $file -Raw|ConvertFrom-Json;$matched=$result.guardSha256 -eq (Get-FileHash (Join-Path $root 'runtime/native-guard-lab.exe')).Hash -and $result.controllerSha256 -eq (Get-FileHash (Join-Path $agentRoot 'Cita.NativeAgentLab.dll')).Hash -and $result.coreSha256 -eq (Get-FileHash (Join-Path $root 'runtime/native-core-lab.exe')).Hash
 $result|Add-Member -NotePropertyName hashesMatched -NotePropertyValue $matched;$result|ConvertTo-Json -Depth 5|Set-Content $file -Encoding UTF8;$result|ConvertTo-Json -Depth 5 -Compress|Write-Output
 if(-not $result.success -or -not $result.independentCleanupConfirmed -or -not $matched){throw 'Native Direct scope acceptance incomplete'}
}finally{$labCredential=$null;$labVmCredential=$null}
