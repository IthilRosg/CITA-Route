param([Parameter(Mandatory)][string]$RunRoot)
$ErrorActionPreference='Stop'
$agent=Join-Path $env:ProgramFiles 'RoutePilot\Agent';$root=[IO.Path]::GetFullPath($RunRoot).TrimEnd('\')
$locks=[Collections.Generic.List[object]]::new();$held=$null;$image=$null;$lease=$null;$results=[Collections.Generic.List[object]]::new();$ownedOperation=$null
function Record($value){[IO.File]::WriteAllText((Join-Path $root 'result.json'),($value|ConvertTo-Json -Depth 7 -Compress),[Text.UTF8Encoding]::new($false))}
function Rpc($request){
    $s=Get-NativeUpgradeServiceSnapshot $lease
    if($s.State -cne 'Running' -or $s.ProcessId -ne $held.Id -or $held.HasExited){throw 'Import service changed.'}
    $json=$request|ConvertTo-Json -Depth 5 -Compress
    try{$reply=[Cita.NativeUpgradePipe]::Exchange($json,$held.Id)|ConvertFrom-Json}finally{$json=$null}
    if($held.HasExited -or (Get-NativeUpgradeServiceSnapshot $lease).ProcessId -ne $held.Id){throw 'Import service changed during request.'}
    if(!$reply.Success -or !$reply.Native){throw 'Import request was refused.'};return $reply.Native
}
function State {Rpc @{Command='native-status'}}
function Wait-Terminal([string]$operation){
    $watch=[Diagnostics.Stopwatch]::StartNew()
    do{$n=State;if($n.operation -cne $operation -or $n.desired){throw 'Import ownership changed.'};if($n.phase -cin @('Disconnected','RecoveryFailed')){return $n};Start-Sleep -Milliseconds 250}while($watch.Elapsed.TotalSeconds -lt 95)
    throw 'Import did not finish within budget.'
}
function Reset-Failed($n){
    if($n.phase -ceq 'RecoveryFailed'){$null=Rpc @{Command='native-stop';RunId=$n.operation};$n=Wait-Terminal $n.operation}
    if($n.desired -or $n.phase -cne 'Disconnected'){throw 'Import reset was not confirmed.'};return $n
}
function Selection {
    $settingsPath=Join-Path $agent 'native\service.json';$settings=[IO.File]::ReadAllText($settingsPath)|ConvertFrom-Json
    $name=$settings.CatalogName;if(!$name){$name='native-catalog.json'}
    if([IO.Path]::GetFileName($name) -cne $name){throw 'Catalog outside native root.'}
    $path=Join-Path $agent ('native\'+$name)
    if((Get-NativeUpgradeHash $path) -ine $settings.CatalogSha256){throw 'Catalog pin differs.'}
    $cat=[IO.File]::ReadAllText($path)|ConvertFrom-Json
    if([IO.Path]::GetFileName($cat.Profile.Name) -cne $cat.Profile.Name){throw 'Profile outside native root.'}
    $profile=Join-Path $agent ('native\'+$cat.Profile.Name)
    if((Get-NativeUpgradeHash $profile) -ine $cat.Profile.Sha256){throw 'Profile pin differs.'}
    return @{SettingsSha=(Get-NativeUpgradeHash $settingsPath);Catalog=$cat;ProfilePath=$profile;CatalogName=$name}
}
function Import-Case([string]$label,[string]$base64,[string]$name,[bool]$expectedValid){
    $before=Selection;$initial=State
    if($initial.desired -or $initial.phase -cne 'Disconnected'){throw 'Import initial state differs.'}
    $script:ownedOperation=[guid]::NewGuid().ToString('N');$watch=[Diagnostics.Stopwatch]::StartNew()
    Record @{Passed=$false;Pending=$true;Case=$label;Operation=$script:ownedOperation;Cases=$results;Utc=[datetime]::UtcNow.ToString('O')}
    $accepted=Rpc @{Command='native-import';RunId=$initial.operation;Profile=@{Base64=$base64;Name=$name;RequestId=$script:ownedOperation}}
    $terminal=Wait-Terminal $script:ownedOperation;$after=Selection
    $valid=$terminal.phase -ceq 'Disconnected'
    $preserved=$before.SettingsSha -ceq $after.SettingsSha
    $matched=$expectedValid -eq $valid
    if(!$expectedValid){$matched=$matched -and $preserved -and $terminal.errorStage -ceq 'Importing'}
    else{$matched=$matched -and !$preserved -and $after.Catalog.Profile.Sha256 -ieq $script:originalSha -and $after.Catalog.ProfileName -ceq $name}
    $results.Add(@{Case=$label;Passed=[bool]$matched;Phase=$terminal.phase;ErrorCode=$terminal.errorCode;ErrorStage=$terminal.errorStage;Generation=$terminal.generation;SettingsPreserved=$preserved;ProfileSha=$after.Catalog.Profile.Sha256;Ms=$watch.ElapsedMilliseconds})
    $null=Reset-Failed $terminal
    if(!$matched){throw 'Import case failed.'}
}
try{
    if([IO.Path]::GetDirectoryName($root) -ine $agent -or [IO.Path]::GetFileName($root) -cnotmatch '^native-profile-test-[a-f0-9]{32}$' -or $PSCommandPath -ine (Join-Path $root 'Test-ProfileImport.ps1')){throw 'Import test root refused.'}
    if((Get-CimInstance Win32_ComputerSystemProduct).UUID -cne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong import station.'}
    . "$root\NativeUpgradeTransaction.ps1";. "$root\NativeUpgradeService.ps1"
    $config=[IO.File]::ReadAllText((Join-Path $agent 'agent-config.json'))|ConvertFrom-Json
    if($config.UserSid -cne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value){throw 'Import owner differs.'}
    foreach($name in @('connection-operation.lock','setup.lock','upgrade.lock','network-control.lock','installation-maintenance.lock')){$path=Join-Path (Join-Path $config.AppRoot 'runtime') $name;Assert-NativeUpgradePath $path;$locks.Add([IO.File]::Open($path,'OpenOrCreate','ReadWrite','None'))}
    $lease=Open-NativeUpgradeLease $agent;$image=[IO.File]::Open((Join-Path $agent 'RoutePilot.Agent.exe'),'Open','Read','Read')
    if((Get-NativeUpgradeHash (Join-Path $agent 'RoutePilot.Agent.exe')) -cne '97506113A245233BB7310DDCDF6CB80B5FD4C62AEC39AF9D0EA49A5FC1E3E764'){throw 'Import agent differs.'}
    $s=Get-NativeUpgradeServiceSnapshot $lease;$held=Get-Process -Id $s.ProcessId;$null=$held.Handle
    if($s.State -cne 'Running' -or $held.HasExited -or $held.Path -ine (Join-Path $agent 'RoutePilot.Agent.exe')){throw 'Import service unavailable.'}
    Initialize-NativeUpgradePipe;$initial=State
    if($initial.desired -or $initial.phase -cne 'Disconnected'){throw 'Import test requires a disconnected session.'}
    $original=Selection;$script:originalSha=$original.Catalog.Profile.Sha256;$originalName=$original.Catalog.ProfileName
    $bytes=[IO.File]::ReadAllBytes($original.ProfilePath);try{$encoded=[Convert]::ToBase64String($bytes)}finally{[Array]::Clear($bytes,0,$bytes.Length)}
    if($encoded.Length -gt 3000){throw 'Import fixture exceeds test pipe budget.'}
    # No config leaves this machine or reaches diagnostic output. The only
    # accepted fixture is the already selected, hash-pinned user profile.
    Import-Case 'malformed-before' 'bm90LWEtV0ctcHJvZmlsZQ==' 'Invalid fixture' $false
    # Re-import with the original name. A controller interruption must not
    # leave a cosmetic test profile selected. Profile switching is a separate
    # acceptance case and requires its own independently recoverable fixture.
    Import-Case 'valid-same-profile' $encoded $originalName $true
    Import-Case 'malformed-after' 'bm90LWEtV0ctcHJvZmlsZQ==' 'Invalid fixture' $false
    $final=State;$selected=Selection
    if($final.desired -or $final.phase -cne 'Disconnected' -or $selected.Catalog.Profile.Sha256 -ine $originalSha -or $selected.Catalog.ProfileName -cne $originalName){throw 'Original profile restoration unconfirmed.'}
    Record @{Passed=$true;Cases=$results;InitialGeneration=$initial.generation;FinalGeneration=$final.generation;FinalPhase=$final.phase;Desired=$final.desired;SelectedContentPreserved=$true;ProfileSha=$originalSha;Utc=[datetime]::UtcNow.ToString('O')}
}catch{
    # Never stop or overwrite a foreign/newer operation. An import itself does
    # not arm system routing; its in-service timeout remains authoritative.
    if($held -and !$held.HasExited -and $ownedOperation){try{$n=State;if($n.operation -ceq $ownedOperation -and !$n.desired){$null=Rpc @{Command='native-stop';RunId=$ownedOperation};$null=Wait-Terminal $ownedOperation}}catch{}}
    Record @{Passed=$false;Cases=$results;ErrorType=$_.Exception.GetType().Name;Line=$_.InvocationInfo.ScriptLineNumber;Utc=[datetime]::UtcNow.ToString('O')};exit 1
}finally{$encoded=$null;if($held){$held.Dispose()};if($image){$image.Dispose()};if($lease){$lease.Handle.Dispose()};foreach($file in $locks){$file.Dispose()}}
