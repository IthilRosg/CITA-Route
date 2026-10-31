# Loaded only by the pinned Actor/Recover scripts in an administrator-owned run.
. "$PSScriptRoot\NativeUpgradeTransaction.ps1"
. "$PSScriptRoot\NativeUpgradePair.ps1"
. "$PSScriptRoot\NativeUpgradeService.ps1"
. "$PSScriptRoot\NativeUpgradeFlow.ps1"
. "$PSScriptRoot\NativeUpgradeRecovery.ps1"
if(-not(Get-Command Get-FileHash -ErrorAction SilentlyContinue)){
    function Get-FileHash {param([Parameter(Position=0)][Alias('LiteralPath')][string]$Path,[string]$Algorithm='SHA256')
        if($Algorithm -cne 'SHA256'){throw 'Worker hash algorithm refused.'}
        return [pscustomobject]@{Hash=(Get-NativeUpgradeHash $Path)}
    }
}
function Read-NativeUpgradeWorkerJson($Context,[string]$Name,[int]$Limit=65536) {
    if($Name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9.-]{0,99}$' -or $Limit -lt 1 -or $Limit -gt 1048576){throw 'Upgrade record scope refused.'}
    $path=Join-Path $Context.Root $Name;Assert-NativeUpgradePath $path
    $f=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{if($f.Length -lt 1 -or $f.Length -gt $Limit){throw 'Upgrade record size refused.'};$r=[IO.StreamReader]::new($f,[Text.Encoding]::UTF8,$true,4096,$true);try{return ($r.ReadToEnd()|ConvertFrom-Json)}finally{$r.Dispose()}}finally{$f.Dispose()}
}
function Save-NativeUpgradeWorkerJson($Context,[string]$Name,$Value) {
    if($Name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9.-]{0,99}$' -or $Name -in @('plan.json','upgrade-input.json','baseline.json')){throw 'Upgrade output scope refused.'}
    $path=Join-Path $Context.Root $Name;Assert-NativeUpgradePath $path
    $tmp=$path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try{
        $f=[IO.FileStream]::new($tmp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        try{$bytes=[Text.Encoding]::UTF8.GetBytes(($Value|ConvertTo-Json -Depth 8 -Compress));$f.Write($bytes,0,$bytes.Length);$f.Flush($true)}finally{$f.Dispose()}
        $acl=[Security.AccessControl.FileSecurity]::new();$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'));$acl.SetAccessRuleProtection($true,$false)
        foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','Allow'))}
        Set-Acl -LiteralPath $tmp -AclObject $acl
        if([IO.File]::Exists($path)){[IO.File]::Replace($tmp,$path,[NullString]::Value)}else{[IO.File]::Move($tmp,$path)}
    }finally{if([IO.File]::Exists($tmp)){[IO.File]::Delete($tmp)}}
}
function Initialize-NativeUpgradeJobInspection {
    if('Cita.UpgradeJobInspection' -as [type]){return}
    Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;using System.ComponentModel;
namespace Cita {public static class UpgradeJobInspection {
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool DuplicateHandle(IntPtr source,IntPtr handle,IntPtr target,out IntPtr result,uint access,bool inherit,uint options);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool result);
 [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
 public static bool Contains(IntPtr parent,long value,IntPtr child){
  if(value<=0)throw new ArgumentException("Job handle refused.");IntPtr job;
  if(!DuplicateHandle(parent,new IntPtr(value),new IntPtr(-1),out job,4,false,0))throw new Win32Exception(Marshal.GetLastWin32Error());
  try{bool result;if(!IsProcessInJob(child,job,out result))throw new Win32Exception(Marshal.GetLastWin32Error());return result;}finally{CloseHandle(job);}
 }
}}
'@ -ErrorAction Stop
}
function Hold-NativeUpgradeWorkerProcess($Proof,[string]$Image,[bool]$AllowDead=$false) {
    if(!$Proof -or $Proof.Pid -le 0 -or $Proof.StartedUtcTicks -le 0){throw 'Upgrade process proof invalid.'}
    try{$p=Get-Process -Id $Proof.Pid -ErrorAction Stop}catch [Microsoft.PowerShell.Commands.ProcessCommandException]{if($AllowDead){return $null};throw}
    try{
        $null=$p.Handle
        if($p.HasExited){if($AllowDead){$p.Dispose();return $null};throw 'Upgrade process exited.'}
        if($p.StartTime.ToUniversalTime().Ticks -ne $Proof.StartedUtcTicks){if($AllowDead){$p.Dispose();return $null};throw 'Upgrade PID was reused.'}
        if($p.Path -ine $Image){throw 'Upgrade process image differs.'}
        return $p
    }catch{$p.Dispose();throw}
}
function Open-NativeUpgradeWorker([string]$RunRoot,[string]$Role,[bool]$Recovery) {
    $ErrorActionPreference='Stop'
    $fixed=Join-Path $env:ProgramFiles 'RoutePilot\Agent';$run=[IO.Path]::GetFullPath($RunRoot).TrimEnd('\');$leaf=[IO.Path]::GetFileName($run)
    if([IO.Path]::GetDirectoryName($run) -ine $fixed -or $leaf -cnotmatch '^native-update-[a-f0-9]{32}$'){throw 'Upgrade worker root refused.'}
    Assert-NativeUpgradePath $run
    $ctx=[pscustomobject]@{Root=$run;Id=$leaf.Substring(14);Recovery=$Recovery;Role=$Role;Plan=$null;Input=$null;Lease=$null;Parent=$null;SourceController=$null;Holders=[Collections.Generic.List[object]]::new();Locks=[Collections.Generic.List[object]]::new();OldIntent=$null;OwnJob=0L;Deadline=[datetime]::UtcNow.AddSeconds(140);Proof=$null;Coordinated=$false}
    try{
        $ctx.Plan=Read-NativeUpgradeWorkerJson $ctx 'plan.json' 16384;$ctx.Input=Read-NativeUpgradeWorkerJson $ctx 'upgrade-input.json'
        if($ctx.Plan.Run -cne $ctx.Id -or $ctx.Input.Run -cne $ctx.Id -or $ctx.Input.Operation -cnotmatch '^[a-f0-9]{32}$' -or $ctx.Input.Generation -lt 0 -or $ctx.Input.OldAgentSha -cnotmatch '^[A-F0-9]{64}$' -or $ctx.Input.ExpectedPhase -cnotin @('Disconnected','Unconfigured')){throw 'Upgrade worker input identity invalid.'}
        $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        if((!$Recovery -and ($Role -cne 'controller' -or $sid -cne $ctx.Plan.OwnerSid)) -or ($Recovery -and ($Role -cnotin @('primary','backup','recover-on-boot') -or $sid -cne 'S-1-5-18'))){throw 'Upgrade worker role refused.'}
        $image=Join-Path $run 'Cita.NativeUpdater.exe'
        if((Get-NativeUpgradeHash $image) -cne $ctx.Plan.HelperSha){throw 'Upgrade supervisor bytes differ.'}
        $source=Read-NativeUpgradeWorkerJson $ctx 'controller.json'
        $parentProof=if($Recovery){Read-NativeUpgradeWorkerJson $ctx ('recovery-controller-'+$Role+'.json')}else{$source}
        if($parentProof.Run -cne $ctx.Id){throw 'Upgrade parent identity differs.'}
        $ctx.Parent=Hold-NativeUpgradeWorkerProcess $parentProof.Process $image
        $actual=Get-CimInstance Win32_Process -Filter ('ProcessId='+$PID)
        if($actual.ParentProcessId -ne $ctx.Parent.Id){throw 'Upgrade worker parent differs.'}
        $ctx.OwnJob=if($Recovery){[long]$parentProof.JobHandle}else{[long]$source.ActorJob}
        Initialize-NativeUpgradeJobInspection
        $self=[Diagnostics.Process]::GetCurrentProcess();try{if(![Cita.UpgradeJobInspection]::Contains($ctx.Parent.Handle,$ctx.OwnJob,$self.Handle)){throw 'Upgrade worker is not in the supervisor job.'}}finally{$self.Dispose()}
        $boot=[string](Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().Ticks
        if($Recovery -and $Role -ceq 'recover-on-boot'){
            if($boot -ceq $ctx.Plan.Boot){throw 'Old boot retirement is unconfirmed.'}
        }else{
            if($boot -cne $ctx.Plan.Boot){throw 'Upgrade boot changed.'}
            $ctx.SourceController=Hold-NativeUpgradeWorkerProcess $source.Process $image $Recovery
            if($ctx.SourceController){$ctx.Holders.Add([pscustomobject]@{Name='controller';Process=$ctx.SourceController})}
            foreach($name in @('primary','backup')){
                $ready=Read-NativeUpgradeWorkerJson $ctx ('ready-'+$name+'.json')
                if($ready.Run -cne $ctx.Id -or $ready.Role -cne $name -or !$ready.JobHeld){throw 'Upgrade watcher descriptor differs.'}
                $held=Hold-NativeUpgradeWorkerProcess $ready.Process $image $Recovery
                if($held){$ctx.Holders.Add([pscustomobject]@{Name=$name;Process=$held})}
            }
        }
        if(!$Recovery){$ctx.Deadline=[datetime]::Parse($ctx.Plan.DeadlineUtc).ToUniversalTime()}
        $config=Join-Path $fixed 'agent-config.json';Assert-NativeUpgradePath $config
        $configLease=[IO.File]::Open($config,'Open','Read','Read');$ctx.Locks.Add($configLease)
        $configText=[IO.File]::ReadAllText($config)|ConvertFrom-Json
        if($configText.UserSid -cne $ctx.Plan.OwnerSid -or [IO.Path]::GetFullPath($configText.AppRoot).TrimEnd('\') -ine [IO.Path]::GetFullPath($ctx.Input.ApplicationRoot).TrimEnd('\')){throw 'Upgrade application owner differs.'}
        $runtime=Join-Path $ctx.Input.ApplicationRoot 'runtime';Assert-NativeUpgradePath $runtime
        foreach($name in @('connection-operation.lock','setup.lock','upgrade.lock','network-control.lock','installation-maintenance.lock')){
            $path=Join-Path $runtime $name;Assert-NativeUpgradePath $path;$ctx.Locks.Add([IO.File]::Open($path,'OpenOrCreate','ReadWrite','None'))
        }
        $ctx.Lease=Open-NativeUpgradeLease $fixed
        if(!$Recovery){$ctx.OldIntent=[IO.File]::Open((Join-Path $fixed 'native\intent.json'),'Open','Read','Read');if((Get-NativeUpgradeHash (Join-Path $fixed 'native\intent.json')) -cne $ctx.Plan.IntentSha){throw 'Upgrade initial intent changed.'}}
        $null=Assert-NativeUpgradeWorkerWriter $ctx
        return $ctx
    }catch{Close-NativeUpgradeWorker $ctx;throw}
}
function Assert-NativeUpgradeWorkerWriter($Context) {
    Assert-NativeUpgradeLease $Context.Lease
    if([datetime]::UtcNow -ge $Context.Deadline -or $Context.Parent.HasExited){throw 'Upgrade writer deadline or parent lost.'}
    if(!$Context.Recovery){foreach($holder in $Context.Holders){if($holder.Process.HasExited){throw 'Upgrade independent watcher lost.'}}}
    else{
        if($Context.Role -cne 'recover-on-boot'){
            $retired=Read-NativeUpgradeWorkerJson $Context ('retired-'+$Context.Role+'.json')
            if($retired.run -cne $Context.Id -or $retired.actorProcesses -ne 0 -or !$retired.controllerExited -or ($Context.SourceController -and !$Context.SourceController.HasExited)){throw 'Previous upgrade actor retirement unconfirmed.'}
        }
    }
    $self=[Diagnostics.Process]::GetCurrentProcess();try{if(![Cita.UpgradeJobInspection]::Contains($Context.Parent.Handle,$Context.OwnJob,$self.Handle)){throw 'Upgrade writer job changed.'}}finally{$self.Dispose()}
    return $true
}
function Wait-NativeUpgradeWorkerLeaseBarrier($Context) {
    if(!(Test-NativeUpgradeStoppedFence $Context.Lease)){throw 'Upgrade release service fence unconfirmed.'}
    if($Context.OldIntent){$Context.OldIntent.Dispose();$Context.OldIntent=$null}
    Save-NativeUpgradeWorkerJson $Context 'release-old.json' @{Run=$Context.Id}
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while($timer.Elapsed.TotalSeconds -lt 15){
        $null=Assert-NativeUpgradeWorkerWriter $Context;$all=$true
        foreach($holder in $Context.Holders){
            if($holder.Process.HasExited){if(!$Context.Recovery){throw 'Upgrade holder died during admission.'};continue}
            $name='released-'+$holder.Name+'.json'
            if(![IO.File]::Exists((Join-Path $Context.Root $name))){$all=$false;continue}
            $ack=Read-NativeUpgradeWorkerJson $Context $name
            if($ack.run -cne $Context.Id -or $ack.released -isnot [bool] -or !$ack.released){throw 'Upgrade release acknowledgement invalid.'}
        }
        if($all){return $true};Start-Sleep -Milliseconds 100
    }
    throw 'Upgrade old leases did not retire.'
}
function Restore-NativeUpgradeWorkerStartupType($Context,[string]$StartupType) {
    $null=Assert-NativeUpgradeWorkerWriter $Context
    if($StartupType -ceq 'AutomaticDelayedStart'){
        & "$env:WINDIR\System32\sc.exe" config RoutePilotRescue start= delayed-auto|Out-Null
        if($LASTEXITCODE){throw 'Delayed startup restoration failed.'}
    }elseif($StartupType -cin @('Automatic','Manual','Disabled')){Set-Service -Name RoutePilotRescue -StartupType $StartupType -ErrorAction Stop}else{throw 'Upgrade startup type invalid.'}
    return ((Get-NativeUpgradeServiceSnapshot $Context.Lease).StartType -ceq $StartupType)
}
function Close-NativeUpgradeWorker($Context) {
    if(!$Context){return}
    if($Context.OldIntent){$Context.OldIntent.Dispose()}
    if($Context.Lease){$Context.Lease.Handle.Dispose()}
    foreach($item in $Context.Locks){$item.Dispose()}
    foreach($holder in $Context.Holders){$holder.Process.Dispose()}
    if($Context.Parent){$Context.Parent.Dispose()}
}
