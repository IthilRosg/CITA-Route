using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoutePilot;

public static class AppPaths
{
    public static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i=0;i<7 && directory!=null;i++,directory=directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName,"SmartRouting.ps1"))) return directory.FullName;
        throw new InvalidOperationException("Рядом с приложением не найден SmartRouting.ps1. Сохрани всю папку RoutePilot целиком.");
    }
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive=true,WriteIndented=true };
    public static T Read<T>(string path,Func<T> fallback)
    {
        try
        {
            if(Path.GetFileName(Path.GetDirectoryName(path))=="runtime"&&Path.GetFileName(path) is "session.json" or "watch-state.json" or "session-health.json" or "policy-state.json")
            {
                string root=Path.GetDirectoryName(Path.GetDirectoryName(path))!;
                path=Path.Combine(Cita.Startup.StartupPaths.RoutingRoot(root),"runtime",Path.GetFileName(path));
            }
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream,Json) ?? fallback();
        }
        catch { return fallback(); }
    }
    public static void Write<T>(string path,T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.WriteAllText(temporary,JsonSerializer.Serialize(value,Json),new UTF8Encoding(false));
            if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
        }
        finally{File.Delete(temporary);}
    }
}

public static class Format
{
    public static string Bytes(double n)
    {
        string[] units={"Б","КБ","МБ","ГБ","ТБ"}; int index=0;
        while(n>=1024 && index<units.Length-1){ n/=1024; index++; }
        return n.ToString(index==0?"0":"0.0",CultureInfo.GetCultureInfo("ru-RU")) + " " + units[index];
    }
    public static string Rate(double n) => Bytes(n) + "/с";
    public static DateTime MoscowNow => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time"));
    public static string Day => MoscowNow.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
    public static string Time => MoscowNow.ToString("HH:mm:ss");
    public static string Uptime(DateTime start)
    { var t=DateTime.UtcNow-start.ToUniversalTime(); return t.TotalHours>=1 ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}"; }
}

