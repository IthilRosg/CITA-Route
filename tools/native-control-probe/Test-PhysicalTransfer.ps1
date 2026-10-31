$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $root 'runtime/invoke-rm-lab.ps1')
$id=[guid]::NewGuid().ToString('N');$folder="cita-transfer-$id"
$remote="C:\Program Files\RoutePilot\Agent\$folder";$item="$remote\probe.txt"
$local=Join-Path $root "runtime/$folder.txt"
[IO.File]::WriteAllText($local,"CITA transfer $id",[Text.UTF8Encoding]::new($false));$hash=(Get-FileHash $local).Hash
$create=@'
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'F8798200-12FB-11F1-8F47-A104BEE59100'){throw 'Wrong station'}
$parent='C:\Program Files\RoutePilot\Agent';$d=Get-Item -LiteralPath $parent -Force
while($d){if($d.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse parent'};$d=$d.Parent}
$current=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$trusted=@('S-1-5-18','S-1-5-32-544','S-1-3-0','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464',$current)
function Check-Acl($path){
 $d=Get-Item -LiteralPath $path -Force;if(!$d.PSIsContainer -or ($d.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unsafe folder'}
 $acl=Get-Acl -LiteralPath $path;$owner=$acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
 if($owner -notin $trusted -or $owner -eq 'S-1-3-0'){throw 'Untrusted folder owner'}
 foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
  $sid=$rule.IdentityReference.Value;$mask=([int64]$rule.FileSystemRights -band 0xffffffff)
  if($rule.AccessControlType -eq 'Allow' -and $sid -notin $trusted -and ($mask -band 0x500d0156)){throw 'Untrusted identity can write test folder'}
 }
}
Check-Acl $parent
Add-Type 'using System; using System.Runtime.InteropServices; public static class CitaProbeDirectory { [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool CreateDirectoryW(string path, IntPtr security); }'
$path='__DIR__';if(![CitaProbeDirectory]::CreateDirectoryW($path,[IntPtr]::Zero)){throw ('Exclusive directory creation failed: '+[Runtime.InteropServices.Marshal]::GetLastWin32Error())}
Check-Acl $path
'ready'
'@
$value=((Invoke-Rm $create.Replace('__DIR__',$remote)) -join '').Trim();if($value -ne 'ready'){throw "Directory unconfirmed: $id"}
$destination="citastation@26.68.227.253:C:/Program Files/RoutePilot/Agent/$folder/probe.txt"
& scp -q -i "$env:USERPROFILE\.ssh\cita_test_station_ed25519" -o "UserKnownHostsFile=$env:USERPROFILE\.ssh\cita_test_station_known_hosts" -o StrictHostKeyChecking=yes -o ConnectTimeout=10 $local $destination
if($LASTEXITCODE){throw "Transfer failed; inspect $remote"}
$check=@'
$ErrorActionPreference='Stop'
$p='__ITEM__';$d='__DIR__';$folder=Get-Item -LiteralPath $d -Force;if(!$folder.PSIsContainer -or ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unsafe folder'};$f=Get-Item -LiteralPath $p -Force
if($f.PSIsContainer -or ($f.Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Get-FileHash -LiteralPath $p).Hash -ne '__HASH__'){throw 'File mismatch'}
if(@(Get-ChildItem -LiteralPath $d -Force).Count -ne 1){throw 'Unexpected directory content'}
Remove-Item -LiteralPath $p -Force;Remove-Item -LiteralPath $d -ErrorAction Stop
if(Test-Path -LiteralPath $d){throw 'Directory remains'}
'confirmed'
'@
$result=((Invoke-Rm $check.Replace('__DIR__',$remote).Replace('__ITEM__',$item).Replace('__HASH__',$hash)) -join '').Trim()
if($result -ne 'confirmed'){throw "Cleanup unconfirmed: $remote"}
Remove-Item -LiteralPath $local -Force
"Physical transfer and exact cleanup passed"
