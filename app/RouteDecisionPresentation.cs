using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RoutePilot;

internal sealed class RouteDecisionKeyView
{
    public string Epoch{get;set;}="";
    public string Domain{get;set;}="";
    public string Application{get;set;}="";
}
internal sealed class RouteDecisionView
{
    public RouteDecisionKeyView Key{get;set;}=new();
    public string Route{get;set;}="";
    public string Reason{get;set;}="";
    public string Evidence{get;set;}="";
    public DateTime CheckedUtc{get;set;}
    public DateTime ExpiresUtc{get;set;}
}
internal sealed record RouteDecisionRow(string Domain,string Application,string Route,string State,string Checked,string Detail,DateTime CheckedUtc);
internal static class RouteDecisionPresentation
{
    internal static RouteDecisionRow[] Read(JsonElement state,string epoch,bool live,DateTime now)
    {
        if(!live||!state.TryGetProperty("decisions",out var array)||array.ValueKind!=JsonValueKind.Array||array.GetArrayLength()>256)return Array.Empty<RouteDecisionRow>();
        bool applied=state.TryGetProperty("publication",out var publication)&&publication.ValueKind==JsonValueKind.Object&&publication.TryGetProperty("phase",out var phase)&&phase.GetString()=="Applied";
        var rows=new List<RouteDecisionRow>();
        foreach(var value in array.EnumerateArray())
        {
            RouteDecisionView? decision;
            try{decision=value.Deserialize<RouteDecisionView>(AppPaths.Json);}catch(JsonException){continue;}
            if(decision?.Key==null||decision.Key.Epoch!=epoch||string.IsNullOrEmpty(decision.Key.Domain)||decision.Key.Domain.Length>253||
               decision.Key.Domain.IndexOfAny(new[]{'/',':','\\','\r','\n','\0'})>=0||decision.CheckedUtc>now||decision.CheckedUtc==default||
               decision.ExpiresUtc<=decision.CheckedUtc||decision.Route is not ("direct" or "dpi" or "vpn"))continue;
            string route=decision.Route switch{"direct"=>"Напрямую","dpi"=>"DPI",_=>"VPN"};
            string status=!applied?"Не подтверждено":decision.ExpiresUtc<=now?"Перепроверка":"Подтверждено";
            string evidence=decision.Evidence switch{"BodyComplete"=>"Ограниченный ответ прочитан целиком","BodySample"=>"Прочитана ограниченная часть ответа",_=>"Проверены заголовки / транспорт"};
            string reason=decision.Reason??"";
            reason=Backend.Redact(reason[..Math.Min(reason.Length,600)]).Replace('\r',' ').Replace('\n',' ');
            rows.Add(new(decision.Key.Domain,Path.GetFileName(decision.Key.Application??""),route,status,
                decision.CheckedUtc.ToLocalTime().ToString("HH:mm:ss"),reason+"\n"+evidence+
                "\nСрок решения: "+decision.ExpiresUtc.ToLocalTime().ToString("HH:mm:ss")+
                ". Это решение контроллера для новых соединений, а не доказательство пути каждого пакета.",decision.CheckedUtc));
        }
        return rows.OrderByDescending(r=>r.CheckedUtc).Take(20).ToArray();
    }
}
