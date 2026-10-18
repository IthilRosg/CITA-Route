[CmdletBinding()]
param([string]$DownloadProxy='')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
New-Item -ItemType Directory -Path (Join-Path $root 'downloads'),(Join-Path $root 'runtime\rules'),(Join-Path $root 'logs'),(Join-Path $root 'data'),(Join-Path $root 'distribution-licenses') -Force | Out-Null
function Download([string]$Url,[string]$Path,[string]$Hash=''){
    if((Test-Path -LiteralPath $Path) -and $Hash -and (Get-FileHash -LiteralPath $Path).Hash -eq $Hash){return}
    $temporary=$Path+'.download'
    try{
        $arguments=@('--fail','--silent','--show-error','--location','--proto','=https','--proto-redir','=https','--max-time','180','--retry','2','--output',$temporary)
        if($DownloadProxy){$arguments+=@('--proxy',$DownloadProxy)}
        & curl.exe @arguments $Url
        if($LASTEXITCODE -ne 0){throw ('Dependency download failed: '+[IO.Path]::GetFileName($Path))}
        if($Hash -and (Get-FileHash -LiteralPath $temporary).Hash -ne $Hash){throw 'Dependency SHA-256 differs from the pinned release.'}
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    }finally{Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue}
}
$archive=Join-Path $root 'downloads\sing-box-1.14.3-windows-amd64.zip'
Download 'https://github.com/SagerNet/sing-box/releases/download/v1.14.3/sing-box-1.14.3-windows-amd64.zip' $archive 'D8B86EF58936DBA9230EC6A57FFA4243933F3C14EF4809828B6C553440ED1518'
$extract=Join-Path $root ('downloads\engine-'+[guid]::NewGuid().ToString('N'))
try{
    Expand-Archive -LiteralPath $archive -DestinationPath $extract
    $binary=@(Get-ChildItem -LiteralPath $extract -Recurse -Filter sing-box.exe -File)
    if($binary.Count -ne 1 -or (Get-FileHash -LiteralPath $binary[0].FullName).Hash -ne '5C19BB3F8C12D4E85B3D47FE63123A7624B314B58DD7A8E32F51004F1C4D2FAF'){throw 'Engine binary differs from the pinned release.'}
    foreach($name in @('smart-router.exe','vpn-bridge.exe')){
        $target=Join-Path $root ('runtime\'+$name)
        if(-not(Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $binary[0].FullName).Hash){Copy-Item -LiteralPath $binary[0].FullName -Destination $target -Force}
    }
}finally{
    $prefix=[IO.Path]::GetFullPath((Join-Path $root 'downloads\engine-'))
    if(-not [IO.Path]::GetFullPath($extract).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe extraction cleanup.'}
    Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue
}
foreach($name in @('russia_inside','discord')){
    $target=Join-Path $root ('runtime\rules\'+$name+'.srs')
    if(-not(Test-Path -LiteralPath $target)){Download ('https://github.com/itdoginfo/allow-domains/releases/latest/download/'+$name+'.srs') $target}
}
$licenses=@(
    @('https://raw.githubusercontent.com/SagerNet/sing-box/v1.14.3/LICENSE','sing-box-LICENSE.txt','650D5E3B99A446FB38E820FA87A49562E0C79EAB868FFF58618AC487A58E554C'),
    @('https://raw.githubusercontent.com/gcc-mirror/gcc/master/COPYING3','GPL-3.0.txt','8CEB4B9EE5ADEDDE47B31E975C1D90C73AD27B6B165A1DCD80C7C545EB65B903'),
    @('https://codeload.github.com/SagerNet/sing-box/zip/refs/tags/v1.14.3','sing-box-1.14.3-source.zip','5BF11EEB2F5B93377166AF1ECDCF24DD2C4BE3C92BCB43C09D95EDD01EB85949'),
    @('https://raw.githubusercontent.com/dotnet/runtime/v8.0.30/LICENSE.TXT','dotnet-LICENSE.txt','CFC21F5E8BD655AE997EEC916138B707B1D290B83272C02A95C9F821B8C87310'),
    @('https://raw.githubusercontent.com/dotnet/runtime/v8.0.30/THIRD-PARTY-NOTICES.TXT','dotnet-THIRD-PARTY-NOTICES.txt','97C1A7B3DA6A4C6AD516448719F45114B41A4D4C5AA300A944476E2E4F5DA438'))
foreach($entry in $licenses){Download $entry[0] (Join-Path $root ('distribution-licenses\'+$entry[1])) $entry[2]}
foreach($name in @('force-direct','force-vpn')){
    $target=Join-Path $root ($name+'.txt');if(-not(Test-Path -LiteralPath $target)){Copy-Item -LiteralPath (Join-Path $root ($name+'.example.txt')) -Destination $target}
}
. (Join-Path $root 'FileStorage.ps1')
. (Join-Path $root 'RuleFeeds.ps1')
Write-TelegramRules ([IO.File]::ReadAllText((Join-Path $root 'rules\telegram-cidr.txt'))) (Join-Path $root 'runtime\rules\telegram.json')
& (Join-Path $root 'Create-AppIcon.ps1')
Write-Output 'Dependencies prepared and pinned engine hashes verified. No services, VPN profiles or system routes changed.'
