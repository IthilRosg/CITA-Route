using System.Text.Json;

namespace RoutePilot.Agent;

// Counter attribution only. This metadata never authorizes stopping a process.
internal static class WatchResourceIdentity
{
    internal sealed class Identity
    {
        public int Schema{get;set;}
        public int Pid{get;set;}
        public string Path{get;set;}="";
        public DateTime StartedUtc{get;set;}
        public string SessionId{get;set;}="";
    }
    sealed class Session{public string Id{get;set;}="";public bool Running{get;set;}}
    static readonly JsonSerializerOptions Json=new(){PropertyNameCaseInsensitive=true};
    internal static string Image=>System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
    static T? Read<T>(string path)
    {
        for(string? part=System.IO.Path.GetFullPath(path);part!=null;part=System.IO.Path.GetDirectoryName(part))
            if((File.Exists(part)||Directory.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException();
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(input.Length>16384)throw new InvalidDataException();
        return JsonSerializer.Deserialize<T>(input,Json);
    }
    internal static Identity? Load(string root,out bool limited)
    {
        limited=false;
        string sessionPath=System.IO.Path.Combine(root,"runtime","session.json");
        if(!File.Exists(sessionPath))return null;
        try
        {
            var session=Read<Session>(sessionPath);if(session?.Running!=true)return null;
            var identity=Read<Identity>(System.IO.Path.Combine(root,"runtime","watch-process.json"));
            if(identity is {Schema:1,Pid:>0}&&identity.StartedUtc!=default&&Guid.TryParseExact(identity.SessionId,"N",out _)&&identity.SessionId==session.Id&&string.Equals(identity.Path,Image,StringComparison.OrdinalIgnoreCase))return identity;
            limited=true;return null;
        }
        catch(Exception error)when(error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException){limited=true;return null;}
    }
    internal static bool Matches(Identity identity,int pid,DateTime start,string image)=>identity.Pid==pid&&
        Math.Abs((identity.StartedUtc.ToUniversalTime()-start.ToUniversalTime()).TotalMilliseconds)<100&&string.Equals(identity.Path,image,StringComparison.OrdinalIgnoreCase)&&string.Equals(image,Image,StringComparison.OrdinalIgnoreCase);
}
