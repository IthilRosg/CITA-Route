$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$report=@{schema=1;success=$false;startedUtc=[datetime]::UtcNow.ToString('o')};$folder=$null;$created=$false
try{
 $base=[Environment]::GetFolderPath('ProgramFiles');$folder=Join-Path $base ('CitaNativeTrust-'+[guid]::NewGuid().ToString('N'))
 New-Item -ItemType Directory -Path $folder -ErrorAction Stop|Out-Null;$created=$true
 foreach($name in @('native-awg-lab.exe','native-core-lab.exe')){Copy-Item -LiteralPath (Join-Path 'C:\CitaLab\NativeAwg' $name) -Destination (Join-Path $folder $name)}
 $catalog=@{Schema=1;Owner=@{Name='native-awg-lab.exe';Sha256=(Get-FileHash (Join-Path $folder 'native-awg-lab.exe')).Hash};Core=@{Name='native-core-lab.exe';Sha256=(Get-FileHash (Join-Path $folder 'native-core-lab.exe')).Hash}}
 $catalog|ConvertTo-Json -Depth 4|Set-Content (Join-Path $folder 'native-catalog.json') -Encoding UTF8
 $digest=(Get-FileHash (Join-Path $folder 'native-catalog.json')).Hash
 $admin=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544');$system=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
 $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false);$acl.SetOwner($admin)
 foreach($sid in @($admin,$system)){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
 Set-Acl -LiteralPath $folder -AclObject $acl
 foreach($item in Get-ChildItem -LiteralPath $folder -File){$fa=[Security.AccessControl.FileSecurity]::new();$fa.SetAccessRuleProtection($true,$false);$fa.SetOwner($admin);foreach($sid in @($admin,$system)){$fa.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','Allow'))};Set-Acl -LiteralPath $item.FullName -AclObject $fa}
 $info=[Diagnostics.ProcessStartInfo]::new('C:\CitaLab\NativeAwg\AgentBackend\Cita.NativeAgentLab.exe',('--trust-lab "'+$folder+'" '+$digest));$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $p=[Diagnostics.Process]::Start($info);try{$output=$p.StandardOutput.ReadToEndAsync();$errors=$p.StandardError.ReadToEndAsync();if(-not $p.WaitForExit(30000)){$p.Kill();$p.WaitForExit();throw 'Trust fixture deadline'};$report.probes=$output.GetAwaiter().GetResult()|ConvertFrom-Json;$report.success=[bool]($p.ExitCode -eq 0 -and $report.probes.success)}finally{$p.Dispose()}
}catch{$report.errorType=$_.Exception.GetType().Name}
finally{
 if($created){$absolute=[IO.Path]::GetFullPath($folder);$parent=[IO.Path]::GetDirectoryName($absolute);if($parent -ne [Environment]::GetFolderPath('ProgramFiles') -or [IO.Path]::GetFileName($absolute) -notmatch '^CitaNativeTrust-[a-f0-9]{32}$'){throw 'Cleanup containment failed'};Remove-Item -LiteralPath $absolute -Recurse -Force;$report.cleaned=[bool](-not (Test-Path -LiteralPath $absolute));if(-not $report.cleaned){$report.success=$false}}
 $report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 5|Set-Content 'C:\CitaLab\NativeAwg\native-trust-result.json' -Encoding UTF8
}
if(-not $report.success){exit 1}
