$ErrorActionPreference='Stop'
$project=$PSScriptRoot
. (Join-Path $project 'FileStorage.ps1')
$base=Join-Path $project ('runtime\release-install-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $base -Force | Out-Null
try{
    $syntax=[Management.Automation.Language.Parser]::ParseFile((Join-Path $project 'Install-Release.ps1'),[ref]$null,[ref]$null)
    $assignment=$syntax.Find({param($node)$node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$files'},$true)
    $files=@($assignment.Right.FindAll({param($node)$node -is [Management.Automation.Language.StringConstantExpressionAst]},$true) | ForEach-Object Value)
    if($files.Count -lt 20){throw 'Could not load update allowlist.'}
    foreach($scenario in @('success','locked-file-rollback','active-session')){
        $fixture=Join-Path $base $scenario;$package=Join-Path $fixture 'runtime\updates\fixture\package'
        New-Item -ItemType Directory -Path $package,(Join-Path $fixture 'data'),(Join-Path $fixture 'logs') -Force | Out-Null
        foreach($name in $files){
            $source=Join-Path $package $name;$target=Join-Path $fixture $name
            New-Item -ItemType Directory -Path (Split-Path $source),(Split-Path $target) -Force | Out-Null
            [IO.File]::WriteAllText($source,'new public file: '+$name)
            [IO.File]::WriteAllText($target,'old public file: '+$name)
        }
        Copy-Item -LiteralPath (Join-Path $project 'runtime\app-build\RoutePilot.exe') -Destination (Join-Path $package 'RoutePilot.exe') -Force
        Copy-Item -LiteralPath (Join-Path $project 'Install-Release.ps1') -Destination $fixture -Force
        Copy-Item -LiteralPath (Join-Path $project 'FileStorage.ps1') -Destination $fixture -Force
        [IO.File]::WriteAllText((Join-Path $fixture 'AgentLease.ps1'),'function Assert-NoAgentRun {}; function Invoke-AgentCoordinator {}')
        [IO.File]::WriteAllText((Join-Path $fixture 'force-vpn.txt'),'keep-user-rule.example')
        Write-JsonFile (Join-Path $fixture 'data\connection.json') @{SourceProfile='Keep my WG'}
        Write-JsonFile (Join-Path $fixture 'runtime\session.json') @{running=($scenario -eq 'active-session')}
        $entries=@($files | ForEach-Object {$source=Join-Path $package $_;@{path=$_;bytes=(Get-Item -LiteralPath $source).Length;sha256=(Get-FileHash -LiteralPath $source).Hash}})
        Write-JsonFile (Join-Path $package 'manifest.json') @{version='0.3.0';architecture='win-x64';selfContained=$true;files=$entries}
        function Get-Process {param($Name,$Id,$ErrorAction) return $null}
        function Get-Service {param($Name,$ErrorAction) return $null}
        function Start-Process {param($FilePath,$WindowStyle) return $null}
        $lock=$null
        try{
            if($scenario -eq 'locked-file-rollback'){$lock=[IO.File]::Open((Join-Path $fixture 'SmartRouting.ps1'),'Open','ReadWrite','None')}
            $failed=$false
            try{& (Join-Path $fixture 'Install-Release.ps1') -Package $package -Version '0.3.0' -GuiPid 9999999 -GuiStartedUtc ([DateTime]::UtcNow.ToString('o'))}catch{$failed=$true}
        }finally{if($lock){$lock.Dispose()}}
        if($scenario -eq 'active-session'){
            if(-not $failed -or (Test-Path -LiteralPath (Join-Path $fixture 'logs\release-update-last.json'))){throw 'An active session was not rejected before mutation.'}
        }else{
            $result=Read-JsonFile (Join-Path $fixture 'logs\release-update-last.json')
            if($scenario -eq 'success' -and (-not $result.success -or -not(Test-Path -LiteralPath (Join-Path $fixture 'data\pending-agent-update.json')))){throw 'Disconnected release installation failed.'}
            if($scenario -eq 'locked-file-rollback' -and ($result.success -or -not $result.restored -or [IO.File]::ReadAllText((Join-Path $fixture 'RoutePilot.exe')) -ne 'old public file: RoutePilot.exe')){throw 'Failed update did not restore old public files.'}
        }
        if([IO.File]::ReadAllText((Join-Path $fixture 'force-vpn.txt')) -ne 'keep-user-rule.example' -or (Read-JsonFile (Join-Path $fixture 'data\connection.json')).SourceProfile -ne 'Keep my WG'){throw 'An update changed personal rules or connection settings.'}
    }
    Write-Output 'PASS: release installation, locked-file rollback, active-session refusal and preservation of personal settings. No real processes, services or network routes changed.'
}finally{
    $prefix=[IO.Path]::GetFullPath((Join-Path $project 'runtime\release-install-tests-'))
    if(-not [IO.Path]::GetFullPath($base).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe update-test cleanup.'}
    Remove-Item -LiteralPath $base -Recurse -Force
}
