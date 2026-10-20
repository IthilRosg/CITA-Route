using System;
using System.IO;
using Microsoft.Win32;

namespace RoutePilot;

internal static class StartupOptions
{
    const string Key="Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    public static string Command(string image)=>"\""+Path.GetFullPath(image)+"\" --startup";
    public static bool Enabled(string image)
    {using var key=Registry.CurrentUser.OpenSubKey(Key);return string.Equals(key?.GetValue("CITA Route") as string,Command(image),StringComparison.OrdinalIgnoreCase);}
    public static void Set(string image,bool enabled)
    {
        using var key=Registry.CurrentUser.CreateSubKey(Key,true);
        if(enabled)key.SetValue("CITA Route",Command(image),RegistryValueKind.String);
        else if(string.Equals(key.GetValue("CITA Route") as string,Command(image),StringComparison.OrdinalIgnoreCase))key.DeleteValue("CITA Route",false);
    }
}
