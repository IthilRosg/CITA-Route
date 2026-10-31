$ErrorActionPreference='Stop'
. "$PSScriptRoot\NativeUpgradeTransaction.ps1"
. "$PSScriptRoot\NativeUpgradeService.ps1"
function Reject([scriptblock]$Run){$rejected=$false;try{& $Run}catch{$rejected=$true};if(!$rejected){throw 'Expected native service refusal.'}}
function Assert([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
$fixed=Join-Path $env:ProgramFiles 'RoutePilot\Agent'
$s=[pscustomobject]@{Name='RoutePilotRescue';StartName='LocalSystem';ServiceType='Own Process';PathName=('"'+(Join-Path $fixed 'RoutePilot.Agent.exe')+'" --service');State='Running';StartMode='Auto'}
Assert-NativeUpgradeServiceDescription $s $fixed
foreach($pair in @(@('StartName','user'),@('ServiceType','Share Process'),@('PathName','C:\untrusted.exe'),@('Name','Other'),@('State','Paused'),@('StartMode','Unknown'))){
    $old=$s.($pair[0]);$s.($pair[0])=$pair[1];Reject {Assert-NativeUpgradeServiceDescription $s $fixed};$s.($pair[0])=$old
}
Reject {Assert-NativeUpgradeServiceDescription $s ($fixed+'-other')}
Assert-NativeUpgradeWriter {$true}
foreach($bad in @({$false},{'True'},{@($true,$true)},{throw 'Lost writer'})){Reject {Assert-NativeUpgradeWriter $bad}}
$id='a'*32
function Reply([string]$Phase='Disconnected') {return [pscustomobject]@{Success=$true;Native=[pscustomobject]@{phase=$Phase;desired=$false;operation=$id;generation=[long]7;errorCode=$null}}}
Assert-NativeUpgradeReadyReply (Reply) $id 7
Assert-NativeUpgradeReadyReply (Reply 'Unconfigured') $id 7 'Unconfigured'
Reject {Assert-NativeUpgradeReadyReply (Reply 'Unconfigured') $id 7}
foreach($phase in @('Stopping','Connected','Unavailable','disconnected')){Reject {Assert-NativeUpgradeReadyReply (Reply $phase) $id 7}}
foreach($change in @(@('desired',$true),@('desired','false'),@('generation','7'),@('generation',8),@('operation',('b'*32)),@('errorCode','Failure'))){
    $r=Reply;$r.Native.($change[0])=$change[1];Reject {Assert-NativeUpgradeReadyReply $r $id 7}
}
$r=Reply;$r.Success='true';Reject {Assert-NativeUpgradeReadyReply $r $id 7}
Reject {Assert-NativeUpgradeReadyReply (Reply) $id -1}
Initialize-NativeUpgradePipe
Reject {[Cita.NativeUpgradePipe]::Exchange('{}',0)}
Reject {[Cita.NativeUpgradePipe]::Exchange("{} `n",1)}
Reject {[Cita.NativeUpgradePipe]::Exchange(('x'*4097),1)}

# This section supplies a deliberately fake SCM adapter; it proves ordering and
# durable failure policy on real fixture files, never actual SCM or RPC readiness.
$fixture=[IO.Path]::GetFullPath((Join-Path (Join-Path $PSScriptRoot '../runtime') ('cita-service-unit-'+[guid]::NewGuid().ToString('N'))))
[void][IO.Directory]::CreateDirectory((Join-Path $fixture 'native'))
[IO.File]::WriteAllText((Join-Path $fixture 'RoutePilot.Agent.exe'),'old agent')
[IO.File]::WriteAllText((Join-Path $fixture 'native\file.bin'),'old native')
$lease=$null
try {
    $lease=Open-NativeUpgradeLease $fixture
    $expected=Get-NativeUpgradeInventory $fixture
    $j=Start-NativeUpgrade $lease $id ('B'*64) ('C'*64) 'S-1-5-18' 'Manual' $expected
    $script:fakeState=[pscustomobject]@{State='Stopped';ProcessId=0;StartType='Manual'}
    $script:actions=[Collections.Generic.List[string]]::new()
    $script:readFailure=$false
    function Get-NativeUpgradeServiceSnapshot($Lease){if($script:readFailure){throw 'Simulated CIM observation failed.'};return $script:fakeState}
    function Set-Service([string]$Name,[string]$StartupType){$script:actions.Add($StartupType);$script:fakeState.StartType=$StartupType}
    function Start-Service {throw 'Unexpected service start.'}
    Stop-NativeUpgradeService $lease $id {$true}
    Assert ((Read-NativeUpgradeJournal $lease).phase -ceq 'ServiceStopped') 'Stop did not persist its witness.'
    Assert ($script:actions.Count -eq 1 -and $script:actions[0] -ceq 'Disabled') 'Stop changed more than the durable startup fence.'
    Assert ([IO.File]::Exists((Join-Path $fixture 'native-upgrade.pending'))) 'Stop removed the maintenance gate.'
    # Writer dies after the durable Disabled fence. A finally block must not
    # re-enable or start anything, and the journal cannot claim ServiceStopped.
    $j.phase='Prepared';Write-NativeUpgradeJournal (Join-Path $fixture 'native-upgrade.json') $j
    $script:fakeState.StartType='Manual';$script:actions.Clear();$script:writerCalls=0
    Reject {Stop-NativeUpgradeService $lease $id {$script:writerCalls++;return ($script:writerCalls -eq 1)}}
    Assert ($script:fakeState.StartType -ceq 'Disabled') 'Failed writer lost the durable startup fence.'
    Assert ($script:actions.Count -eq 1) 'Failure restarted or re-enabled the service.'
    Assert ((Read-NativeUpgradeJournal $lease).phase -ceq 'Prepared') 'Failure falsely recorded service stop.'
    Assert ([IO.File]::Exists((Join-Path $fixture 'native-upgrade.pending'))) 'Failure removed the maintenance gate.'
    $script:fakeState.StartType='Manual';$script:actions.Clear();$script:readFailure=$true
    Reject {Set-NativeUpgradeFailureFence $lease}
    Assert ($script:actions.Count -eq 1 -and $script:actions[0] -ceq 'Disabled') 'CIM failure prevented the emergency disable attempt.'
    Assert ($script:fakeState.StartType -ceq 'Disabled') 'CIM failure left startup enabled.'
    $script:readFailure=$false
    Set-NativeUpgradeFailureFence $lease
    # Complete new pair, with identical bytes in this fixture. SCM START must
    # occur while that exact image is held against writing/replacement.
    $j.phase='NativePromoted';$j.newAgentSha=Get-NativeUpgradeHash (Join-Path $fixture 'RoutePilot.Agent.exe')
    Write-NativeUpgradeJournal (Join-Path $fixture 'native-upgrade.json') $j
    $script:fakeState=[pscustomobject]@{State='Stopped';ProcessId=0;StartType='Disabled'}
    $script:actions.Clear();$script:imagePinned=$false;$script:badCapability=$false
    function Start-Service([string]$Name){
        $opened=$null
        try{$opened=[IO.File]::Open((Join-Path $fixture 'RoutePilot.Agent.exe'),[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::None)}catch [IO.IOException]{$script:imagePinned=$true}finally{if($opened){$opened.Dispose()}}
        if(!$script:imagePinned){throw 'SCM start did not hold the verified image.'}
        $script:fakeState.State='Running';$script:fakeState.ProcessId=123
    }
    function Get-Service([string]$Name){
        $item=[pscustomobject]@{};$item|Add-Member ScriptMethod WaitForStatus {param($status,$timeout)};return $item
    }
    function Get-Process([int]$Id){
        $item=[pscustomobject]@{Handle=[IntPtr]1;HasExited=$false;Path=(Join-Path $fixture 'RoutePilot.Agent.exe');StartTime=[datetime]'2026-10-30T01:00:00Z'}
        $item|Add-Member ScriptMethod Dispose {};return $item
    }
    function Invoke-NativeUpgradeServiceRpc($Lease,$StartedService,[string]$Command,[string]$Id){
        if($Command -cne 'native-capabilities' -or $StartedService.ProcessId -ne 123 -or $StartedService.StartedUtcTicks -le 0 -or $StartedService.Id -cne $Id){throw 'Start proof was not supplied to RPC.'}
        return [pscustomobject]@{Success=$true;Native=[pscustomobject]@{maintenanceGateProtocol=1;maintenanceValidationProtocol=$(if($script:badCapability){'1'}else{1})}}
    }
    $proof=Start-NativeUpgradeValidationService $lease $id {$true}
    Assert ($script:imagePinned -and $proof.ProcessId -eq 123 -and $proof.AgentSha -ceq $j.newAgentSha) 'Validation start provenance failed.'
    Assert ($script:fakeState.StartType -ceq 'Manual' -and $script:actions.Count -eq 1) 'Successful validation start changed startup unexpectedly.'
    Assert ([IO.File]::Exists((Join-Path $fixture 'native-upgrade.pending'))) 'Validation start removed the maintenance gate.'
    $script:fakeState=[pscustomobject]@{State='Stopped';ProcessId=0;StartType='Disabled'};$script:badCapability=$true;$script:actions.Clear()
    Reject {Start-NativeUpgradeValidationService $lease $id {$true}}
    Assert ($script:fakeState.StartType -ceq 'Disabled' -and $script:actions.Count -eq 2) 'Invalid capabilities did not restore the startup fence.'
}finally{
    if($lease){$lease.Handle.Dispose()}
    $path=[IO.Path]::GetFullPath($fixture);$parent=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../runtime')).TrimEnd('\')
    if([IO.Path]::GetDirectoryName($path) -ine $parent -or [IO.Path]::GetFileName($path) -cnotmatch '^cita-service-unit-[a-f0-9]{32}$'){throw 'Fixture cleanup escaped root.'}
    Assert-NativeUpgradePath $path
    if([IO.Directory]::Exists($path)){Remove-Item -LiteralPath $path -Recurse -Force}
}
'Native service description, readiness, bounded pipe input and mocked SCM failure ordering PASS; no live SCM/RPC test.'