public record RatePoint(DateTime Time,double Upload,double Download);
public class ByteCount { public long Upload{get;set;} public long Download{get;set;} public long Total=>Upload+Download; }
public sealed class TrafficHistory
{
    public string Epoch{get;set;}="";
    public long LastUpload{get;set;}
    public long LastDownload{get;set;}
    public Dictionary<string,ByteCount> Days{get;set;}=new();
    public long TotalUpload{get;set;}
    public long TotalDownload{get;set;}
    public DateTime CreatedUtc{get;set;}=DateTime.UtcNow;
    public (long Up,long Down) Apply(string epoch,long upload,long download,string day)
    {
        if(string.IsNullOrWhiteSpace(epoch)) throw new ArgumentException("An engine session is required.");
        upload=Math.Max(0,upload); download=Math.Max(0,download);
        long up=epoch==Epoch && upload>=LastUpload ? upload-LastUpload : upload;
        long down=epoch==Epoch && download>=LastDownload ? download-LastDownload : download;
        Epoch=epoch; LastUpload=upload; LastDownload=download;
        TotalUpload+=up; TotalDownload+=down;
        if(!Days.TryGetValue(day,out var count)){count=new ByteCount();Days[day]=count;}
        count.Upload+=up;count.Download+=down;
        return(up,down);
    }
}
public sealed class Preferences
{
    public bool CloseToTray{get;set;}=true;
    public int RefreshSeconds{get;set;}=1;
    public bool AutoInstallUpdates{get;set;}=true;
    public string ViewMode{get;set;}="Compact";
    public bool ResourceNotificationsEnabled{get;set;}
    public string ResourceNotificationKey{get;set;}="";
    public DateTime ResourceNotificationAt{get;set;}
    public int ResourceNotificationLevel{get;set;}
    public WindowPlacement CompactWindow{get;set;}=new(){Width=420,Height=490};
    public WindowPlacement AdvancedWindow{get;set;}=new(){Width=1360,Height=900};
}
public sealed class WindowPlacement
{
    public double Width{get;set;}
    public double Height{get;set;}
    public double? Left{get;set;}
    public double? Top{get;set;}
    public bool Maximized{get;set;}
    public static double Bounded(double value,double fallback,double min,double max)=>Math.Clamp(double.IsFinite(value)&&value>0?value:fallback,min,Math.Max(min,max));
}
public sealed class RoutingPolicyPreferences { public string Mode{get;set;}="Auto";public bool AllowListedDpi{get;set;}public bool ExperimentalFastTcp{get;set;} }
public sealed class EngineSession
{
    public bool Running{get;set;}
    public bool Recovering{get;set;}
    public bool BridgeReady{get;set;}
    public int RouterPid{get;set;}
    public int BridgePid{get;set;}
    public int BridgeGeneration{get;set;}
    public DateTime RouterStartedUtc{get;set;}
    public string VpnHoldReason{get;set;}="";
    public string Id{get;set;}="";
    public string Mode{get;set;}="";
    public string SourceProfile{get;set;}="";
    public string NetworkSignature{get;set;}="";
    public DateTime StartedUtc{get;set;}
}
public sealed class ConnectionRow
{
    public string Id{get;set;}="";
    public string Host{get;set;}="";
    public string Process{get;set;}="—";
    public string Network{get;set;}="";
    public bool IsVpn{get;set;}
    public bool IsDpi{get;set;}
    public string Route=>IsVpn?"VPN":IsDpi?"DPI":"Напрямую";
    public string RouteColor=>IsVpn?"#E8443F":IsDpi?"#D7AD67":"#86A9C8";
    public string Rule{get;set;}="";
    public long Upload{get;set;}
    public long Download{get;set;}
    public string Uploaded=>Format.Bytes(Upload);
    public string Downloaded=>Format.Bytes(Download);
    public string Address{get;set;}="";
    public string Started{get;set;}="";
}
public sealed class Snapshot
{
    public long Upload{get;set;}
    public long Download{get;set;}
    public List<ConnectionRow> Connections{get;set;}=new();
    public static Snapshot Parse(string json)
    {
        try {
        using var document=JsonDocument.Parse(json);var root=document.RootElement;
        var snapshot=new Snapshot { Upload=root.GetProperty("uploadTotal").GetInt64(),Download=root.GetProperty("downloadTotal").GetInt64() };
        if(root.TryGetProperty("connections",out var connections) && connections.ValueKind==JsonValueKind.Array)
        foreach(var connection in connections.EnumerateArray())
        {
            var metadata=connection.GetProperty("metadata");
            string Read(JsonElement item,string key)=>item.TryGetProperty(key,out var value)?value.ToString():"";
            string host=Read(metadata,"host"),ip=Read(metadata,"destinationIP"),path=Read(metadata,"processPath");
            path=Regex.Replace(path,@" \(\d+\)$","");
            bool vpn=connection.TryGetProperty("chains",out var chains) && chains.EnumerateArray().Any(c=>c.GetString()=="vpn");
            snapshot.Connections.Add(new ConnectionRow{
                Id=Read(connection,"id"),Host=string.IsNullOrEmpty(host)?ip:host,
                Address=(ip.Contains(':')?"["+ip+"]":ip)+":"+Read(metadata,"destinationPort"),Process=string.IsNullOrEmpty(path)?"—":Path.GetFileName(path),
                Network=Read(metadata,"network").ToUpperInvariant(),IsVpn=vpn,IsDpi=connection.TryGetProperty("chains",out var dpiChains) && dpiChains.EnumerateArray().Any(c=>c.GetString() is "dpi" or "dpi-auto"),Rule=Read(connection,"rule"),
                Upload=connection.GetProperty("upload").GetInt64(),Download=connection.GetProperty("download").GetInt64(),Started=Read(connection,"start")
            });
        }
        return snapshot;
        } catch(Exception ex)when(ex is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException) {
            throw new JsonException("Некорректный ответ API статистики.",ex);
        }
    }
}
public sealed class UserRule { public string Domain{get;set;}="";public string Target{get;set;}="VPN";public string Color=>Target=="VPN"?"#E8443F":"#86A9C8"; }
public static class DomainRules
{
    public static string Normalize(string input)
    {
        string domain=input.Trim().TrimEnd('.').ToLowerInvariant();
        try{domain=new IdnMapping().GetAscii(domain);}catch{throw new ArgumentException("Некорректное имя сайта.");}
        if(!Regex.IsMatch(domain,@"^(?=.{1,253}$)([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9-]{2,63}$") || System.Net.IPAddress.TryParse(domain,out _))
            throw new ArgumentException("Введи домен без https://, пути и звёздочки. Например: example.com");
        return domain;
    }
    public static bool Matches(string domain,string suffix)=>domain==suffix || domain.EndsWith("."+suffix,StringComparison.OrdinalIgnoreCase);
    public static List<string> Read(string path)
    {
        if(!File.Exists(path))return new();
        return File.ReadAllLines(path).Select(line=>line.Split('#')[0].Trim()).Where(line=>line.Length>0).Select(Normalize).Distinct().Order().ToList();
    }
    public static void Save(string root,List<UserRule> rules)
    {
        foreach(var direct in rules.Where(r=>r.Target=="Напрямую"))
        foreach(var vpn in rules.Where(r=>r.Target=="VPN"))
            if(Matches(direct.Domain,vpn.Domain)||Matches(vpn.Domain,direct.Domain))throw new ArgumentException("Правила конфликтуют: "+direct.Domain+" и "+vpn.Domain+". Удали пересекающееся правило.");
        foreach(var target in new[]{"VPN","Напрямую"})
        {
            var domains=rules.Where(r=>r.Target==target).Select(r=>Normalize(r.Domain)).Distinct().Order().ToArray();
            string file=target=="VPN"?"force-vpn.txt":"force-direct.txt";
            string path=Path.Combine(root,file),temporary=path+".tmp";
            File.WriteAllText(temporary,"# Managed by RoutePilot. Subdomains are included.\n"+string.Join("\n",domains)+"\n",new UTF8Encoding(false));
            File.Move(temporary,path,true);
            string local=target=="VPN"?"personal-vpn.json":"personal-direct.json";
            object[] conditions=domains.Length==0?Array.Empty<object>():new object[]{new{domain_suffix=domains}};
            AppPaths.Write(Path.Combine(root,"runtime","rules",local),new{version=3,rules=conditions});
        }
    }
}
