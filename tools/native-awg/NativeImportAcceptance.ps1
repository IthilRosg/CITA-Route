function Test-NativeImport {
    $settings=Join-Path $native 'service.json';$before=(Get-FileHash -LiteralPath $settings).Hash;$baseline=(Rpc 'status').Native
    function Start-Import([string]$text,[string]$name) {
        $current=(Rpc 'status').Native
        $body=@{Base64=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text));Name=$name;RequestId=[guid]::NewGuid().ToString('N')}
        $reply=Rpc 'native-import' $current.operation $body
        if(-not $reply.Success){throw 'Native import request refused'}
        return $reply.Native
    }
    $bad=Start-Import '[Interface]' 'Rejected fixture';$null=Wait-Native 'RecoveryFailed'
    if((Get-FileHash $settings).Hash -ne $before -or (Rpc 'status').Native.profileId -ne $baseline.profileId){throw 'Invalid import changed selection'}
    $null=Rpc 'native-stop' $bad.operation;$null=Wait-Native 'Disconnected';$report.invalidImportRetainedProfile=$true
    $text=[IO.File]::ReadAllText((Join-Path $root 'private\peer.conf'))
    $scripted=Start-Import ($text+"`nPostUp = rejected-script`n") 'Unsupported directive';$null=Wait-Native 'RecoveryFailed'
    if((Get-FileHash $settings).Hash -ne $before){throw 'Unsupported directive changed selection'}
    $null=Rpc 'native-stop' $scripted.operation;$null=Wait-Native 'Disconnected';$report.scriptDirectiveRejected=$true
    $blocked=[regex]::Replace($text,'(?mi)^\s*Endpoint\s*=\s*[^\r\n]+','Endpoint = 192.0.2.1:9')
    $old=(Rpc 'status').Native;$pending=Start-Import $blocked 'Cancelled fixture'
    if((Rpc 'native-start').Success){throw 'Native Start raced import'}
    if((Rpc 'native-stop' $old.operation).Success){throw 'Stale Stop cancelled import'}
    $null=Rpc 'native-stop' $pending.operation;$null=Wait-Native 'Disconnected'
    if((Get-FileHash $settings).Hash -ne $before -or (Rpc 'status').Native.profileId -ne $baseline.profileId){throw 'Cancelled import changed selection'}
    $report.importCancellationRetainedProfile=$true;$report.importExcludesStart=$true;$report.importRejectsStaleStop=$true
    $accepted=Start-Import ($text+"`n# Native import acceptance`n") 'Imported VM profile';$selected=Wait-Native 'Disconnected';$text=$null;$blocked=$null
    if($selected.operation -ne $accepted.operation -or $selected.profileId -eq $baseline.profileId -or $selected.profileName -ne 'Imported VM profile' -or $selected.desired){throw 'Imported profile was not selected safely'}
    $published=Get-Content -LiteralPath $settings -Raw|ConvertFrom-Json
    if($published.CatalogName -notmatch '^catalog-[a-f0-9]{32}\.json$'){throw 'Import did not publish an immutable catalog'}
    $catalog=Get-Content -LiteralPath (Join-Path $native $published.CatalogName) -Raw|ConvertFrom-Json
    $private=Join-Path $native $catalog.Profile.Name
    foreach($rule in (Get-Acl -LiteralPath $private).Access){if($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')){throw 'Imported private profile ACL widened'}}
    $report.importPrivateAclVerified=$true;$report.importSelectedDisconnected=$true
    Restart-Service RoutePilotRescue -Force;$persisted=Wait-Native 'Disconnected'
    if($persisted.profileId -ne $selected.profileId -or $persisted.profileName -ne $selected.profileName -or $persisted.desired){throw 'Imported selection did not survive restart'}
    $report.importSelectionSurvivesRestart=$true
    $null=Rpc 'native-start';$connected=Wait-Native 'Connected';$report.importedProfileTraffic=Probe-Tun
    $null=Rpc 'native-stop' $connected.operation;$null=Wait-Native 'Disconnected'
    $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $root 'NativeUiLab\Cita.NativeUiLab.exe'),'--service-import C:\CitaLab\NativeAwg');$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;$process=[Diagnostics.Process]::Start($info)
    try{$output=$process.StandardOutput.ReadToEndAsync();$errorOutput=$process.StandardError.ReadToEndAsync();if(-not $process.WaitForExit(65000)){$process.Kill();$process.WaitForExit();throw 'Desktop import deadline'};if($process.ExitCode -ne 0 -or -not ($output.GetAwaiter().GetResult()|ConvertFrom-Json).success){throw 'Desktop import failed'};$report.desktopImportVerified=$true}finally{$process.Dispose()}
}
