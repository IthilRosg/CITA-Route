param([Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$ExpectedManifestSha,
      [ValidatePattern('^[A-F0-9]{64}$')][string]$ExpectedOldAgentSha='97506113A245233BB7310DDCDF6CB80B5FD4C62AEC39AF9D0EA49A5FC1E3E764',[long]$ExpectedGeneration=39)
$ErrorActionPreference='Stop';$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'tools/native-control-probe/Invoke-BoundedRm.ps1')
$journal=Join-Path $workspace 'runtime/native-bundle-producer-active.json'
if(Test-Path -LiteralPath $journal){throw 'The existing production bundle operation needs reconciliation.'}
$id=[guid]::NewGuid().ToString('N');$entry=@{Run=$id;ExpectedManifest=$ExpectedManifestSha;State='MayStart';Utc=[datetime]::UtcNow.ToString('O')}
$f=[IO.File]::Open($journal,'CreateNew','Write','Read');try{$b=[Text.Encoding]::UTF8.GetBytes(($entry|ConvertTo-Json -Compress));$f.Write($b,0,$b.Length);$f.Flush($true)}finally{$f.Dispose()}
$code=@'
$ErrorActionPreference='Stop';Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
$client='C:\Users\CITASTATION\AppData\Local\Programs\CITA Route\client';$agent='C:\Program Files\RoutePilot\Agent';$id='__ID__'
$resultPath=Join-Path $client ('runtime\native-bundle-producer-result-'+$id+'.json')
function Selection {
 $setting=[IO.File]::ReadAllText("$agent\native\service.json")|ConvertFrom-Json;$name=$setting.CatalogName;if(!$name){$name='native-catalog.json'}
 if([IO.Path]::GetFileName($name) -cne $name){throw 'Catalog path differs.'};$p=Join-Path "$agent\native" $name
 if((Get-FileHash -LiteralPath $p).Hash -ine $setting.CatalogSha256){throw 'Catalog pin differs.'};$cat=[IO.File]::ReadAllText($p)|ConvertFrom-Json
 if(!$cat.Profile.Name -or [IO.Path]::GetFileName($cat.Profile.Name) -cne $cat.Profile.Name -or (Get-FileHash -LiteralPath (Join-Path "$agent\native" $cat.Profile.Name)).Hash -ine $cat.Profile.Sha256){throw 'Selected profile pin differs.'}
 return $cat
}
try{
 if((Get-CimInstance Win32_ComputerSystemProduct).UUID -cne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong producer station.'}
 if((Get-FileHash -LiteralPath "$client\manifest.json").Hash -cne '__MANIFEST__'){throw 'Installed package manifest differs.'}
 $manifest=[IO.File]::ReadAllText("$client\manifest.json")|ConvertFrom-Json
 foreach($entry in $manifest.files){if($entry.path -cin @('force-direct.txt','force-vpn.txt')){continue};$p=[IO.Path]::GetFullPath((Join-Path $client $entry.path));if(!$p.StartsWith($client+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-Item -LiteralPath $p).Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $p).Hash -cne $entry.sha256){throw 'Installed package file differs.'}}
 $config=[IO.File]::ReadAllText("$agent\agent-config.json")|ConvertFrom-Json
 if($config.UserSid -cne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or $config.AppRoot -ine $client){throw 'Producer owner differs.'}
 $intentSha=(Get-FileHash -LiteralPath "$agent\native\intent.json").Hash;$before=[IO.File]::ReadAllText("$agent\native\intent.json")|ConvertFrom-Json
 if($before.Desired -or $before.Generation -ne __GENERATION__){throw 'Producer baseline changed.'}
 $selected=Selection
 if($selected.Profile.Sha256 -cne '748F7A49AEF2166E4378E80F4EFDFE42E4B2B4B4E12C921C64A66BBCB806A37A'){throw 'The test profile changed.'}
 $oldAgent=(Get-FileHash -LiteralPath "$agent\RoutePilot.Agent.exe").Hash
 if($oldAgent -cne '__OLD_AGENT__'){throw 'The old test service changed.'}
 $zip=Join-Path $client 'runtime\native-components.zip';$zipSha=(Get-FileHash -LiteralPath $zip).Hash
 $null=& "$client\Invoke-NativeBundleUpgrade.ps1" -Package $zip -Sha256 $zipSha -UserSid $config.UserSid -UserProfile $config.UserProfile
 $j=[IO.File]::ReadAllText("$agent\native-upgrade.json")|ConvertFrom-Json
 if($j.id -cnotmatch '^[a-f0-9]{32}$'){throw 'Producer journal identity invalid.'}
 $receipt=[IO.File]::ReadAllText((Join-Path $agent ('native-update-result-'+$j.id+'.json')))|ConvertFrom-Json
 $after=Selection;$newAgent=(Get-FileHash -LiteralPath "$agent\RoutePilot.Agent.exe").Hash
 if($j.phase -cne 'Committed' -or $receipt.id -cne $j.id -or $receipt.phase -cne 'Committed' -or $newAgent -cne $j.newAgentSha -or $newAgent -cne (Get-FileHash -LiteralPath "$client\runtime\agent-build\RoutePilot.Agent.exe").Hash){throw 'Producer committed identity differs.'}
 if((Get-FileHash -LiteralPath "$agent\native\intent.json").Hash -cne $intentSha -or $selected.Profile.Sha256 -cne $after.Profile.Sha256 -or $selected.ProfileName -cne $after.ProfileName){throw 'Producer changed the selected profile or intent.'}
 $root=Join-Path $agent ('native-update-'+$j.id);$task='CitaNativeUpgrade-'+$j.id
 $taskNames=@($task,($task+'-Backup'),($task+'-Boot'),($task+'-Controller'))
 if((Test-Path -LiteralPath $root) -or @(foreach($t in $taskNames){Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue}).Count){throw 'Producer temporary payload remains.'}
 $result=@{Run=$id;Passed=$true;UpgradeRun=$j.id;Phase=$j.phase;OldAgent=$oldAgent;Agent=$newAgent;ProfilePreserved=$true;IntentPreserved=$true;Generation=$before.Generation;PayloadAbsent=$true;TasksAbsent=$true;ReceiptRetained=$true;Utc=[datetime]::UtcNow.ToString('O')}
}catch{$result=@{Run=$id;Passed=$false;ErrorType=$_.Exception.GetType().Name;Script=[IO.Path]::GetFileName($_.InvocationInfo.ScriptName);Line=$_.InvocationInfo.ScriptLineNumber;Utc=[datetime]::UtcNow.ToString('O')}}
[IO.File]::WriteAllText($resultPath,($result|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false));$result|ConvertTo-Json -Compress
'@
$code=$code.Replace('__ID__',$id).Replace('__MANIFEST__',$ExpectedManifestSha).Replace('__OLD_AGENT__',$ExpectedOldAgentSha).Replace('__GENERATION__',$ExpectedGeneration.ToString([Globalization.CultureInfo]::InvariantCulture))
$localSource=Join-Path $workspace ('runtime/native-bundle-producer-test-'+$id+'.ps1')
$remoteSource='C:\Program Files\RoutePilot\Agent\native-producer-test-'+$id+'.ps1'
[IO.File]::WriteAllText($localSource,$code,[Text.UTF8Encoding]::new($true));$sourceSha=(Get-FileHash -LiteralPath $localSource).Hash
$entry.Script=$remoteSource;$entry.ScriptSha=$sourceSha;$entry.State='MayCopyScript'
[IO.File]::WriteAllText($journal,($entry|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
$preflight="if((Get-CimInstance Win32_ComputerSystemProduct).UUID -cne 'F8798200-12FB-11F1-8F47-A104BEE59100' -or (Test-Path -LiteralPath '$remoteSource')){throw 'Producer test source path is occupied or station differs.'};'absent'"
if((Invoke-BoundedRm $preflight 20).Trim() -cne 'absent'){throw 'Producer test source absence unconfirmed.'}
Invoke-BoundedScp $localSource ('citastation@192.168.1.124:'+$remoteSource.Replace('\','/'))
# Build the small envelope without expanding remote PowerShell variables.
$invoke=@'
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force;$hold=[IO.File]::Open('__SCRIPT__','Open','Read','Read');try{if((Get-FileHash -LiteralPath '__SCRIPT__').Hash -cne '__SHA__'){throw 'Producer test script differs.'};& '__SCRIPT__'}finally{$hold.Dispose()}
'@
$value=Invoke-BoundedRm $invoke.Replace('__SCRIPT__',$remoteSource).Replace('__SHA__',$sourceSha) 180
$value|Set-Content -LiteralPath (Join-Path $workspace ('runtime/native-bundle-producer-result-'+$id+'.json'))
$result=$value|ConvertFrom-Json
if(!$result.Passed){throw 'Production producer outcome failed or unknown; retain the journal and inspect the same run.'}
$clean="if([IO.Path]::GetDirectoryName('$remoteSource') -ine 'C:\Program Files\RoutePilot\Agent' -or ((Get-Item -LiteralPath '$remoteSource').Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Get-FileHash -LiteralPath '$remoteSource').Hash -cne '$sourceSha'){throw 'Producer cleanup source differs.'};Remove-Item -LiteralPath '$remoteSource' -Force;if(Test-Path -LiteralPath '$remoteSource'){throw 'Producer script remains.'};'absent'"
if((Invoke-BoundedRm $clean 20).Trim() -cne 'absent'){throw 'Producer test script cleanup unconfirmed.'}
Move-Item -LiteralPath $journal -Destination (Join-Path $workspace ('runtime/native-bundle-producer-completed-'+$id+'.json'))
$value
