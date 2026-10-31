$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $root 'runtime/invoke-rm-lab.ps1')
$image=Join-Path $root 'runtime/agent-build/RoutePilot.Agent.exe'
$hash=(Get-FileHash -LiteralPath $image).Hash
$productHash=(Get-FileHash -LiteralPath (Join-Path $root 'product.json')).Hash
$folder='AgentAcceptance-'+$hash.Substring(0,12)
$prepare=@'
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong physical station'}
$path='C:\CITA-Lab\__FOLDER__'
if((Test-Path $path)-and !(Test-Path "$path\self-test-owned.json")){throw 'Foreign acceptance directory'}
New-Item -ItemType Directory -Path $path -Force|Out-Null
[IO.File]::WriteAllText("$path\self-test-owned.json",'{"owner":"CITA-Agent-Acceptance","schema":1}')
'@
Invoke-Rm ($prepare.Replace('__FOLDER__',$folder))
& scp -q -i "$env:USERPROFILE\.ssh\cita_test_station_ed25519" -o "UserKnownHostsFile=$env:USERPROFILE\.ssh\cita_test_station_known_hosts" -o StrictHostKeyChecking=yes $image (Join-Path $root 'product.json') ('citastation@26.68.227.253:C:/CITA-Lab/'+$folder+'/')
if($LASTEXITCODE){throw 'Acceptance transfer failed'}
$code=@'
$ErrorActionPreference='Stop';$root='C:\CITA-Lab\__FOLDER__';$image="$root\RoutePilot.Agent.exe"
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong physical station'}
if((Get-FileHash $image).Hash -ne '__HASH__' -or (Get-FileHash "$root\product.json").Hash -ne '__PRODUCT__'){throw 'Acceptance identity mismatch'}
$tag='CITA-Lab-AgentSelfTest-'+[Guid]::NewGuid().ToString('N');$rules=@();$p=$null;$result=@{success=$false;sha256='__HASH__'};$watch=[Diagnostics.Stopwatch]::StartNew()
try
{
 foreach($direction in @('Inbound','Outbound'))
 {
  $name=$tag+'-'+$direction
  New-NetFirewallRule -Name $name -DisplayName $name -Program $image -Direction $direction -Action Allow -Protocol Any -LocalAddress 127.0.0.1 -RemoteAddress 127.0.0.1 -Profile Any|Out-Null
  $rules+=$name
 }
 $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$image;$info.Arguments='--self-test';$info.WorkingDirectory=$root;$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $p=[Diagnostics.Process]::Start($info);$output=$p.StandardOutput.ReadToEndAsync();$errorOutput=$p.StandardError.ReadToEndAsync()
 if(!$p.WaitForExit(180000)){$p.Kill();throw 'Acceptance deadline'}
 $stdout=$output.Result;$stderr=$errorOutput.Result
 [IO.File]::WriteAllText("$root\self-test.log",$stdout+$stderr)
 $result.exit=$p.ExitCode;$result.success=($p.ExitCode -eq 0)
 $result.zapretScopeTests=($stdout -like '*PASS: exact Zapret TLS socket*')
 $result.socketLifecycleTests=($stdout -like '*PASS: real bound TCP ownership*')
 $result.logSha256=(Get-FileHash "$root\self-test.log").Hash
}
catch{$result.error=$_.Exception.Message}
finally
{
 if($p){if(!$p.HasExited){$p.Kill();$p.WaitForExit()};$p.Dispose()}
 foreach($name in $rules){Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue|Remove-NetFirewallRule}
 $result.firewallCleanup=(@($rules|ForEach-Object {Get-NetFirewallRule -Name $_ -ErrorAction SilentlyContinue}).Count -eq 0)
 $result.durationMs=$watch.ElapsedMilliseconds
 $result|ConvertTo-Json -Depth 4|Set-Content "$root\self-test-result.json" -Encoding utf8
}
$result|ConvertTo-Json -Depth 4
'@
$code=$code.Replace('__FOLDER__',$folder).Replace('__HASH__',$hash).Replace('__PRODUCT__',$productHash)
Invoke-Rm $code|Set-Content (Join-Path $root 'runtime/goal2-physical-agent-tests.json') -Encoding utf8
Get-Content (Join-Path $root 'runtime/goal2-physical-agent-tests.json')
