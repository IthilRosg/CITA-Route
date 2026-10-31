param([Parameter(Mandatory)][string]$Package,[Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$Sha256,[Parameter(Mandatory)][string]$UserSid,[Parameter(Mandatory)][string]$UserProfile)
$ErrorActionPreference='Stop'
if(-not([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Native bundle setup requires administrator approval.'}
if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -cne $UserSid){throw 'Native bundle owner differs.'}
. "$PSScriptRoot\installer\NativeUpgradeTransaction.ps1"
. "$PSScriptRoot\installer\NativeUpgradeService.ps1"
$agent=Join-Path $env:ProgramFiles 'RoutePilot\Agent'
$manifest=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'manifest.json'))|ConvertFrom-Json
$sources=[ordered]@{'Cita.NativeUpdater.exe'='runtime\native-updater\Cita.NativeUpdater.exe';'Incoming.Agent.exe'='runtime\agent-build\RoutePilot.Agent.exe'}
foreach($name in @('Actor.ps1','Recover.ps1','NativeUpgradeWorker.ps1','NativeUpgradeRecovery.ps1','NativeUpgradeFlow.ps1','NativeUpgradeTransaction.ps1','NativeUpgradePair.ps1','NativeUpgradeService.ps1','Start-NativeUpgrade.ps1')){$sources[$name]='installer\'+$name}
foreach($name in @('Install-NativeComponents.ps1','NativeMaintenance.ps1','AgentLease.ps1')){$sources[$name]=$name}
$pins=Get-NativeBundleSourcePins $PSScriptRoot $manifest $sources
$archive=[IO.Path]::GetFullPath($Package);Assert-NativeUpgradePath $archive
if((Get-NativeUpgradeHash $archive) -ine $Sha256){throw 'Native package digest mismatch.'}
$svc=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'"
if(!$svc){
    if([IO.Directory]::Exists($agent) -and @(Get-ChildItem -LiteralPath $agent -Force).Count){throw 'An existing protected installation needs recovery before first setup.'}
    # There is no prior pair to restore on first setup. No profile or start
    # intent exists, so this path cannot connect a route.
    & "$PSScriptRoot\Install-NativeAgent.ps1" -UserSid $UserSid -UserProfile $UserProfile
    & "$PSScriptRoot\Install-NativeComponents.ps1" -Package $archive -Sha256 $Sha256
    'Native bundle installed.';return
}
$config=[IO.File]::ReadAllText((Join-Path $agent 'agent-config.json'))|ConvertFrom-Json
if($config.UserSid -cne $UserSid -or [IO.Path]::GetFullPath($config.AppRoot).TrimEnd('\') -ine [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')){throw 'Native bundle belongs to another installation.'}
$resumeInitial=$false;$lease=Open-NativeUpgradeLease $agent
try{
    $s=Get-NativeUpgradeServiceSnapshot $lease
    if($s.State -cne 'Running' -or $s.ProcessId -le 0){throw 'Native setup requires the existing service to be running.'}
    $held=Get-Process -Id $s.ProcessId -ErrorAction Stop;$null=$held.Handle
    try{
        if($held.HasExited -or $held.Path -ine (Join-Path $agent 'RoutePilot.Agent.exe')){throw 'Native setup service identity differs.'}
        Initialize-NativeUpgradePipe
        if(![IO.Directory]::Exists((Join-Path $agent 'native'))){
            # Retry only the known first-setup boundary. Never replace an old
            # agent or an incomplete existing native directory sequentially.
            if((Get-NativeUpgradeHash (Join-Path $agent 'RoutePilot.Agent.exe')) -cne $pins['Incoming.Agent.exe']){throw 'Incomplete setup uses another agent; recovery is required.'}
            $reply=[Cita.NativeUpgradePipe]::Exchange('{"Command":"status"}',$s.ProcessId)|ConvertFrom-Json
            if(!$reply.Success -or $reply.Native -or !$reply.State -or $reply.State.Active -isnot [bool] -or $reply.State.Active){throw 'Initial setup retry state is not empty.'}
            $resumeInitial=$true
        }elseif(![IO.File]::Exists((Join-Path $agent 'native\intent.json'))){
            # Older templates held their false intent only in memory. Ask the
            # authenticated service to persist its own stopped state; never
            # invent an intent file while that service is live.
            $before=[Cita.NativeUpgradePipe]::Exchange('{"Command":"native-status"}',$s.ProcessId)|ConvertFrom-Json
            if(!$before.Success){throw 'Legacy template state unavailable.'};Assert-NativePreparationState $before.Native
            $reply=[Cita.NativeUpgradePipe]::Exchange((@{Command='native-stop';RunId=$before.Native.operation}|ConvertTo-Json -Compress),$s.ProcessId)|ConvertFrom-Json
            if(!$reply.Success){throw 'Legacy template persistence refused.'}
            $clock=[Diagnostics.Stopwatch]::StartNew()
            do{
                $reply=[Cita.NativeUpgradePipe]::Exchange('{"Command":"native-status"}',$s.ProcessId)|ConvertFrom-Json
                if(!$reply.Success -or $reply.Native.operation -cne $before.Native.operation -or $reply.Native.generation -ne $before.Native.generation -or $reply.Native.desired){throw 'Legacy template operation changed.'}
                if($reply.Native.phase -ceq $before.Native.phase){break};if($reply.Native.phase -cne 'Stopping'){throw 'Legacy template persistence failed.'};Start-Sleep -Milliseconds 200
            }while($clock.Elapsed.TotalSeconds -lt 35)
            if($reply.Native.phase -cne $before.Native.phase -or ![IO.File]::Exists((Join-Path $agent 'native\intent.json'))){throw 'Legacy template persistence is unconfirmed.'}
        }
        if($held.HasExited -or (Get-NativeUpgradeServiceSnapshot $lease).ProcessId -ne $s.ProcessId){throw 'Native setup service changed.'}
    }finally{$held.Dispose()}
}finally{$lease.Handle.Dispose()}
if($resumeInitial){& "$PSScriptRoot\Install-NativeComponents.ps1" -Package $archive -Sha256 $Sha256;'Native initial setup resumed.';return}
$id=[guid]::NewGuid().ToString('N');$root=Join-Path $agent ('native-update-'+$id);Assert-NativeUpgradePath $root
if(-not('CitaNativeUpgradeDirectory' -as [type])){Add-Type 'using System;using System.Runtime.InteropServices;public static class CitaNativeUpgradeDirectory{[DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]public static extern bool CreateDirectoryW(string path,IntPtr security);}'}
if(![CitaNativeUpgradeDirectory]::CreateDirectoryW($root,[IntPtr]::Zero)){throw 'Native update directory already exists or is unavailable.'}
$acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'));$acl.SetAccessRuleProtection($true,$false)
foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))};Set-Acl -LiteralPath $root -AclObject $acl
foreach($name in $sources.Keys){
    $source=Join-Path $PSScriptRoot $sources[$name];$target=Join-Path $root $name
    $read=[IO.File]::Open($source,'Open','Read','Read');$write=$null
    try{$write=[IO.File]::Open($target,'CreateNew','Write','None');$read.CopyTo($write);$write.Flush($true)}finally{if($write){$write.Dispose()};$read.Dispose()}
    if((Get-NativeUpgradeHash $target) -cne $pins[$name]){throw 'Copied native bundle source differs.'}
}
$read=[IO.File]::Open($archive,'Open','Read','Read');$write=$null
try{$write=[IO.File]::Open((Join-Path $root 'native-components.zip'),'CreateNew','Write','None');$read.CopyTo($write);$write.Flush($true)}finally{if($write){$write.Dispose()};$read.Dispose()}
if((Get-NativeUpgradeHash (Join-Path $root 'native-components.zip')) -ine $Sha256){throw 'Copied native archive differs.'}
$starter=Join-Path $root 'Start-NativeUpgrade.ps1'
# Start in its own script scope so PSScriptRoot resolves to the protected payload.
& $starter -RunRoot $root -ExpectedHelperSha $pins['Cita.NativeUpdater.exe'] -NewAgentSha $pins['Incoming.Agent.exe'] -ArchiveSha $Sha256.ToUpperInvariant()|Out-Null
$deadline=[datetime]::UtcNow.AddMinutes(8)
do {
    $ready=$true
    foreach($role in @('primary','backup')){
        $path=Join-Path $root ('verified-'+$role+'.json')
        if(![IO.File]::Exists($path)){$ready=$false;continue}
        $v=[IO.File]::ReadAllText($path)|ConvertFrom-Json
        if($v.Run -cne $id -or $v.Success -isnot [bool] -or !$v.Success -or $v.ActorProcesses -ne 0 -or $v.RecoveryProcesses -ne 0){throw 'Native bundle completion witness invalid.'}
    }
    if($ready){break};Start-Sleep -Milliseconds 250
}while([datetime]::UtcNow -lt $deadline)
if(!$ready){throw 'Native bundle recovery is unconfirmed. Keep the protected update journal for diagnosis.'}
$lease=Open-NativeUpgradeLease $agent
try{
    $j=Read-NativeUpgradeJournal $lease
    if($j.id -cne $id -or (Test-Path -LiteralPath (Join-Path $agent 'native-upgrade.pending'))){throw 'Native bundle terminal state differs.'}
    if($j.phase -ceq 'RolledBack'){throw 'Native bundle update was rejected; the previous components were restored.'}
    if($j.phase -cne 'Committed'){throw 'Native bundle update was not committed.'}
    if((Get-NativeUpgradeHash (Join-Path $agent 'RoutePilot.Agent.exe')) -cne $j.newAgentSha){throw 'Installed native agent digest differs.'}
    $actual=Get-NativeUpgradeInventory $agent
    if((ConvertTo-Json -InputObject @($actual) -Depth 6 -Compress) -cne (ConvertTo-Json -InputObject @($j.newNative) -Depth 6 -Compress)){throw 'Installed native inventory differs.'}
    $upgradeInputRecord=[IO.File]::ReadAllText((Join-Path $root 'upgrade-input.json'))|ConvertFrom-Json
    $proof=[IO.File]::ReadAllText((Join-Path $root 'started-service.json'))|ConvertFrom-Json
    $reply=Invoke-NativeUpgradeServiceRpc $lease $proof 'native-status' $id
    Assert-NativeUpgradeReadyReply $reply $upgradeInputRecord.Operation $upgradeInputRecord.Generation $upgradeInputRecord.ExpectedPhase
    $state=Get-NativeUpgradeServiceSnapshot $lease
    if($state.StartType -cne $upgradeInputRecord.OriginalStartType){throw 'Native service startup mode was not restored.'}
    # Retain a small protected receipt, not the executable update payload. Never
    # retire recovery tasks while a helper/worker is still alive.
    $clock=[Diagnostics.Stopwatch]::StartNew()
    do{
        $actors=@(Get-CimInstance Win32_Process -Filter "Name='Cita.NativeUpdater.exe' OR Name='powershell.exe'"|Where-Object {$_.ProcessId -ne $PID -and ($_.ExecutablePath -ieq (Join-Path $root 'Cita.NativeUpdater.exe') -or ($_.Name -ieq 'powershell.exe' -and $_.CommandLine -like ('*'+$root+'*')))})
        if(!$actors.Count){break};Start-Sleep -Milliseconds 100
    }while($clock.Elapsed.TotalSeconds -lt 15)
    if($actors.Count){throw 'Native bundle cleanup pending: temporary worker is still running.'}
    $task='CitaNativeUpgrade-'+$id
    $tasks=@(
        [pscustomobject]@{Name=$task;Mode='--primary';Sid='S-1-5-18'}
        [pscustomobject]@{Name=($task+'-Backup');Mode='--backup';Sid='S-1-5-18'}
        [pscustomobject]@{Name=($task+'-Boot');Mode='--recover-on-boot';Sid='S-1-5-18'}
        [pscustomobject]@{Name=($task+'-Controller');Mode='--controller';Sid=$UserSid}
    )
    foreach($row in $tasks){
        $t=Get-ScheduledTask -TaskName $row.Name -ErrorAction Stop;$sid=[string]$t.Principal.UserId
        if($sid -cnotmatch '^S-1-'){$sid=([Security.Principal.NTAccount]::new($sid)).Translate([Security.Principal.SecurityIdentifier]).Value}
        if($t.State -notin @('Ready','Disabled') -or $sid -cne $row.Sid -or @($t.Actions).Count -ne 1 -or $t.Actions[0].Execute -ine (Join-Path $root 'Cita.NativeUpdater.exe') -or $t.Actions[0].Arguments -cne ($row.Mode+' "'+$root+'"')){throw 'Native update task identity differs.'}
    }
    foreach($row in $tasks){Disable-ScheduledTask -TaskName $row.Name|Out-Null}
    if(@(Get-CimInstance Win32_Process -Filter "Name='Cita.NativeUpdater.exe' OR Name='powershell.exe'"|Where-Object {$_.ProcessId -ne $PID -and ($_.ExecutablePath -ieq (Join-Path $root 'Cita.NativeUpdater.exe') -or ($_.Name -ieq 'powershell.exe' -and $_.CommandLine -like ('*'+$root+'*')))}).Count){throw 'Native worker appeared during cleanup.'}
    $receipt=[ordered]@{schema=1;id=$id;phase=$j.phase;agentSha=$j.newAgentSha;nativeFiles=@($actual).Count;operation=$upgradeInputRecord.Operation;generation=$upgradeInputRecord.Generation;state=$upgradeInputRecord.ExpectedPhase;temporaryProcesses=0;utc=[datetime]::UtcNow.ToString('O')}
    Write-NativeUpgradeJournal (Join-Path $agent ('native-update-result-'+$id+'.json')) $receipt
    foreach($row in $tasks){if((Get-ScheduledTask -TaskName $row.Name).State -eq 'Running'){throw 'Native update task restarted.'};Unregister-ScheduledTask -TaskName $row.Name -Confirm:$false}
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($root)) -ine $agent -or [IO.Path]::GetFileName($root) -cne ('native-update-'+$id)){throw 'Native cleanup root escaped scope.'}
    Assert-NativeUpgradePath $root;$files=@(Get-ChildItem -LiteralPath $root -Force)
    if($files.Count -gt 128){throw 'Unexpected native payload inventory.'}
    foreach($f in $files){if($f.PSIsContainer -or ($f.Attributes -band [IO.FileAttributes]::ReparsePoint) -or [IO.Path]::GetDirectoryName($f.FullName) -ine $root){throw 'Unexpected native cleanup child.'}}
    foreach($f in $files){Remove-Item -LiteralPath $f.FullName -Force};Remove-Item -LiteralPath $root
    if(Test-Path -LiteralPath $root){throw 'Native payload cleanup is incomplete.'}
}finally{$lease.Handle.Dispose()}
'Native bundle update committed.'
