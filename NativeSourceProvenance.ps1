function Get-NativeWorkspaceInventory([string]$Root)
{
    $items=New-Object 'Collections.Generic.List[object]'
    foreach($part in @('tools/native-awg/core-patches','tools/native-awg','tools/native-guard-lab')){
        $directory=Join-Path $Root $part
        foreach($file in @(Get-ChildItem -LiteralPath $directory -File|Sort-Object Name)){
            if($part -ne 'tools/native-awg/core-patches' -and $file.Extension -ne '.go' -and $file.Name -notin @('go.mod','go.sum')){continue}
            if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Native source reparse point refused'}
            $items.Add([pscustomobject]@{path=($part+'/'+$file.Name);sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash})
        }
    }
    foreach($relative in @('tools/native-awg/Build-CoreLab.ps1','tools/native-awg/Build.ps1','tools/native-guard-lab/Build.ps1','Build-NativeSource.ps1','Verify-NativeSource.ps1','Build-NativeComponents.ps1','NativeSourceProvenance.ps1')){
        $file=Get-Item -LiteralPath (Join-Path $Root $relative)
        if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Native source reparse point refused'}
        $items.Add([pscustomobject]@{path=$relative;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash})
    }
    $go=Get-Command go -ErrorAction Stop;$items.Add([pscustomobject]@{path='@toolchain/go.exe';sha256=(Get-FileHash -LiteralPath $go.Source -Algorithm SHA256).Hash})
    return $items.ToArray()
}
function Assert-NativeInventory($Expected,$Actual)
{
    $wanted=@{};foreach($item in @($Expected)){if(!$item.path -or $item.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $wanted.ContainsKey([string]$item.path)){throw 'Native source inventory invalid'};$wanted[[string]$item.path]=[string]$item.sha256}
    $seen=@{};foreach($item in @($Actual)){if(!$item.path -or $seen.ContainsKey([string]$item.path)){throw 'Native source inventory invalid'};$seen[[string]$item.path]=$true;if(!$wanted.ContainsKey([string]$item.path) -or $wanted[[string]$item.path] -ine [string]$item.sha256){throw 'Native source workspace differs from verified inventory'}}
    if($seen.Count -ne $wanted.Count){throw 'Native source workspace differs from verified inventory'}
}
