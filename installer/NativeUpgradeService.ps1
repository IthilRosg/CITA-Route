# Service side of the native pair transaction. This library is deliberately not
# an entry point: its caller must hold the transaction lease, the independent
# recovery worker/job and the disconnected-intent lease for the whole operation.
function Assert-NativeUpgradeWriter([scriptblock]$AssertWriter) {
    if(!$AssertWriter){throw 'Native upgrade writer proof required.'}
    $value=& $AssertWriter
    if($value -isnot [bool] -or !$value){throw 'Native upgrade writer is not held.'}
}
function Assert-NativeUpgradeServiceDescription($Service,[string]$Root) {
    $fixed=[IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'RoutePilot\Agent')).TrimEnd('\')
    if([IO.Path]::GetFullPath($Root).TrimEnd('\') -ine $fixed){throw 'Native upgrade service root differs.'}
    if(!$Service -or $Service.Name -cne 'RoutePilotRescue' -or $Service.StartName -cne 'LocalSystem' -or
       $Service.ServiceType -cne 'Own Process' -or $Service.PathName -ine ('"'+(Join-Path $fixed 'RoutePilot.Agent.exe')+'" --service') -or
       $Service.StartMode -notin @('Auto','Manual','Disabled') -or $Service.State -notin @('Running','Stopped','Start Pending','Stop Pending')){throw 'Native upgrade service identity differs.'}
}
function Get-NativeUpgradeServiceSnapshot($Lease) {
    Assert-NativeUpgradeLease $Lease
    Assert-NativeUpgradePath $Lease.Root
    $s=Get-CimInstance Win32_Service -Filter "Name='RoutePilotRescue'" -ErrorAction Stop
    Assert-NativeUpgradeServiceDescription $s $Lease.Root
    $registry=Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\RoutePilotRescue' -ErrorAction Stop
    $delayed=$registry.DelayedAutoStart
    if($null -ne $delayed -and ($delayed -isnot [int] -or $delayed -notin @(0,1))){throw 'Native upgrade delayed startup value invalid.'}
    $start=if($s.StartMode -ceq 'Auto'){if($delayed -eq 1){'AutomaticDelayedStart'}else{'Automatic'}}elseif($s.StartMode -ceq 'Manual'){'Manual'}else{'Disabled'}
    return [pscustomobject]@{State=$s.State;ProcessId=[int]$s.ProcessId;StartType=$start}
}
function Test-NativeUpgradeStoppedFence($Lease) {
    $state=Get-NativeUpgradeServiceSnapshot $Lease
    return ($state.State -ceq 'Stopped' -and $state.ProcessId -eq 0 -and $state.StartType -ceq 'Disabled')
}
function Set-NativeUpgradeFailureFence($Lease) {
    # Called only after the fixed service was admitted under the writer lease.
    # Attempt Disabled even when CIM observation caused the original failure.
    # A fresh query is confirmation, never a prerequisite for this attempt.
    try {
        Set-Service -Name RoutePilotRescue -StartupType Disabled -ErrorAction Stop
        if((Get-NativeUpgradeServiceSnapshot $Lease).StartType -cne 'Disabled'){throw 'Disabled was not confirmed.'}
    }catch{throw 'Native upgrade recovery unconfirmed; independent recovery required.'}
}
function Stop-NativeUpgradeService($Lease,[string]$Id,[scriptblock]$AssertWriter) {
    Assert-NativeUpgradeWriter $AssertWriter
    $j=Read-NativeUpgradeJournal $Lease
    if($j.id -cne $Id -or $j.phase -cne 'Prepared'){throw 'Native upgrade stop phase differs.'}
    $marker=Assert-NativeUpgradeMarker $Lease $Id;$marker.Dispose()
    $state=Get-NativeUpgradeServiceSnapshot $Lease
    if($state.StartType -cne $j.originalStartType -or $state.State -notin @('Running','Stopped')){throw 'Native upgrade initial service state differs.'}
    $agent=Join-Path $Lease.Root 'RoutePilot.Agent.exe'
    $image=[IO.FileStream]::new($agent,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $process=$null
    try {
        if((Get-NativeUpgradeHash $agent) -cne $j.oldAgentSha){throw 'Native upgrade initial service image differs.'}
        if($state.State -ceq 'Running'){
            if($state.ProcessId -le 0){throw 'Native upgrade service process unavailable.'}
            $process=Get-Process -Id $state.ProcessId -ErrorAction Stop
            $null=$process.Handle
            if($process.HasExited -or $process.Path -ine $agent){throw 'Native upgrade service process differs.'}
            $check=Get-NativeUpgradeServiceSnapshot $Lease
            if($check.ProcessId -ne $state.ProcessId -or $check.State -cne 'Running'){throw 'Native upgrade service changed before stop.'}
        }elseif($state.ProcessId -ne 0){throw 'Stopped service still has a process.'}
        # Disabled survives controller death and reboot. Never restore startup
        # here on failure: the independent recovery worker owns that decision.
        Set-Service -Name RoutePilotRescue -StartupType Disabled -ErrorAction Stop
        if((Get-NativeUpgradeServiceSnapshot $Lease).StartType -cne 'Disabled'){throw 'Native upgrade boot fence unconfirmed.'}
        Assert-NativeUpgradeWriter $AssertWriter
        if($state.State -ceq 'Running'){
            Stop-Service -Name RoutePilotRescue -ErrorAction Stop
            (Get-Service -Name RoutePilotRescue -ErrorAction Stop).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(35))
            if(!$process.WaitForExit(5000)){throw 'Native upgrade service process exit unconfirmed.'}
        }
        if(!(Test-NativeUpgradeStoppedFence $Lease)){throw 'Native upgrade stopped fence unconfirmed.'}
        Assert-NativeUpgradeWriter $AssertWriter
        Set-NativeUpgradePhase $Lease $Id 'ServiceStopped'
    }finally{if($process){$process.Dispose()};$image.Dispose()}
}
function Assert-NativeUpgradeReadyReply($Reply,[string]$Operation,[long]$Generation,[string]$ExpectedPhase='Disconnected') {
    if($Operation -cnotmatch '^[a-f0-9]{32}$' -or $Generation -lt 0){throw 'Native upgrade expected session invalid.'}
    if($ExpectedPhase -cnotin @('Disconnected','Unconfigured')){throw 'Native upgrade expected phase invalid.'}
    if(!$Reply -or $Reply.Success -isnot [bool] -or !$Reply.Success -or !$Reply.Native){throw 'Native upgrade validation was refused.'}
    $n=$Reply.Native
    if($n.desired -isnot [bool] -or $n.desired -or $n.operation -cne $Operation -or
       ($n.generation -isnot [long] -and $n.generation -isnot [int]) -or $n.generation -ne $Generation -or
       $n.phase -cne $ExpectedPhase -or $n.errorCode){throw 'Native upgrade native readiness unconfirmed.'}
}
function Start-NativeUpgradeValidationService($Lease,[string]$Id,[scriptblock]$AssertWriter) {
    Assert-NativeUpgradeWriter $AssertWriter
    $j=Read-NativeUpgradeJournal $Lease
    if($j.id -cne $Id -or $j.phase -cne 'NativePromoted' -or !(Test-NativeUpgradeStoppedFence $Lease)){throw 'Native upgrade validation start refused.'}
    $marker=Assert-NativeUpgradeMarker $Lease $Id
    $image=$null;$process=$null
    try {
        $agent=Join-Path $Lease.Root 'RoutePilot.Agent.exe'
        $image=[IO.FileStream]::new($agent,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        if((Get-NativeUpgradeHash $agent) -cne $j.newAgentSha){throw 'Native upgrade start image differs.'}
        $inventory=Get-NativeUpgradeInventory $Lease.Root
        if((ConvertTo-Json -InputObject @($inventory) -Depth 6 -Compress) -cne (ConvertTo-Json -InputObject @($j.newNative) -Depth 6 -Compress)){throw 'Native upgrade start native pair differs.'}
        Assert-NativeUpgradeWriter $AssertWriter
        # Only a complete new pair may leave Disabled. The verified new agent
        # must implement the persistent maintenance gate; startup cannot route.
        Set-Service -Name RoutePilotRescue -StartupType Manual -ErrorAction Stop
        $before=Get-NativeUpgradeServiceSnapshot $Lease
        if($before.State -cne 'Stopped' -or $before.ProcessId -ne 0 -or $before.StartType -cne 'Manual'){throw 'Native upgrade validation start fence changed.'}
        Start-Service -Name RoutePilotRescue -ErrorAction Stop
        (Get-Service -Name RoutePilotRescue -ErrorAction Stop).WaitForStatus('Running',[TimeSpan]::FromSeconds(20))
        $state=Get-NativeUpgradeServiceSnapshot $Lease
        if($state.State -cne 'Running' -or $state.ProcessId -le 0){throw 'Native upgrade validation process missing.'}
        $process=Get-Process -Id $state.ProcessId -ErrorAction Stop;$null=$process.Handle
        if($process.HasExited -or $process.Path -ine $agent){throw 'Native upgrade validation process differs.'}
        $ticks=$process.StartTime.ToUniversalTime().Ticks
        $proof=[pscustomobject]@{Id=$Id;AgentSha=$j.newAgentSha;ProcessId=$state.ProcessId;StartedUtcTicks=$ticks}
        Assert-NativeUpgradeWriter $AssertWriter
        $cap=Invoke-NativeUpgradeServiceRpc $Lease $proof 'native-capabilities' $Id
        foreach($name in @('maintenanceGateProtocol','maintenanceValidationProtocol')){
            $version=$cap.Native.$name
            if(($version -isnot [int] -and $version -isnot [long]) -or $version -ne 1){throw 'Native upgrade maintenance protocols unavailable.'}
        }
        return $proof
    }catch{
        # Never claim recovery or start the old image here. This durable fence
        # leaves recovery to the independent actor with the pair journal.
        Set-NativeUpgradeFailureFence $Lease
        throw
    }finally{if($process){$process.Dispose()};if($image){$image.Dispose()};$marker.Dispose()}
}
function Initialize-NativeUpgradePipe {
    if('Cita.NativeUpgradePipe' -as [type]){return}
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
namespace Cita {
    public static class NativeUpgradePipe {
        [DllImport("kernel32.dll", SetLastError=true)]
        static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
        public static string Exchange(string request, int expectedPid) {
            if(expectedPid<=0 || request==null || request.Length>4096 || request.IndexOf('\n')>=0 || request.IndexOf('\r')>=0)
                throw new IOException("Native upgrade request refused.");
            using(var pipe=new NamedPipeClientStream(".","RoutePilot.Rescue.v1",PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification)) {
                pipe.Connect(5000);
                uint pid;
                if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out pid) || pid!=(uint)expectedPid)
                    throw new IOException("Native upgrade pipe server differs.");
                var exchange=Task.Run(async delegate {
                    byte[] bytes=Encoding.UTF8.GetBytes(request+"\n");
                    await pipe.WriteAsync(bytes,0,bytes.Length).ConfigureAwait(false);
                    await pipe.FlushAsync().ConfigureAwait(false);
                    using(var result=new MemoryStream()) {
                        byte[] one=new byte[1];
                        while(true) {
                            if(await pipe.ReadAsync(one,0,1).ConfigureAwait(false)!=1)throw new IOException("Native upgrade response ended early.");
                            if(one[0]==10)return new UTF8Encoding(false,true).GetString(result.ToArray());
                            if(result.Length>=65536)throw new IOException("Native upgrade response too large.");
                            result.WriteByte(one[0]);
                        }
                    }
                });
                try {
                    if(!exchange.Wait(5000))throw new IOException("Native upgrade response deadline exceeded.");
                    return exchange.GetAwaiter().GetResult();
                } finally {
                    pipe.Dispose();
                    // Dispose cancels the outstanding overlapped I/O. Observe
                    // any later exception; never block recovery indefinitely.
                    if(!exchange.IsCompleted)exchange.ContinueWith(t=>{var e=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
                }
            }
        }
    }
}
'@ -ErrorAction Stop
}
function Invoke-NativeUpgradeServiceRpc($Lease,$StartedService,[string]$Command,[string]$Id,[switch]$AllowNativeStartupPending) {
    # StartedService is returned by Start-NativeUpgradeValidationService while
    # holding the pinned image before SCM START. A disk hash alone is not proof
    # that an older already-running process loaded those bytes.
    if(!$StartedService -or $StartedService.Id -cne $Id -or $StartedService.AgentSha -cnotmatch '^[A-F0-9]{64}$' -or $StartedService.ProcessId -le 0 -or $StartedService.StartedUtcTicks -le 0 -or $Command -cnotin @('native-capabilities','native-status','native-maintenance-validate','native-maintenance-status') -or $Id -cnotmatch '^[a-f0-9]{32}$'){throw 'Native upgrade RPC scope refused.'}
    $state=Get-NativeUpgradeServiceSnapshot $Lease
    if($state.State -cne 'Running' -or $state.ProcessId -ne $StartedService.ProcessId){throw 'Native upgrade service is not running as started.'}
    $agent=Join-Path $Lease.Root 'RoutePilot.Agent.exe'
    $image=[IO.FileStream]::new($agent,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $process=$null
    try {
        if((Get-NativeUpgradeHash $agent) -cne $StartedService.AgentSha){throw 'Native upgrade RPC image differs.'}
        $process=Get-Process -Id $state.ProcessId -ErrorAction Stop;$null=$process.Handle
        if($process.HasExited -or $process.Path -ine $agent -or $process.StartTime.ToUniversalTime().Ticks -ne $StartedService.StartedUtcTicks){throw 'Native upgrade RPC process differs.'}
        Initialize-NativeUpgradePipe
        $json=[Cita.NativeUpgradePipe]::Exchange((@{Command=$Command;RunId=$Id}|ConvertTo-Json -Compress),$state.ProcessId)
        $after=Get-NativeUpgradeServiceSnapshot $Lease
        if($process.HasExited -or $after.State -cne 'Running' -or $after.ProcessId -ne $state.ProcessId){throw 'Native upgrade RPC service changed.'}
        $reply=$json|ConvertFrom-Json -ErrorAction Stop
        if($reply.Success -isnot [bool]){throw 'Native upgrade RPC was refused.'}
        if(!$reply.Success -and !($AllowNativeStartupPending -and $Command -ceq 'native-status' -and $null -eq $reply.Native)){throw 'Native upgrade RPC was refused.'}
        return $reply
    }finally{if($process){$process.Dispose()};$image.Dispose()}
}
