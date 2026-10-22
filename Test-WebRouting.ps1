#requires -Version 7.0
[CmdletBinding()]
param([switch]$PrepareOnly)
$ErrorActionPreference='Stop'
$webProject=$PSScriptRoot
$webArchive=Join-Path $webProject 'distribution-licenses\sing-box-1.14.3-source.zip'
if((Get-FileHash -LiteralPath $webArchive).Hash -ne '5BF11EEB2F5B93377166AF1ECDCF24DD2C4BE3C92BCB43C09D95EDD01EB85949'){throw 'Pinned upstream fixture archive differs'}
$webZip=[IO.Compression.ZipFile]::OpenRead($webArchive)
try{
 $webEntry=$webZip.GetEntry('sing-box-1.14.3/common/sniff/quic_test.go');if(-not $webEntry){throw 'Pinned QUIC test source missing'}
 $webReader=[IO.StreamReader]::new($webEntry.Open());try{$webSource=$webReader.ReadToEnd()}finally{$webReader.Dispose()}
}finally{$webZip.Dispose()}
$webCase=($webSource -split 'func TestSniffUQUICChrome115')[1] -split 'func TestSniffQUICFirefox'
$webMatch=[regex]::Match($webCase[0],'DecodeString\("([0-9a-f]+)"\)');if(-not $webMatch.Success){throw 'Pinned single-datagram QUIC fixture missing'}
$webPacket=[Convert]::FromHexString($webMatch.Groups[1].Value)
if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($webPacket)) -ne '4A132C02F053DEF557DB41B8D89E1AAB2A8CFDDF8F646D617C8EE6BBD695048D'){throw 'Pinned public QUIC fixture checksum differs'}
[void][IO.Directory]::CreateDirectory((Join-Path $webProject 'runtime'))
[IO.File]::WriteAllBytes((Join-Path $webProject 'runtime\web-quic-initial.bin'),$webPacket)
if($PrepareOnly){Write-Output 'Verified public upstream QUIC fixture prepared; no network test started.';return}
$webAgent=Join-Path $webProject 'runtime\web-routing-agent-build\RoutePilot.Agent.dll'
if(-not (Test-Path -LiteralPath $webAgent)){throw 'Build the web-routing agent candidate before running this fixture'}
$webInfo=[Diagnostics.ProcessStartInfo]::new((Join-Path $webProject 'runtime\dotnet10\dotnet.exe'))
$webInfo.UseShellExecute=$false;$webInfo.CreateNoWindow=$true;$webInfo.RedirectStandardOutput=$true;$webInfo.RedirectStandardError=$true
foreach($webArgument in @($webAgent,'--web-routing-lab',$webProject)){[void]$webInfo.ArgumentList.Add($webArgument)}
$webProcess=[Diagnostics.Process]::Start($webInfo)
try{
 $webOutput=$webProcess.StandardOutput.ReadToEndAsync();$webError=$webProcess.StandardError.ReadToEndAsync()
 if(-not $webProcess.WaitForExit(45000)){$webProcess.Kill();throw 'Owned web fixture exceeded deadline'}
 $webText=$webOutput.GetAwaiter().GetResult();$webFailure=$webError.GetAwaiter().GetResult()
 if($webProcess.ExitCode -ne 0){throw ('Owned web fixture failed: '+$webFailure)}
 Write-Output $webText
}finally{$webProcess.Dispose()}
