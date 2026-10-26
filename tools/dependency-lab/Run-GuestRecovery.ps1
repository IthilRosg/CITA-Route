$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong guest'}
$root='C:\CitaLab\PlatformNext';$helper='C:\CitaLab\DependencyLab\Cita.DependencyLab.exe'
$report=@{success=$false;phase='Prepare';startedUtc=[DateTime]::UtcNow.ToString('o')};$original=''
function Read([string]$path){[IO.File]::ReadAllText($path)|ConvertFrom-Json}
function Child([string]$image,[string[]]$arguments,[int]$seconds=150){
 $id=[guid]::NewGuid().ToString('N');$quoted=($arguments|ForEach-Object {'"'+$_.Replace('"','\"')+'"'}) -join ' '
 $p=Start-Process $image -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput ('C:\CitaLab\platform-recovery-'+$id+'.out') -RedirectStandardError ('C:\CitaLab\platform-recovery-'+$id+'.err');$null=$p.Handle
 try{if(-not $p.WaitForExit($seconds*1000)){throw 'Recovery child deadline'};$p.Refresh();if($p.ExitCode -ne 0){throw 'Recovery child failed'}}finally{$p.Dispose()}
}
function Save {$report.updatedUtc=[DateTime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 6|Set-Content C:\CitaLab\platform-recovery-acceptance.json -Encoding UTF8}
try{
 Save
 if(Get-Process smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'Guest route already active'}
 $original=(Read 'C:\Program Files\RoutePilot\Agent\agent-config.json').AppRoot
 if(-not $original.StartsWith('C:\CitaLab\Installed\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected protected owner'}
 $productPath=Join-Path $root 'product.json';$product=Read $productPath;$product.distribution.agentSha256=(Get-FileHash (Join-Path $root 'runtime\agent-build\RoutePilot.Agent.exe')).Hash;$product|ConvertTo-Json -Depth 8|Set-Content $productPath -Encoding UTF8
 # Bind the protected candidate once. The recovery fixture itself performs the
 # real Start/Stop cycle; a second warm-up connection adds no acceptance evidence.
 $binding=Read (Join-Path $root 'data\connection.json')
 $prepare=@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $root 'Configure-Connection.ps1'),'-Action','Apply','-SourceProfile',$binding.SourceProfile,'-CliPath',$binding.CliPath)
 if($binding.ExpectedEgressIp){$prepare+=@('-ExpectedEgressIp',$binding.ExpectedEgressIp)}
 Child 'powershell.exe' $prepare 120
 $report.phase='ActiveRecovery';Save
 Child 'powershell.exe' @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File','C:\CitaLab\Platform-ActiveRecovery.ps1') 150
 $result=Read 'C:\CitaLab\platform-active-recovery.json';$report.result=$result;$report.success=[bool]$result.success
}catch{$report.error=$_.Exception.Message}
finally{
 try{
  if((Read (Join-Path $root 'runtime\session.json')).running){Child $helper @('stop',$root) 120}
  if(Get-Process smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'Guest engines remain'}
  if($original){$config=Read (Join-Path $original 'data\connection.json');$arguments=@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $original 'Configure-Connection.ps1'),'-Action','Apply','-SourceProfile',$config.SourceProfile,'-CliPath',$config.CliPath);if($config.ExpectedEgressIp){$arguments+=@('-ExpectedEgressIp',$config.ExpectedEgressIp)};Child 'powershell.exe' $arguments 120;$report.originalOwnerRestored=(Read 'C:\Program Files\RoutePilot\Agent\agent-config.json').AppRoot -ieq $original}
 }catch{$report.cleanupError=$_.Exception.Message;$report.success=$false}
 $report.phase='Finished';$report.finishedUtc=[DateTime]::UtcNow.ToString('o');Save
}
if(-not $report.success){exit 1}
