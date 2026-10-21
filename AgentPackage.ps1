function Receive-SetupReservation([string]$Path,[int]$Owner,[long]$Handle) {
    if(-not ('RoutePilotSetupNative' -as [type])){
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
public static class RoutePilotSetupNative {
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int id);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool DuplicateHandle(IntPtr source,IntPtr handle,IntPtr target,out SafeFileHandle copy,uint access,bool inherit,uint options);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder path,uint size,uint flags);
    public static FileStream Receive(string expected,int owner,long handle) {
        if(owner<=0||handle<=0)throw new IOException("Invalid setup reservation.");
        IntPtr process=OpenProcess(0x0040,false,owner);
        if(process==IntPtr.Zero)throw new IOException("Setup owner is no longer available.");
        SafeFileHandle copy;
        try{if(!DuplicateHandle(process,new IntPtr(handle),GetCurrentProcess(),out copy,0,false,2))throw new IOException("Setup reservation is no longer available.");}
        finally{CloseHandle(process);}
        try {
            var path=new StringBuilder(32768);uint length=GetFinalPathNameByHandle(copy,path,(uint)path.Capacity,0);
            string actual=path.ToString();if(actual.StartsWith(@"\\?\"))actual=actual.Substring(4);
            if(length==0||length>=path.Capacity||!Path.GetFullPath(actual).Equals(Path.GetFullPath(expected),StringComparison.OrdinalIgnoreCase))throw new IOException("Wrong setup reservation.");
            bool reserved=false;
            try{using(var competing=File.Open(actual,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite|FileShare.Delete)){}}
            catch(IOException ex){if(ex.HResult!=unchecked((int)0x80070020))throw;reserved=true;}
            if(!reserved)throw new IOException("Setup reservation is not exclusive.");
            return new FileStream(copy,FileAccess.ReadWrite);
        }catch{copy.Dispose();throw;}
    }
}
'@
    }
    return [RoutePilotSetupNative]::Receive([IO.Path]::GetFullPath($Path),$Owner,$Handle)
}

function Publish-AgentPackage {
    param([object[]]$Files,[string]$InstallRoot,[scriptblock]$Stop,[scriptblock]$Start,[scriptblock]$Prepare={},[scriptblock]$Restore={})
    $InstallRoot=[IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
    $parent=Split-Path $InstallRoot
    $name=Split-Path $InstallRoot -Leaf
    $id=[guid]::NewGuid().ToString('N')
    $stage=Join-Path $parent ($name+'-stage-'+$id)
    $backup=Join-Path $parent ($name+'-backup-'+$id)
    $failed=Join-Path $parent ($name+'-failed-'+$id)
    $stopped=$false; $promoted=$false; $saved=$false
    if(-not $Files -or $Files.Count -eq 0){throw 'A protected package must include its primary executable.'}
    $restartOld=Test-Path -LiteralPath (Join-Path $InstallRoot $Files[0].Name) -PathType Leaf
    # Derived siblings are verified before any move or recursive cleanup.
    foreach ($path in @($stage,$backup,$failed)) {
        if ((Split-Path ([IO.Path]::GetFullPath($path))) -ne $parent) { throw 'Invalid package transaction path.' }
    }
    try {
        New-Item -ItemType Directory -Path $stage | Out-Null
        foreach ($file in $Files) {
            if ($file.Name -match '(^[\\/]|(^|[\\/])\.\.([\\/]|$)|:)' -or -not $file.Name) { throw 'Invalid package filename.' }
            $source=Get-Item -LiteralPath $file.Source -Force
            if ($source.PSIsContainer -or ($source.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid package source.' }
            $destination=Join-Path $stage $file.Name
            New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
            Copy-Item -LiteralPath $source.FullName -Destination $destination
            if ((Get-FileHash -LiteralPath $source.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) { throw 'Staged package hash differs from its source.' }
        }
        # All source reads and hashes finish while the old rescue service is still running.
        $stopped=$true
        & $Stop
        if (Test-Path -LiteralPath $InstallRoot) { Move-Item -LiteralPath $InstallRoot -Destination $backup; $saved=$true }
        Move-Item -LiteralPath $stage -Destination $InstallRoot
        $promoted=$true
        & $Prepare
        & $Start
    } catch {
        $problem=$_
        if ($stopped) {
            try {
                if ($promoted) { & $Stop; Move-Item -LiteralPath $InstallRoot -Destination $failed }
                if ($saved) { Move-Item -LiteralPath $backup -Destination $InstallRoot; $saved=$false }
                & $Restore
                if ($restartOld -and (Test-Path -LiteralPath $InstallRoot)) { & $Start }
            } catch { throw ('Package update failed: '+$problem.Exception.Message+'; rollback failed: '+$_.Exception.Message) }
        }
        throw $problem
    } finally {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    }
    if ($saved) {
        # The package is committed. Cleanup must not make callers roll their
        # selected profile back while the new protected service stays installed.
        try{Remove-Item -LiteralPath $backup -Recurse -Force}catch{Write-Warning 'Protected update succeeded; its old backup could not be removed.'}
    }
}
