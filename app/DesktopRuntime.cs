using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace RoutePilot;

internal static class DesktopRuntime
{
    // The rescue service runs as SYSTEM; a private/user SDK is insufficient.
    internal static readonly Version Minimum=new(10,0,13);
    internal static bool Available(Version? minimum=null)
    {
        try
        {
            using var registry=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
            using var key=registry.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64");
            string root=key?.GetValue("InstallLocation") as string??Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432")??Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"dotnet");
            return At(root,minimum);
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException){return false;}
    }
    static bool Regular(string path)
    {
        for(string? item=Path.GetFullPath(path);item!=null;item=Path.GetDirectoryName(item))
            if((File.Exists(item)||Directory.Exists(item))&&(File.GetAttributes(item)&FileAttributes.ReparsePoint)!=0)return false;
        return true;
    }
    internal static bool At(string root,Version? minimum=null)
    {
        minimum??=Minimum;
        if(minimum.Major!=10||minimum.Minor!=0||minimum<Minimum||minimum.Revision>=0)return false;
        if(!File.Exists(Path.Combine(root,"dotnet.exe"))||!Regular(Path.Combine(root,"dotnet.exe")))return false;
        return new[]{"Microsoft.NETCore.App","Microsoft.WindowsDesktop.App"}.All(framework=>
        {
            string shared=Path.Combine(root,"shared",framework);
            string required=framework=="Microsoft.NETCore.App"?"System.Private.CoreLib.dll":"Microsoft.WindowsDesktop.App.deps.json";
            return Directory.Exists(shared) && Regular(shared) && Directory.EnumerateDirectories(shared).Any(path=>
                Version.TryParse(Path.GetFileName(path),out var version) && version.Major==10 && version.Minor==0 &&
                version>=minimum && version.Revision<0 && File.Exists(Path.Combine(path,required)) && Regular(Path.Combine(path,required)));
        });
    }
}
