using System;
using System.IO;
using System.Text.Json;

namespace RoutePilot;

public sealed class SessionHealthState
{
    public string SessionId{get;set;}="";
    public int BridgeGeneration{get;set;}
    public bool Healthy{get;set;}
    public string Code{get;set;}="";
    public string Message{get;set;}="";
    public DateTime UpdatedUtc{get;set;}
}
public sealed class WatchState
{
    public string SessionId{get;set;}="";
    public string Phase{get;set;}="";
    public string Reason{get;set;}="";
    public string WireSockState{get;set;}="";
    public DateTime UpdatedUtc{get;set;}
}
public static class SessionHealthCache
{
    public static (string Label,string Detail,bool Connected) Presentation(EngineSession session,WatchState? watch,SessionHealthState? health,bool wireSockConnected,bool verified,DateTime now)
    {
        if(!session.Running)return("Отключено","Маршрутизатор остановлен.",false);
        bool currentWatch=watch!=null&&watch.SessionId==session.Id&&Fresh(watch.UpdatedUtc,now,25);
        if(!string.IsNullOrEmpty(session.VpnHoldReason))return("Проверь подключение","Безопасность VPN не подтверждена. Проверь профиль и журнал.",false);
        if(currentWatch&&watch!.Phase=="WaitingForNetwork")return("Ожидание сети","Ожидаем доступную сеть. Подключение восстановится автоматически.",false);
        if(currentWatch&&watch!.Phase=="NeedsAttention")return("Требуется внимание",Attention(watch.Reason),false);
        if(session.Recovering||currentWatch&&watch!.Phase=="Recovering"||!wireSockConnected||!session.BridgeReady)
            return("Восстановление туннеля","Восстанавливаем VPN. «Отключить» отменит восстановление.",false);
        if(health!=null&&Matches(session,health,now)&&!health.Healthy)
            return("Передача не подтверждена","Туннель подключён, но контрольный HTTPS-запрос не прошёл. Повторяем проверку.",false);
        if(verified)return("Подключено","Контрольный HTTPS/IPv4 через VPN проверен. Доступность всех сайтов этим не подтверждается.",true);
        return("Проверка подключения","Туннель подключён. Ожидаем подтверждение передачи через VPN.",false);
    }
    sealed class SafetyHold {public int Schema{get;set;}public string SessionId{get;set;}="";public bool Hold{get;set;}public string Code{get;set;}="";}
    public static string Hold(string root,EngineSession session)
    {
        if(!session.Running)return "";
        try
        {
            using var stream=new FileStream(Path.Combine(Cita.Startup.StartupPaths.RoutingRoot(root),"runtime","bridge-safety.json"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(stream.Length>16384)return "SafetyStateUnavailable";
            var value=JsonSerializer.Deserialize<SafetyHold>(stream,AppPaths.Json);
            if(value==null||value.Schema!=1||string.IsNullOrEmpty(value.SessionId)||!value.Hold)return "SafetyStateUnavailable";
            return value.SessionId==session.Id?value.Code=="EgressMismatch"?"EgressMismatch":"SafetyStateUnavailable":"";
        }
        catch(FileNotFoundException){return "";}
        catch(DirectoryNotFoundException){return "";}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException){return "SafetyStateUnavailable";}
    }
    public static string Attention(string reason)=>reason switch
    {
        "Component retry limit reached"=>"Автовосстановление исчерпало попытки. Подробности — в журнале.",
        "Another WireSock profile is active"=>"В WireSock выбран другой профиль. Проверь подключение.",
        "Policy identity or progress unavailable"=>"Работа управления маршрутами не подтверждена. Проверь журнал.",
        "Component operation exceeded its deadline; bridge safety remains active"=>"Восстановление выполняется слишком долго. Проверь журнал.",
        "VPN paused while another component operation is in progress"=>"VPN приостановлен. Выполняется операция восстановления.",
        _=>"Проверь профиль подключения и расширенный журнал."
    };
    public static bool Fresh(DateTime value,DateTime now,int seconds)=>value<=now.AddSeconds(2) && value>now.AddSeconds(-seconds);
    public static bool Matches(EngineSession session,SessionHealthState health,DateTime now)=>
        session.Running && session.BridgeReady && string.IsNullOrEmpty(session.VpnHoldReason) && !string.IsNullOrEmpty(session.Id) &&
        health.SessionId==session.Id && health.BridgeGeneration==session.BridgeGeneration && Fresh(health.UpdatedUtc,now,90);
    public static (bool Watching,SessionHealthState? Health,WatchState? Watch) Read(string root,EngineSession session,DateTime now)
    {
        if(string.IsNullOrEmpty(session.VpnHoldReason))session.VpnHoldReason=Hold(root,session);
        var watch=AppPaths.Read(System.IO.Path.Combine(root,"runtime","watch-state.json"),()=>new WatchState());
        var health=AppPaths.Read(System.IO.Path.Combine(root,"runtime","session-health.json"),()=>new SessionHealthState());
        bool watching=session.Running && !string.IsNullOrEmpty(session.Id) && watch.SessionId==session.Id && Fresh(watch.UpdatedUtc,now,25);
        return(watching,Matches(session,health,now)?health:null,watching?watch:null);
    }
}
