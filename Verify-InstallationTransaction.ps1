$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'installer\InstallationTransaction.ps1')
$fixture=Join-Path $PSScriptRoot ('runtime\installation-transaction-tests-'+[guid]::NewGuid().ToString('N'))
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
function Assert($Value,[string]$Message){if(-not $Value){throw $Message}}
function Public([string]$Name){Join-Path $client $Name}
function Manifest {
 $entries=@();foreach($name in @('RoutePilot.exe','product.json')+@(1..20|ForEach-Object {'public-'+$_+'.txt'})){$bytes=[Text.Encoding]::UTF8.GetBytes('new '+$name);$sha=[Security.Cryptography.SHA256]::Create();try{$hash=([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','')}finally{$sha.Dispose()};$entries+=@{path=$name;bytes=$bytes.Length;sha256=$hash}}
 @{version='0.6.1';architecture='win-x64';files=$entries}|ConvertTo-Json -Depth 5|Set-Content $incoming -Encoding UTF8
}
function Copy-Incoming {
 foreach($entry in (Get-Content $incoming -Raw|ConvertFrom-Json).files){[IO.File]::WriteAllText((Public $entry.path),('new '+$entry.path),[Text.UTF8Encoding]::new($false))}
 Copy-Item $incoming (Public 'manifest.json') -Force
}
function Prepare([string]$Name){
 $script:client=Join-Path $fixture ($Name+'\client');[void][IO.Directory]::CreateDirectory((Join-Path $client 'runtime'));[void][IO.Directory]::CreateDirectory((Join-Path $client 'data'))
 $script:incoming=Join-Path $fixture ($Name+'\incoming.json');Manifest
 [IO.File]::WriteAllText((Public 'RoutePilot.exe'),'old gui');[IO.File]::WriteAllText((Public 'product.json'),'old product')
 [IO.File]::WriteAllText((Public 'force-vpn.txt'),'my rule');[IO.File]::WriteAllText((Public 'data\preferences.json'),'my preferences')
}
try{
 Prepare 'rollback';Start-InstallTransaction $client $sid $incoming ([version]'0.6.1');Copy-Incoming
 Restore-InstallTransaction $client $sid
 Assert (([IO.File]::ReadAllText((Public 'RoutePilot.exe'))) -eq 'old gui') 'Old GUI was not restored.'
 Assert (([IO.File]::ReadAllText((Public 'product.json'))) -eq 'old product') 'Old metadata was not restored.'
 Assert (-not(Test-Path (Public 'public-1.txt'))) 'A new public file survived rollback.'
 Assert (([IO.File]::ReadAllText((Public 'force-vpn.txt'))) -eq 'my rule' -and ([IO.File]::ReadAllText((Public 'data\preferences.json'))) -eq 'my preferences') 'Rollback modified user data.'
 Assert ((Read-InstallJournal $client $sid).phase -eq 'RolledBack') 'Rollback was not journalled.'
 Restore-InstallTransaction $client $sid

 Prepare 'commit';Start-InstallTransaction $client $sid $incoming ([version]'0.6.1');Copy-Incoming
 $release=Join-Path $fixture 'commit.signal';[IO.File]::WriteAllText($release,'COMMIT')
 Complete-InstallTransaction $client $sid $incoming $release
 $journal=Read-InstallJournal $client $sid
 Assert ($journal.phase -eq 'Committed' -and -not(Test-Path $journal.backup)) 'Verified commit must retire its public backup.'
 Restore-InstallTransaction $client $sid
 Assert (([IO.File]::ReadAllText((Public 'RoutePilot.exe'))) -eq 'new RoutePilot.exe') 'A completed install was unexpectedly rolled back.'

 Prepare 'cancelled';Start-InstallTransaction $client $sid $incoming ([version]'0.6.1');Copy-Incoming
 [IO.File]::WriteAllText($release,'ROLLBACK');$refused=$false
 try{Complete-InstallTransaction $client $sid $incoming $release}catch{$refused=$true}
 Assert $refused 'Commit accepted a cancelled owner signal.';Restore-InstallTransaction $client $sid

 Prepare 'tamper';Start-InstallTransaction $client $sid $incoming ([version]'0.6.1');Copy-Incoming
 $journal=Read-InstallJournal $client $sid;[IO.File]::WriteAllText((Join-Path $journal.backup 'RoutePilot.exe'),'corrupt')
 $refused=$false;try{Restore-InstallTransaction $client $sid}catch{$refused=$true}
 Assert ($refused -and (Read-InstallJournal $client $sid).phase -eq 'RecoveryFailed') 'Corrupt backup must retain an actionable recovery state.'
 Assert (([IO.File]::ReadAllText((Public 'product.json'))) -eq 'new product.json') 'No target may change before all backups are validated.'
 [IO.File]::WriteAllText((Join-Path $journal.backup 'RoutePilot.exe'),'old gui');Restore-InstallTransaction $client $sid

 Prepare 'scope'
 foreach($name in @('..\escape.txt','C:\escape.txt','private\test.conf','data\connection.json','force-vpn.txt','runtime\installation-transaction.json')){$refused=$false;try{$null=Get-InstallPublicPath $client $name}catch{$refused=$true};Assert $refused ('Unsafe public path accepted: '+$name)}
 $refused=$false;try{$null=Read-InstallJournal (Join-Path $fixture 'tamper\client') ($sid+'-other')}catch{$refused=$true};Assert $refused 'Another Windows owner must not use the backup.'
 Write-Output 'PASS: public installation snapshot/rollback/commit, new-file cleanup, cancellation, corrupt backup retention and retry, user-data exclusion, owner and path boundaries.'
}finally{
 $resolved=[IO.Path]::GetFullPath($fixture);$prefix=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runtime\installation-transaction-tests-'))
 if(-not $resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe transaction fixture cleanup.'}
 Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
}
