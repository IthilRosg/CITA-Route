using System;

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
    public static bool Fresh(DateTime value,DateTime now,int seconds)=>value<=now.AddSeconds(2) && value>now.AddSeconds(-seconds);
    public static bool Matches(EngineSession session,SessionHealthState health,DateTime now)=>
        session.Running && session.BridgeReady && string.IsNullOrEmpty(session.VpnHoldReason) && !string.IsNullOrEmpty(session.Id) &&
        health.SessionId==session.Id && health.BridgeGeneration==session.BridgeGeneration && Fresh(health.UpdatedUtc,now,90);
    public static (bool Watching,SessionHealthState? Health,WatchState? Watch) Read(string root,EngineSession session,DateTime now)
    {
        var watch=AppPaths.Read(System.IO.Path.Combine(root,"runtime","watch-state.json"),()=>new WatchState());
        var health=AppPaths.Read(System.IO.Path.Combine(root,"runtime","session-health.json"),()=>new SessionHealthState());
        bool watching=session.Running && !string.IsNullOrEmpty(session.Id) && watch.SessionId==session.Id && Fresh(watch.UpdatedUtc,now,25);
        return(watching,Matches(session,health,now)?health:null,watching?watch:null);
    }
}
