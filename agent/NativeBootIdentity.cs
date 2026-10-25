using System.Diagnostics;
using System.Globalization;

namespace RoutePilot.Agent;

internal static class NativeBootIdentity
{
    static readonly Lazy<string> current=new(Read);
    internal static string Current=>current.Value;
    internal static bool MayResume(bool desired,string saved,string current)=>desired&&saved.Length>0&&saved==current;
    static string Read()
    {
        // Win32_OperatingSystem.LastBootUpTime is stable across SCM restart and
        // suspend. A failed query disables automatic resume rather than guessing.
        var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{"-NoProfile","-NonInteractive","-Command","(Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().Ticks"})info.ArgumentList.Add(arg);
        try{using var p=Process.Start(info)!;var output=p.StandardOutput.ReadToEndAsync();_=p.StandardError.BaseStream.CopyToAsync(Stream.Null);if(!p.WaitForExit(8000)){p.Kill(true);p.WaitForExit();return "";}string text=output.GetAwaiter().GetResult().Trim();return p.ExitCode==0&&long.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out long ticks)&&ticks>0?text:"";}catch{return "";}
    }
}
