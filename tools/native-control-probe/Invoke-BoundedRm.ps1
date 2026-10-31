function Invoke-BoundedRm([string]$Code,[int]$TimeoutSeconds=30,[switch]$ViaRadmin){
    if($TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 180){throw 'Invalid SSH deadline'}
    $Code="$"+'ProgressPreference=''SilentlyContinue'';'+$Code
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Code))
    # Windows OpenSSH may dispatch through cmd.exe. Keep the complete encoded
    # command below its limit; never send a truncated script to the station.
    if($encoded.Length -gt 6800){
        $memory=[IO.MemoryStream]::new()
        try{
            $zip=[IO.Compression.GZipStream]::new($memory,[IO.Compression.CompressionMode]::Compress,$true)
            try{$bytes=[Text.Encoding]::UTF8.GetBytes($Code);$zip.Write($bytes,0,$bytes.Length)}finally{$zip.Dispose()}
            $compressed=[Convert]::ToBase64String($memory.ToArray())
        }finally{$memory.Dispose()}
        $envelope='$m=[IO.MemoryStream]::new([Convert]::FromBase64String('''+$compressed+'''));$g=[IO.Compression.GZipStream]::new($m,[IO.Compression.CompressionMode]::Decompress);$r=[IO.StreamReader]::new($g,[Text.Encoding]::UTF8);try{& ([ScriptBlock]::Create($r.ReadToEnd()))}finally{$r.Dispose();$g.Dispose();$m.Dispose()}'
        $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($envelope))
    }
    if($encoded.Length -gt 7000){throw 'Remote script exceeds bounded command transport; transfer a verified script file instead'}
    $info=[Diagnostics.ProcessStartInfo]::new();$info.FileName=(Get-Command ssh -ErrorAction Stop).Source
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $destination=if($ViaRadmin){'citastation@26.68.227.253'}else{'citastation@192.168.1.124'}
    foreach($arg in @('-i',"$env:USERPROFILE\.ssh\cita_test_station_ed25519",'-o',"UserKnownHostsFile=$env:USERPROFILE\.ssh\cita_test_station_known_hosts",'-o','HostKeyAlias=26.68.227.253','-o','StrictHostKeyChecking=yes','-o','ConnectTimeout=10','-o','ServerAliveInterval=5','-o','ServerAliveCountMax=3','-o','BatchMode=yes',$destination,"powershell -NoProfile -NonInteractive -EncodedCommand $encoded")){$info.ArgumentList.Add($arg)}
    $p=[Diagnostics.Process]::Start($info);if(!$p){throw 'SSH start failed'}
    try{
        $out=$p.StandardOutput.ReadToEndAsync();$err=$p.StandardError.ReadToEndAsync()
        if(!$p.WaitForExit($TimeoutSeconds*1000)){$p.Kill();if(!$p.WaitForExit(5000)){throw 'SSH client exit unconfirmed'};throw 'SSH observation timed out; remote outcome unknown; do not repeat before reconciliation'}
        if(![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($out,$err),5000)){throw 'SSH output incomplete; remote outcome unknown'}
        if($p.ExitCode -ne 0){throw 'SSH command failed; remote outcome may be unknown'}
        return $out.Result
    }finally{$p.Dispose()}
}
function Invoke-BoundedScp([string]$Source,[string]$Destination,[int]$TimeoutSeconds=45,[switch]$ViaRadmin){
    if($TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 180){throw 'Invalid SCP deadline'}
    $info=[Diagnostics.ProcessStartInfo]::new();$info.FileName=(Get-Command scp -ErrorAction Stop).Source
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    if(!$ViaRadmin){
        if($Source.StartsWith('citastation@26.68.227.253:',[StringComparison]::OrdinalIgnoreCase)){$Source='citastation@192.168.1.124:'+$Source.Substring('citastation@26.68.227.253:'.Length)}
        if($Destination.StartsWith('citastation@26.68.227.253:',[StringComparison]::OrdinalIgnoreCase)){$Destination='citastation@192.168.1.124:'+$Destination.Substring('citastation@26.68.227.253:'.Length)}
    }
    foreach($arg in @('-q','-i',"$env:USERPROFILE\.ssh\cita_test_station_ed25519",'-o',"UserKnownHostsFile=$env:USERPROFILE\.ssh\cita_test_station_known_hosts",'-o','HostKeyAlias=26.68.227.253','-o','StrictHostKeyChecking=yes','-o','ConnectTimeout=10','-o','ServerAliveInterval=5','-o','ServerAliveCountMax=3','-o','BatchMode=yes',$Source,$Destination)){$info.ArgumentList.Add($arg)}
    $p=[Diagnostics.Process]::Start($info);if(!$p){throw 'SCP start failed'}
    try{
        $out=$p.StandardOutput.ReadToEndAsync();$err=$p.StandardError.ReadToEndAsync()
        if(!$p.WaitForExit($TimeoutSeconds*1000)){$p.Kill();if(!$p.WaitForExit(5000)){throw 'SCP exit unconfirmed'};throw 'SCP timed out; remote file may be incomplete'}
        if(![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($out,$err),5000)){throw 'SCP output incomplete; remote file status unknown'}
        if($p.ExitCode -ne 0){throw 'SCP failed; remote file status may be unknown'}
    }finally{$p.Dispose()}
}
