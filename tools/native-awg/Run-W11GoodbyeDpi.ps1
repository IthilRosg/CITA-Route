param([switch]$Media)
$ErrorActionPreference='Stop';$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$run=[guid]::NewGuid().ToString('N')
$archive=Join-Path $root 'downloads\goodbyedpi-0.2.3rc3-2.zip';if((Get-FileHash -LiteralPath $archive).Hash -ne '37F96B32D050DADCC930A639EBA68E1CCD57ED5C04A5F77DFCA908F01905A4C5'){throw 'GoodbyeDPI archive differs from pinned upstream artifact'}
$source=Join-Path $root 'runtime\goodbyedpi-official\goodbyedpi-0.2.3rc3-2\x86_64'
. (Join-Path $root 'runtime\vm-lab\W11-Calls.ps1')
try{
 W11-Code 'New-Item -ItemType Directory -Path C:\CitaLab\GoodbyeDpi -Force|Out-Null;exit 0'|Out-Null
 foreach($name in @('goodbyedpi.exe','WinDivert.dll','WinDivert64.sys')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $source $name),('C:\CitaLab\GoodbyeDpi\'+$name))|Out-Null}
 W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root 'runtime\dpi-compare-lab.exe'),'C:\CitaLab\GoodbyeDpi\dpi-compare-lab.exe')|Out-Null
 W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $PSScriptRoot 'Run-GuestGoodbyeDpi.ps1'),'C:\CitaLab\GoodbyeDpi\Run.ps1')|Out-Null
 if($Media){W11-Code 'New-Item -ItemType Directory -Path C:\CitaLab\GoodbyeDpi\AgentBackend -Force|Out-Null;exit 0'|Out-Null;foreach($name in @('Cita.NativeAgentLab.exe','Cita.NativeAgentLab.dll','Cita.NativeAgentLab.deps.json','Cita.NativeAgentLab.runtimeconfig.json')){W11-Call @('copyFileFromHostToGuest',$labVmx,(Join-Path $root ('runtime\native-agent-dpi-lab\'+$name)),('C:\CitaLab\GoodbyeDpi\AgentBackend\'+$name))|Out-Null}}
 $flag=if($Media){' -Media'}else{''};W11-Code ('& C:\CitaLab\GoodbyeDpi\Run.ps1 -Run '+$run+$flag+';exit 0')|Out-Null
 $file=Join-Path $root ('runtime\goodbyedpi-comparison-'+$run+'.json');W11-Call @('copyFileFromGuestToHost',$labVmx,'C:\CitaLab\GoodbyeDpi\result.json',$file)|Out-Null
 $result=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json;if($result.run -ne $run){throw 'Stale GoodbyeDPI result'}
 $matched=$result.helperSha256 -eq (Get-FileHash (Join-Path $root 'runtime\dpi-compare-lab.exe')).Hash -and $result.executableSha256 -eq (Get-FileHash (Join-Path $source 'goodbyedpi.exe')).Hash -and $result.driverSha256 -eq (Get-FileHash (Join-Path $source 'WinDivert64.sys')).Hash
 if($Media){$matched=$matched -and $result.controllerSha256 -eq (Get-FileHash (Join-Path $root 'runtime\native-agent-dpi-lab\Cita.NativeAgentLab.dll')).Hash}
 $result|Add-Member -NotePropertyName hashesMatched -NotePropertyValue $matched;$result|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $file -Encoding UTF8
 if($Media){
  $brief=@();foreach($case in $result.cases){$pages=@();foreach($page in $case.media.results){$pages+=@{page=$page.page;elapsedMs=$page.elapsedMs;auditFailure=$page.auditFailure;ready=$page.dom.ready;video=$page.dom.video}};$brief+=@{mode=$case.mode;pages=$pages}}
  @{success=$result.success;cleanupConfirmed=$result.cleanupConfirmed;hashesMatched=$matched;resultFile=$file;cases=$brief}|ConvertTo-Json -Depth 8 -Compress|Write-Output
 }else{$result|ConvertTo-Json -Depth 8|Write-Output};if(-not $result.success -or -not $result.cleanupConfirmed -or -not $matched){throw 'GoodbyeDPI comparison incomplete'}
}finally{$labCredential=$null;$labVmCredential=$null}
