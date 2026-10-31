$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source=Join-Path $workspace 'Install-NativeAgent.ps1'
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Native agent setup syntax invalid.'}
$function=$ast.Find({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-NativeAgentReplace'},$true)
if(!$function){throw 'Atomic replacement function missing.'}
. ([scriptblock]::Create($function.Extent.Text))
$parent=Join-Path $workspace 'runtime/native-agent-atomic-tests';[void][IO.Directory]::CreateDirectory($parent)
$test=Join-Path $parent ([guid]::NewGuid().ToString('N'));[void][IO.Directory]::CreateDirectory($test)
$target=Join-Path $test 'agent.exe';$staged=Join-Path $test 'stage.exe';$backup=Join-Path $test 'backup.exe'
try {
 [IO.File]::WriteAllText($target,'old');[IO.File]::WriteAllText($staged,'new')
 Invoke-NativeAgentReplace $staged $target $backup
 if([IO.File]::ReadAllText($target) -ne 'new' -or [IO.File]::ReadAllText($backup) -ne 'old' -or (Test-Path -LiteralPath $staged)){throw 'Atomic promotion mismatch.'}
 # A missing target must never consume either source or retained backup.
 [IO.File]::Delete($target)
 $refused=$false;try{Invoke-NativeAgentReplace $backup $target $staged}catch{$refused=$true}
 if(!$refused -or [IO.File]::ReadAllText($backup) -ne 'old' -or (Test-Path -LiteralPath $staged)){throw 'Missing target consumed recovery source.'}
 [IO.File]::WriteAllText($target,'new')
 Invoke-NativeAgentReplace $backup $target $staged
 if([IO.File]::ReadAllText($target) -ne 'old' -or [IO.File]::ReadAllText($staged) -ne 'new' -or (Test-Path -LiteralPath $backup)){throw 'Atomic rollback mismatch.'}
 # Failed preconditions must preserve every existing input.
 [IO.File]::WriteAllText($backup,'retained')
 $refused=$false;try{Invoke-NativeAgentReplace $staged $target $backup}catch{$refused=$true}
 if(!$refused -or [IO.File]::ReadAllText($target) -ne 'old' -or [IO.File]::ReadAllText($staged) -ne 'new' -or [IO.File]::ReadAllText($backup) -ne 'retained'){throw 'Existing backup changed on refusal.'}
 [IO.File]::Delete($target)
 $refused=$false;try{Invoke-NativeAgentReplace $staged $target $backup}catch{$refused=$true}
 if(!$refused -or [IO.File]::ReadAllText($staged) -ne 'new' -or [IO.File]::ReadAllText($backup) -ne 'retained'){throw 'Missing target did not preserve staged and backup files.'}
 [IO.File]::WriteAllText($target,'old');[IO.File]::Delete($backup)
 $held=[IO.File]::Open($target,'Open','Read','Read')
 try{$refused=$false;try{Invoke-NativeAgentReplace $staged $target $backup}catch{$refused=$true};if(!$refused){throw 'Locked target was unexpectedly replaced.'}}finally{$held.Dispose()}
 if([IO.File]::ReadAllText($target) -ne 'old' -or [IO.File]::ReadAllText($staged) -ne 'new' -or (Test-Path -LiteralPath $backup)){throw 'Failed locked replacement changed files.'}
 'PASS atomic agent promotion, rollback, existing-backup and locked-target refusal, absent-target preservation.'
} finally {
 if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($test)) -ine [IO.Path]::GetFullPath($parent) -or (Split-Path $test -Leaf) -cnotmatch '^[a-f0-9]{32}$'){throw 'Unsafe fixture cleanup.'}
 if(Test-Path -LiteralPath $test){Remove-Item -LiteralPath $test -Recurse -Force}
}
