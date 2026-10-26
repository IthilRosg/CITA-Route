[CmdletBinding()]
param([ValidateSet('Arm','Collect')][string]$Action='Collect',[string]$ClientRoot=(Split-Path $PSScriptRoot))
$ErrorActionPreference='Stop'
$ClientRoot=[IO.Path]::GetFullPath($ClientRoot).TrimEnd('\')
$shared=Join-Path $env:ProgramData 'RoutePilot'
$protected=Join-Path $env:ProgramFiles 'RoutePilot\Agent'
function ReadSmall([string]$Path){
    $stream=[IO.File]::Open($Path,'Open','Read',([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try{if($stream.Length -gt 131072){throw 'Oversized diagnostic state.'};$reader=New-Object IO.StreamReader($stream);try{return ($reader.ReadToEnd()|ConvertFrom-Json)}finally{$reader.Dispose()}}finally{$stream.Dispose()}
}
function Owns([string]$Path){[IO.Path]::GetFullPath($Path).TrimEnd('\') -eq $ClientRoot}
function Utc($Value){([datetime]$Value).ToUniversalTime()}
function SameBoot($First,$Second){$First -and $Second -and [Math]::Abs(((Utc $First)-(Utc $Second)).TotalSeconds) -lt 10}
$settings=ReadSmall (Join-Path $protected 'startup-settings.json')
if(-not $settings.Enabled -or -not(Owns $settings.SourceRoot)){throw 'This client does not own enabled protected startup.'}
$boot=(Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime()
$directory=Join-Path $ClientRoot 'logs';New-Item -ItemType Directory -Path $directory -Force|Out-Null
$baseline=Join-Path $directory 'startup-boot-baseline.json'
if($Action -eq 'Arm'){
    $priorState=ReadSmall (Join-Path $shared 'startup-state.json')
    @{schema=1;armedUtc=[datetime]::UtcNow.ToString('o');osBootUtc=$boot.ToString('o');activationId=[string]$priorState.ActivationId;version=$settings.Version;sourceRoot=$ClientRoot}|ConvertTo-Json|Set-Content -LiteralPath $baseline -Encoding UTF8
    Write-Output 'Baseline recorded. Reboot the separate test Windows manually and leave it signed out until the connection is ready. No network or startup settings changed.'
    exit 0
}
$before=ReadSmall $baseline
if(-not(Owns $before.sourceRoot)){throw 'The baseline belongs to another client.'}
$state=ReadSmall (Join-Path $shared 'startup-state.json')
$proof=ReadSmall (Join-Path $shared 'startup-boot-proof.json')
$session=ReadSmall (Join-Path $protected 'startup-client\runtime\session.json')
$health=ReadSmall (Join-Path $protected 'startup-client\runtime\session-health.json')
$age=([datetime]::UtcNow-(Utc $health.updatedUtc)).TotalSeconds
$current=($state.Phase -eq 'Connected' -and (Owns $state.SourceRoot) -and $session.running -and $session.bridgeReady -and $session.id -and $proof.SessionId -eq $session.id -and $health.sessionId -eq $session.id -and $proof.BridgeGeneration -eq $session.bridgeGeneration -and $health.bridgeGeneration -eq $session.bridgeGeneration -and $health.healthy -and $age -ge 0 -and $age -le 90 -and (SameBoot $proof.BootUtc $state.BootUtc) -and (SameBoot $proof.BootUtc $boot) -and $proof.ConnectedUtc -and (Utc $proof.ConnectedUtc) -gt (Utc $before.armedUtc) -and (Utc $proof.StartedUtc) -gt (Utc $before.armedUtc) -and $proof.Version -eq $settings.Version)
$rebooted=-not(SameBoot $before.osBootUtc $boot)
$activationChanged=[string]$before.activationId -match '^[A-F0-9]{64}$' -and [string]$proof.ActivationId -match '^[A-F0-9]{64}$' -and $proof.ActivationId -eq $state.ActivationId -and $proof.ActivationId -ne $before.activationId
$report=@{schema=1;checkedUtc=[datetime]::UtcNow.ToString('o');version=$proof.Version;kernelBootChanged=$rebooted;currentHealthyGeneration=[bool]$current;serviceSessionId=$proof.ServiceSessionId;interactiveUsersAtStart=$proof.InteractiveUsersAtStart;interactiveUsersAtConnection=$proof.InteractiveUsersAtConnection;connectedUtc=$proof.ConnectedUtc;
    rebootBeforeSignInObserved=[bool]($current -and $rebooted -and $proof.ServiceSessionId -eq 0 -and $null -ne $proof.InteractiveUsersAtStart -and $proof.InteractiveUsersAtStart -eq 0 -and $null -ne $proof.InteractiveUsersAtConnection -and $proof.InteractiveUsersAtConnection -eq 0);
    windowsActivationChanged=[bool]$activationChanged;
    activationBeforeSignInObserved=[bool]($current -and ($rebooted -or $activationChanged) -and $proof.ServiceSessionId -eq 0 -and $null -ne $proof.InteractiveUsersAtStart -and $proof.InteractiveUsersAtStart -eq 0 -and $null -ne $proof.InteractiveUsersAtConnection -and $proof.InteractiveUsersAtConnection -eq 0);
    limitation='WTS point-in-time observations. A new activation is separate from kernel restart. Uninterrupted absence of interactive logons requires separate evidence. Synthetic service CI is not real WireSock transport proof.'}
$report|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $directory 'startup-boot-report.json') -Encoding UTF8
$report|ConvertTo-Json
