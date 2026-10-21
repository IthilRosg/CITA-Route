using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace RoutePilot;

internal static class DesktopRuntime
{
    // The rescue service runs as SYSTEM; a private/user SDK is insufficient.
    internal static bool Available()
    {
        try
        {
            using var registry=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
            using var key=registry.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64");
            string root=key?.GetValue("InstallLocation") as string??Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432")??Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"dotnet");
            return At(root);
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException){return false;}
    }
    internal static bool At(string root)
    {
        if(!File.Exists(Path.Combine(root,"dotnet.exe")))return false;
        return new[]{"Microsoft.NETCore.App","Microsoft.WindowsDesktop.App"}.All(framework=>
        {
            string shared=Path.Combine(root,"shared",framework);
            string required=framework=="Microsoft.NETCore.App"?"System.Private.CoreLib.dll":"Microsoft.WindowsDesktop.App.deps.json";
            return Directory.Exists(shared) && Directory.EnumerateDirectories(shared).Any(path=>
                Version.TryParse(Path.GetFileName(path),out var version) && version.Major==8 && version.Minor==0 &&
                version.Build>=0 && version.Revision<0 && File.Exists(Path.Combine(path,required)));
        });
    }
}
