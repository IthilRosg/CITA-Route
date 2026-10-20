using System;
using System.IO;
using System.Text.Json;

namespace Cita.Startup;

public sealed class StartupState
{
    public int Schema{get;set;}=1;
    public string SourceRoot{get;set;}="";
    public bool Enabled{get;set;}
    public string Phase{get;set;}="Disabled";
    public string Message{get;set;}="";
    public DateTime UpdatedUtc{get;set;}
    public DateTime BootUtc{get;set;}
    public bool Active=>Phase is "WaitingForNetwork" or "Starting" or "Connected" or "Stopping" or "StopFailed" or "NeedsAttention";
    public bool Owns(string root)=>Schema==1&&SourceRoot.Length>0&&Path.GetFullPath(SourceRoot).TrimEnd('\\').Equals(Path.GetFullPath(root).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);
}

public static class StartupPaths
{
    public static string AgentDirectory=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RoutePilot","Agent");
    public static string Client=>Path.Combine(AgentDirectory,"startup-client");
    public static string StateFile=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RoutePilot","startup-state.json");
    public static StartupState Read()
    {
        try
        {
            using var stream=new FileStream(StateFile,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(stream.Length>16384)return new();
            return JsonSerializer.Deserialize<StartupState>(stream,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??new();
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException){return new();}
    }
    public static string RoutingRoot(string root)
    {var state=Read();return state.Active&&state.Owns(root)?Client:root;}
    public static bool SameBoot(DateTime recorded,DateTime boot)=>recorded!=default&&Math.Abs((recorded.ToUniversalTime()-boot.ToUniversalTime()).TotalSeconds)<10;
}
