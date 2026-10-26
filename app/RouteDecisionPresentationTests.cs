using System;
using System.Text.Json;

namespace RoutePilot;

internal static class RouteDecisionPresentationTests
{
    internal static void Run()
    {
        var now=new DateTime(2026,10,24,0,0,0,DateTimeKind.Utc);
        JsonDocument State(string epoch,DateTime checkedUtc,string route="vpn",string publication="Applied")=>JsonDocument.Parse(JsonSerializer.Serialize(new{
            publication=new{phase=publication},decisions=new[]{new{Key=new{Epoch=epoch,Domain="example.com",Application=@"C:\Users\Private\browser.exe"},Route=route,
                Reason="Two successful probes",Evidence="BodySample",CheckedUtc=checkedUtc,ExpiresUtc=checkedUtc.AddMinutes(15)}}}));
        using var valid=State("current",now.AddMinutes(-1));
        var rows=RouteDecisionPresentation.Read(valid.RootElement,"current",true,now);
        if(rows.Length!=1||rows[0].Application!="browser.exe"||!rows[0].Detail.Contains("не доказательство"))throw new Exception("Route explanation lost its scope or exposed full process path.");
        if(RouteDecisionPresentation.Read(valid.RootElement,"other",true,now).Length!=0||RouteDecisionPresentation.Read(valid.RootElement,"current",false,now).Length!=0)throw new Exception("Stale session decisions shown as current.");
        using var future=State("current",now.AddMinutes(1));
        if(RouteDecisionPresentation.Read(future.RootElement,"current",true,now).Length!=0)throw new Exception("Future route evidence accepted.");
        using var pending=State("current",now.AddMinutes(-1),publication:"Applying");
        if(RouteDecisionPresentation.Read(pending.RootElement,"current",true,now)[0].State!="Не подтверждено")throw new Exception("Unapplied decision shown as confirmed.");
        using var expired=State("current",now.AddMinutes(-16));
        if(RouteDecisionPresentation.Read(expired.RootElement,"current",true,now)[0].State!="Перепроверка")throw new Exception("Expired evidence presented as fresh.");
    }
}
