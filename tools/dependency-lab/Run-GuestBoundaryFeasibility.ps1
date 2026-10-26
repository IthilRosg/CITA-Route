$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$root='C:\CitaLab\PlatformNext';$helper='C:\CitaLab\DependencyLab\Cita.DependencyLab.exe'
$report=@{success=$false;phase='Prepare';startedUtc=[datetime]::UtcNow.ToString('o');scope='Isolated firewall feasibility only; no production boundary or leak-proof claim'}
$original='';$ruleName='CITA-Lab-Boundary-'+[guid]::NewGuid().ToString('N');$policy=$null;$added=$false
function Read([string]$path){[IO.File]::ReadAllText($path)|ConvertFrom-Json}
function Child([string]$image,[string[]]$arguments,[switch]$AllowFailure){
 $prefix='C:\CitaLab\boundary-call-'+[guid]::NewGuid().ToString('N')
 $quoted=($arguments|ForEach-Object {'"'+$_.Replace('"','\"')+'"'}) -join ' '
 $p=Start-Process $image -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput ($prefix+'.out') -RedirectStandardError ($prefix+'.err');$null=$p.Handle
 try{
  if(-not $p.WaitForExit(150000)){throw 'Boundary child deadline; cleanup required'}
  if($p.ExitCode -ne 0 -and -not $AllowFailure){throw ('Boundary child failed: '+[IO.Path]::GetFileName($image))}
  $stream=[IO.File]::Open(($prefix+'.out'),'Open','Read','ReadWrite, Delete');$reader=New-Object IO.StreamReader($stream)
  try{return $reader.ReadToEnd()}finally{$reader.Dispose()}
 }finally{$p.Dispose()}
}
function Save{$report.updatedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 5|Set-Content C:\CitaLab\boundary-feasibility.json -Encoding UTF8}
function Health{
 $raw=Child 'C:\Program Files\RoutePilot\Agent\RoutePilot.Agent.exe' @('--session-health') -AllowFailure
 $value=$raw|ConvertFrom-Json
 return @{healthy=[bool]$value.healthy;code=$value.code}
}
try{
 Save
 if(Get-Process smart-router,vpn-bridge,RoutePilot -ErrorAction SilentlyContinue){throw 'Guest must be idle'}
 $original=(Read 'C:\Program Files\RoutePilot\Agent\agent-config.json').AppRoot
 if(-not $original.StartsWith('C:\CitaLab\Installed\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected original owner'}
 $product=Read (Join-Path $root 'product.json');$product.distribution.agentSha256=(Get-FileHash (Join-Path $root 'runtime\agent-build\RoutePilot.Agent.exe')).Hash
 $product|ConvertTo-Json -Depth 8|Set-Content (Join-Path $root 'product.json') -Encoding UTF8
 $null=Child $helper @('connect',$root)
 $report.before=Health;if(-not $report.before.healthy){throw 'No verified VPN baseline'}
 $policy=New-Object -ComObject HNetCfg.FwPolicy2
 foreach($profile in @(1,2,4)){if(-not $policy.FirewallEnabled($profile)){throw 'Firewall disabled; no feasibility claim allowed'}}
 $rule=New-Object -ComObject HNetCfg.FWRule
 $rule.Name=$ruleName;$rule.Description='Temporary CITA isolated VM feasibility test';$rule.ApplicationName='C:\Program Files\RoutePilot\Agent\vpn-bridge.exe'
 $rule.Direction=2;$rule.Action=0;$rule.Enabled=$true;$rule.Profiles=7;$rule.Protocol=256;$rule.RemoteAddresses='*'
 $policy.Rules.Add($rule);$added=$true
 $report.phase='RuleInstalled';Save
 $report.withRule=Health
 $policy.Rules.Remove($ruleName);$added=$false
 $report.afterRemoval=Health
 $report.workingVpnSurvivesRule=[bool]$report.withRule.healthy
 $report.recoveredAfterRuleRemoval=[bool]$report.afterRemoval.healthy
 $report.success=$true
}catch{$report.error=$_.Exception.Message}
finally{
 try{
  if($added){$policy.Rules.Remove($ruleName);$added=$false}
  $report.ruleRemoved=-not [bool]@($policy.Rules|Where-Object Name -eq $ruleName).Count
  if((Read (Join-Path $root 'runtime\session.json')).running){$null=Child $helper @('stop',$root)}
  if(Get-Process smart-router,vpn-bridge -ErrorAction SilentlyContinue){throw 'Guest engines remain'}
  if($original){$config=Read (Join-Path $original 'data\connection.json');$parameters=@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $original 'Configure-Connection.ps1'),'-Action','Apply','-SourceProfile',$config.SourceProfile,'-CliPath',$config.CliPath);if($config.ExpectedEgressIp){$parameters+=@('-ExpectedEgressIp',$config.ExpectedEgressIp)};$null=Child 'powershell.exe' $parameters;$report.originalOwnerRestored=(Read 'C:\Program Files\RoutePilot\Agent\agent-config.json').AppRoot -ieq $original}
 }catch{$report.cleanupError=$_.Exception.Message;$report.success=$false}
 $report.phase='Finished';Save
}
if(-not $report.success){exit 1}
