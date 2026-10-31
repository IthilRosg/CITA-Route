$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$receipt=Get-Content -LiteralPath (Join-Path $root 'runtime/goal2-control-build-receipt.json') -Raw|ConvertFrom-Json
$paths=@(Get-ChildItem -LiteralPath (Join-Path $root 'agent') -Recurse -File -Filter '*.cs' | Where-Object {$_.FullName -notmatch '[\\/](bin|obj)[\\/]'} | ForEach-Object {$_.FullName.Substring($root.Length+1)})
$paths+=@('agent\RoutePilot.Agent.csproj','tools\native-control-probe\Program.cs','tools\native-control-probe\NativeControlProbe.csproj')
$listed=@($receipt.inputs|ForEach-Object {$_.path})
if($paths.Count -ne $listed.Count -or @(Compare-Object ($paths|Sort-Object) ($listed|Sort-Object)).Count){throw 'Source inventory differs from build receipt'}
foreach($item in $receipt.inputs){$path=Join-Path $root $item.path;if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $item.sha256){throw "Source differs: $($item.path)"}}
foreach($pair in @(@('runtime/goal2-preflight-agent-build/RoutePilot.Agent.dll',$receipt.agentSha256),@('runtime/control-probe-publish/NativeControlProbe.exe',$receipt.probeSha256),@('runtime/dotnet10/dotnet.exe',$receipt.dotnetSha256))){
 if((Get-FileHash -LiteralPath (Join-Path $root $pair[0]) -Algorithm SHA256).Hash -cne $pair[1]){throw 'Build output or tool differs from receipt'}
}
'Probe build receipt matches local files'
