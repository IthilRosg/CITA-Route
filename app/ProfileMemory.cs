using System;
using System.IO;
using System.Linq;

namespace RoutePilot;

public sealed record ProfileChoice(string Name,string Cli,DateTime UsedUtc);
public sealed class ProfileHistory
{
    public int Schema{get;set;}=1;
    public ProfileChoice? Applied{get;set;}
    public ProfileChoice? Used{get;set;}
}
public sealed class ProfileMemory
{
    readonly string _path;
    public static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CITA","Route","profile-history.json");
    public ProfileMemory(string? path=null)=>_path=path??DefaultPath;
    public ProfileHistory Read()
    {
        var value=AppPaths.Read(_path,()=>new ProfileHistory());
        bool Valid(ProfileChoice? choice)=>choice!=null && !string.IsNullOrWhiteSpace(choice.Name) && !string.IsNullOrWhiteSpace(choice.Cli) && Path.IsPathFullyQualified(choice.Cli) && choice.UsedUtc<=DateTime.UtcNow.AddMinutes(5);
        if(!Valid(value.Applied))value.Applied=null;if(!Valid(value.Used))value.Used=null;
        return value;
    }
    static bool Matches(ProfileChoice value,string cli)=>string.Equals(value.Cli,cli,StringComparison.OrdinalIgnoreCase) && value.Name.Length>0;
    public string Preferred(string cli,string applied)
    {
        var history=Read();if(history.Schema!=1)return applied;
        return new[]{history.Applied,history.Used}.Where(v=>v!=null && Matches(v,cli)).OrderByDescending(v=>v!.UsedUtc).FirstOrDefault()?.Name??applied;
    }
    public string? Select(string cli,string applied,string current,string[] available)
    {
        if(available.Contains(current))return current;
        string preferred=Preferred(cli,applied);
        if(preferred.Length>0)return available.Contains(preferred)?preferred:null;
        return available.Length==1?available[0]:null;
    }
    public void Remember(string name,string cli,DateTime at,bool used)
    {
        if(name.Length==0||cli.Length==0)return;
        // Different installations share only profile metadata; never overwrite
        // a more recent choice with an older folder's migration.
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var held=new FileStream(_path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var history=Read();if(history.Schema!=1)return;
        var previous=used?history.Used:history.Applied;
        if(previous!=null && previous.UsedUtc>=at)return;
        var choice=new ProfileChoice(name,cli,at);
        if(used)history.Used=choice;else history.Applied=choice;
        AppPaths.Write(_path,history);
    }
}
