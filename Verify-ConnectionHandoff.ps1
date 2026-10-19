$ErrorActionPreference='Stop'
$project=$PSScriptRoot
. (Join-Path $project 'FileStorage.ps1')
. (Join-Path $project 'ConnectionEnvironment.ps1')
. (Join-Path $project 'WireSockCli.ps1')
function Assert($Value,[string]$Message){if(-not $Value){throw $Message}}
$base=Join-Path $project ('runtime\handoff-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $base -Force|Out-Null
try {
    foreach($case in @('none','foreign','vpnus','both','unknown','conflict','cancel','active-route','wrong-service','failed-stop','restore-foreign-change','interrupted','complete','stale-complete','missing-profile')){
        $root=Join-Path $base $case;$runtime=Join-Path $root 'runtime'
        New-Item -ItemType Directory -Path $runtime -Force|Out-Null
        $OperationId=[guid]::NewGuid().ToString('N');$script:cli='fixture.exe'
        $script:wire=if($case -in @('foreign','both','cancel','restore-foreign-change','interrupted','missing-profile')){"Connected to server using profile Friend WG`r`nExternal address: 203.0.113.1`r`n"}else{'Not connected'}
        if($case -eq 'unknown'){$script:wire='unrecognized'}
        if($case -eq 'conflict'){$script:wire="Connected to server using profile Friend WG`r`nConnected to server using profile Other`r`n"}
        $script:up=$case -in @('vpnus','both','wrong-service','failed-stop','complete','stale-complete','missing-profile')
        $script:serviceState='Running';$script:stops=0;$script:starts=0;$script:disconnects=0;$script:restores=0;$script:leases=0
        function Assert-NoAgentRun {}
        function Invoke-AgentCoordinator($Command){if($Command -eq 'begin-operation'){$script:leases++}else{$script:leases--}}
        function Get-Process {param($Name,$ErrorAction) if($case -eq 'active-route'){return [pscustomobject]@{Id=123}}}
        function Get-Service {param($Name,$ErrorAction) [pscustomobject]@{Status=$script:serviceState}}
        function Get-NetAdapter {param($Name,$ErrorAction) [pscustomobject]@{Status=$(if($script:up){'Up'}else{'Down'})}}
        function Get-CimInstance {param($ClassName,$Filter,$ErrorAction) [pscustomobject]@{State=$script:serviceState;PathName=$(if($case -eq 'wrong-service'){'C:\Untrusted\fake.exe'}else{Join-Path $env:ProgramFiles 'VPNUS\service\vpnus-service.exe'})}}
        function Stop-Service {param($Name,$ErrorAction) Assert ($Name -eq 'VpnusService') 'Stopped another service';$script:stops++;if($case -ne 'failed-stop'){$script:up=$false;$script:serviceState='Stopped'}}
        function Start-Service {param($Name,$ErrorAction) Assert ($Name -eq 'VpnusService') 'Started another service';$script:starts++;$script:serviceState='Running'}
        function Start-Sleep {param($Milliseconds) if($case -eq 'failed-stop'){throw 'Fixture stop deadline'}}
        function Read-WireSockStatus {$script:wire}
        function Read-HelperOutput($Image,$Arguments){
            if($Arguments -eq 'disconnect'){$script:disconnects++;$script:wire='Not connected';if($case -eq 'cancel'){[IO.File]::WriteAllText((Join-Path $runtime ('operation-'+$OperationId+'.cancel')),'cancel')}}
            elseif($Arguments -eq 'connect "Friend WG" -exit'){$script:restores++;$script:wire='Connected to server using profile Friend WG'}
            else{throw ('Unexpected CLI mutation: '+$Arguments)}
        }
        function Get-WireSockProfileNames {if($case -ne 'missing-profile'){@('Friend WG','Other')}}
        $failed=$false
        try{Release-ConnectionConflicts $root}catch{$failed=$true}
        Assert ($script:leases -eq 0) 'Coordinator lease leaked'
        if($case -in @('unknown','conflict','active-route','wrong-service')){
            Assert ($failed -and $script:stops -eq 0 -and $script:disconnects -eq 0) ('Unverified conflict was mutated: '+$case)
        }elseif($case -eq 'none'){
            Assert (-not $failed -and -not(Test-Path -LiteralPath (Join-Path $runtime 'connection-handoff.json'))) 'A disconnected system was changed'
        }elseif($case -eq 'complete'){
            Complete-ConnectionHandoff $root
            Assert (-not(Test-Path -LiteralPath (Join-Path $runtime 'connection-handoff.json')) -and $script:starts -eq 0 -and $script:stops -eq 1) 'Successful Connect restored the competing VPN or left its journal'
        }else{
            Assert ($failed -eq ($case -in @('cancel','failed-stop'))) ('Unexpected prepare outcome: '+$case)
            if($case -eq 'restore-foreign-change'){$script:wire='Connected to server using profile Other'}
            if($case -eq 'interrupted'){
                $OperationId=[guid]::NewGuid().ToString('N');Release-ConnectionConflicts $root
                Assert ($script:restores -eq 1 -and $script:disconnects -eq 2) 'Interrupted handoff was not restored before a new attempt'
            }
            if($case -eq 'stale-complete'){
                $previousId=$OperationId;$OperationId=[guid]::NewGuid().ToString('N');$rejected=$false
                try{Complete-ConnectionHandoff $root}catch{$rejected=$true}finally{$OperationId=$previousId}
                Assert ($rejected -and (Test-Path -LiteralPath (Join-Path $runtime 'connection-handoff.json'))) 'Another operation deleted the handoff journal'
            }
            $restorationFailed=$false
            try{Restore-ConnectionHandoff $root}catch{$restorationFailed=$true}
            Assert ($script:leases -eq 0) 'Restore leaked coordinator lease'
            if($case -eq 'missing-profile'){
                Assert ($restorationFailed -and $script:starts -eq 1 -and (Test-Path -LiteralPath (Join-Path $runtime 'connection-handoff.json'))) 'Missing WireSock profile prevented independent VPNUS service restoration'
            }elseif($case -eq 'restore-foreign-change'){
                Assert ($restorationFailed -and $script:restores -eq 0) 'Restore overwrote a newly selected foreign profile'
            }else{
                Assert (-not $restorationFailed -and -not(Test-Path -LiteralPath (Join-Path $runtime 'connection-handoff.json'))) ('Restore did not finish: '+$case)
                if($case -in @('vpnus','both','failed-stop')){Assert ($script:starts -eq 1) 'VPNUS service was not restored after failure'}
                if($case -in @('foreign','both','cancel')){Assert ($script:restores -eq 1) 'Previous profile was not restored after failure/cancellation'}
            }
        }
    }
    Write-Output 'PASS: supported VPN conflict release, CRLF identity, bounded stop, cancellation/rollback, interrupted journals, unknown/conflicting profiles, newer profile protection and service identity. All network/service operations mocked.'
}finally{
    $prefix=[IO.Path]::GetFullPath((Join-Path $project 'runtime\handoff-tests-'))
    if(-not [IO.Path]::GetFullPath($base).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture cleanup'}
    Remove-Item -LiteralPath $base -Recurse -Force
}
