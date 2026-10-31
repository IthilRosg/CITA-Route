$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Gui.ps1')
# Real fixture bytes; publication ACL and process/task retirement require RM.
function Save-GuiCycleRecord([string]$Root,[string]$Name,$Value){[IO.File]::WriteAllText((Join-Path $Root $Name),($Value|ConvertTo-Json -Depth 6 -Compress))}
$root=Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../runtime'))) ('gui-cycle-fixture-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $root 'data'),(Join-Path $root 'runtime')|Out-Null
$plan=[pscustomobject]@{Run=('a'*32);AppRoot=$root;ProfileSha=('A'*64);AppSha=('B'*64);InitialOperation=('b'*32);InitialGeneration=43}
try{
 $prefs=Join-Path $root 'data\preferences.json';[IO.File]::WriteAllText($prefs,'{"ConnectOnLaunch":false,"other":"preserve"}');$original=[IO.File]::ReadAllText($prefs)
 Set-GuiCyclePreferences $plan $root
 $updated=Get-Content -LiteralPath $prefs -Raw|ConvertFrom-Json
 if(!$updated.ConnectOnLaunch -or $updated.other -cne 'preserve'){throw 'Preferences fixture did not preserve unrelated fields.'}
 [IO.File]::WriteAllText($prefs,'external-edit');$refused=$false
 try{Restore-GuiCyclePreferences $plan $root}catch{$refused=$true}
 if(!$refused -or [IO.File]::ReadAllText($prefs) -cne 'external-edit'){throw 'Foreign preferences were overwritten.'}
 $record=Get-Content -LiteralPath (Join-Path $root 'gui-preferences.json') -Raw|ConvertFrom-Json
 [IO.File]::WriteAllBytes($prefs,[Convert]::FromBase64String($record.Entries[0].ProposedBase64));Restore-GuiCyclePreferences $plan $root
 if([IO.File]::ReadAllText($prefs) -cne $original -or (Test-Path -LiteralPath (Join-Path $root 'data\transport-choice.json'))){throw 'Original preference bytes or absence not restored.'}
 Set-GuiCyclePreferences $plan $root
 $changed=Get-Content -LiteralPath $prefs -Raw|ConvertFrom-Json;$changed|Add-Member -NotePropertyName ViewMode -NotePropertyValue 'Compact' -Force;$changed.other='external-preserve'
 [IO.File]::WriteAllText($prefs,($changed|ConvertTo-Json -Compress));Restore-GuiCyclePreferences $plan $root
 $restored=Get-Content -LiteralPath $prefs -Raw|ConvertFrom-Json
 if($restored.ConnectOnLaunch -or $restored.other -cne 'external-preserve' -or $restored.ViewMode -cne 'Compact'){throw 'Owned-field rollback destroyed unrelated changes.'}
 $state=[pscustomobject]@{Success=$true;Native=[pscustomobject]@{desired=$false;phase='Disconnected';operation=$plan.InitialOperation;generation=43}}
 $refused=$false;try{Resolve-GuiCycleConnection $plan $root $state}catch{$refused=$true};if(!$refused){throw 'Missing sender evidence was treated as a proven absent session.'}
 Save-GuiCycleRecord $root 'gui-process.json' @{Run=$plan.Run;ImageSha=$plan.AppSha;Pid=100;StartedUtcTicks=200}
 $receipt=@{schema=1;processId=100;processStartedUtcTicks=200;initialOperation=$plan.InitialOperation;initialGeneration=43;profileId=$plan.ProfileSha;connectionId=('c'*32)}
 Save-GuiCycleRecord (Join-Path $root 'runtime') 'native-launch-request.json' $receipt
 if((Resolve-GuiCycleConnection $plan $root $state) -cne ('c'*32)){throw 'Owned GUI receipt was not resolved.'}
 Save-GuiCycleRecord $root 'gui-senders.json' @{Run=$plan.Run;Senders=@(@{Run=$plan.Run;ImageSha=$plan.AppSha;Pid=100;StartedUtcTicks=200},@{Run=$plan.Run;ImageSha=$plan.AppSha;Pid=101;StartedUtcTicks=300})}
 Save-GuiCycleRecord $root 'gui-process.json' @{Run=$plan.Run;ImageSha=$plan.AppSha;Pid=101;StartedUtcTicks=300}
 if((Resolve-GuiCycleConnection $plan $root $state) -cne ('c'*32)){throw 'Reopening GUI lost the original sender connection identity.'}
 $receipt.processId=101;Save-GuiCycleRecord (Join-Path $root 'runtime') 'native-launch-request.json' $receipt;$refused=$false
 try{Resolve-GuiCycleConnection $plan $root $state}catch{$refused=$true}
 if(!$refused){throw 'Foreign sender receipt was accepted.'}
 'PASS real-FS preference conflict/restoration and sender/CAS lineage; no remote process or network test.'
}finally{
 if([IO.Path]::GetDirectoryName($root) -ine [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../runtime')) -or [IO.Path]::GetFileName($root) -cnotmatch '^gui-cycle-fixture-[a-f0-9]{32}$'){throw 'Fixture cleanup scope differs.'}
 Remove-Item -LiteralPath $root -Recurse -Force
}
