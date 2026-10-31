using System;

namespace RoutePilot;

internal static class NativeRoutePresentationTests
{
    internal static void Run()
    {
        LaunchJournal();
        if(NativeLaunchPolicy.MayProceed(true,true)||!NativeLaunchPolicy.MayProceed(false,true)||!NativeLaunchPolicy.WantsStart(true,true)||!NativeLaunchPolicy.WantsStart(true,false)||NativeLaunchPolicy.WantsStart(false,true))throw new Exception("Launch must not cancel or turn a stale connected snapshot into a stop.");
        if(!NativeLaunchPolicy.ConfirmedProfileMatches("same","same")||NativeLaunchPolicy.ConfirmedProfileMatches("old","new")||NativeLaunchPolicy.ConfirmedProfileMatches("same","")||NativeLaunchPolicy.ConfirmedProfileMatches("",""))throw new Exception("Autoconnect requires the confirmed native profile identity.");
        string own=new('a',32),foreign=new('b',32),before=new('c',32);
        if(NativeConnectionPolicy.SupportsProtocol(new(){Success=true,Native=new()})||NativeConnectionPolicy.SupportsProtocol(new(){Success=false,Native=new(){ConnectionProtocol=1}})||!NativeConnectionPolicy.SupportsProtocol(new(){Success=true,Native=new(){ConnectionProtocol=1}}))throw new Exception("An older module must not accept a non-CAS start.");
        var recovered=new NativeRouteView{Phase="Recovering",Desired=true,ConnectionId=own,Operation=new string('d',32)};
        if(NativeConnectionPolicy.StopIdentity(recovered)!=own||NativeConnectionPolicy.StopIdentity(new(){Phase="Importing",ConnectionId=own})!=null||NativeConnectionPolicy.StopIdentity(new(){Phase="Unconfigured",ConnectionId=own})!=null)throw new Exception("Manual stop must follow session identity, while import uses its operation.");
        if(!NativeConnectionPolicy.Owns(recovered,own)||NativeConnectionPolicy.ConfirmsAbsent(recovered,own,before))throw new Exception("A recovered operation must retain its own connection identity.");
        if(!NativeConnectionPolicy.ConfirmsAbsent(new(){Phase="Connected",Desired=true,ConnectionId=foreign},own,before)||NativeConnectionPolicy.ConfirmsAbsent(new(){Phase="Unavailable",ConnectionId=foreign},own,before))throw new Exception("Only a valid available foreign session can confirm our connection absent.");
        if(!NativeConnectionPolicy.ConfirmsAbsent(new(){Phase="Disconnected",Operation=before},own,before)||NativeConnectionPolicy.ConfirmsAbsent(new(){Phase="Disconnected",Operation=foreign},own,before)||NativeConnectionPolicy.ConfirmsAbsent(new(){Phase="Preparing",Desired=true},own,before))throw new Exception("Missing identity must not falsely confirm cleanup.");
        int stops=0,waits=0;
        System.Threading.Tasks.Task<NativeControlReply> Stop(string id){if(id!=own)throw new Exception("Wrong stop identity.");stops++;return System.Threading.Tasks.Task.FromResult(new NativeControlReply{Success=true});}
        System.Threading.Tasks.Task Wait(string id){if(id!=own)throw new Exception("Wrong wait identity.");waits++;return System.Threading.Tasks.Task.CompletedTask;}
        var foreignReply=new NativeControlReply{Success=true,Native=new(){Phase="Connected",Desired=true,ConnectionId=foreign}};
        if(!NativeConnectionPolicy.CleanupOwnAsync(own,before,()=>System.Threading.Tasks.Task.FromResult(foreignReply),Stop,Wait).GetAwaiter().GetResult()||stops!=0||waits!=0)throw new Exception("ACK-lost cleanup touched a foreign connection.");
        var ownedReply=new NativeControlReply{Success=true,Native=recovered};
        if(!NativeConnectionPolicy.CleanupOwnAsync(own,before,()=>System.Threading.Tasks.Task.FromResult(ownedReply),Stop,Wait).GetAwaiter().GetResult()||stops!=1||waits!=1)throw new Exception("ACK-lost cleanup did not stop its recovered connection.");
        bool unknown=false;try{NativeConnectionPolicy.CleanupOwnAsync(own,before,()=>System.Threading.Tasks.Task.FromResult(new NativeControlReply{Success=true,Native=new(){Phase="Unavailable"}}),Stop,Wait).GetAwaiter().GetResult();}catch(System.IO.IOException){unknown=true;}
        if(!unknown||stops!=1)throw new Exception("Unavailable status was treated as confirmed cleanup.");
        foreach(string phase in new[]{"Unavailable","RecoveryFailed"})
        {
            var maintenance=new NativeRouteView{Phase=phase,ErrorCode="NativeMaintenanceInProgress"};
            if(maintenance.Connected||!NativeRoutePresentation.RequiresAttention(maintenance)||NativeRoutePresentation.Title(maintenance)!="Обновление модуля"||!NativeRoutePresentation.Detail(maintenance).Contains("завершить восстановление",StringComparison.Ordinal))throw new Exception("Pending maintenance must not look connected or recover indefinitely.");
        }
        foreach(string failure in new[]{"CoreRecoveryUnconfirmed","GuardLost","GuardLostUnconfirmed","TransportCloseUnconfirmed","LeaseCloseUnconfirmed","PolicyRecoveryUnconfirmed"})
        {
            // An older service may still expose Connected as its outer phase.
            var old=new NativeRouteView{Phase="Connected",Desired=true,Runtime=new(){CoreState=failure}};
            var current=new NativeRouteView{Phase="RecoveryFailed",Desired=true,ErrorCode=failure,Runtime=new(){CoreState=failure}};
            foreach(var state in new[]{old,current})
                if(state.Connected||!NativeRoutePresentation.RequiresAttention(state)||NativeRoutePresentation.Title(state)!="Требуется внимание"||!NativeRoutePresentation.Detail(state).Contains("диагностику",StringComparison.Ordinal))
                    throw new Exception("An unconfirmed native protection failure was shown as connected or kept waiting.");
        }
        foreach(string pending in new[]{"CoreRepairFailed","GuardUnconfirmed","PolicyFailClosed","TransportDegraded","TransportUnavailable"})
        {
            var state=new NativeRouteView{Phase="Connected",Desired=true,Runtime=new(){CoreState=pending}};
            if(state.Connected||NativeRoutePresentation.RequiresAttention(state))throw new Exception("A pending check or confirmed repair was prematurely reported as terminal.");
        }
        var stopped=new NativeRouteView{Phase="Stopping",Desired=false,Runtime=new(){CoreState="GuardLostUnconfirmed"}};
        if(NativeRoutePresentation.Title(stopped)!="Отключаем VPN"||NativeRoutePresentation.RequiresAttention(stopped))throw new Exception("An old core failure overrode manual Stop.");
        var connected=new NativeRouteView{Phase="Connected",Desired=true,Runtime=new(){CoreState="Active"}};
        if(!connected.Connected||NativeRoutePresentation.RequiresAttention(connected)||NativeRoutePresentation.Title(connected)!="Подключено")throw new Exception("A verified active connection was hidden by failure presentation.");
    }
    static void LaunchJournal()
    {
        string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"cita-launch-"+Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var state=new NativeRouteView{Operation=new string('a',32),Generation=42,ProfileId=new string('B',64)};
            string cid=new('c',32),path=System.IO.Path.Combine(root,"runtime","native-launch-request.json");
            NativeLaunchRequest.Save(root,state,cid);
            using(var json=System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path)))
            using(var process=System.Diagnostics.Process.GetCurrentProcess())
            {
                var record=json.RootElement;
                if(record.GetProperty("connectionId").GetString()!=cid||record.GetProperty("initialOperation").GetString()!=state.Operation||
                   record.GetProperty("initialGeneration").GetInt64()!=42||record.GetProperty("profileId").GetString()!=state.ProfileId||
                   record.GetProperty("processId").GetInt32()!=process.Id||record.GetProperty("processStartedUtcTicks").GetInt64()!=process.StartTime.ToUniversalTime().Ticks)
                    throw new Exception("Launch evidence was not bound to its sender and expected state.");
            }
            string saved=System.IO.File.ReadAllText(path);bool rejected=false;
            try{NativeLaunchRequest.Save(root,state,"invalid");}catch(System.IO.IOException){rejected=true;}
            if(!rejected||System.IO.File.ReadAllText(path)!=saved||System.IO.Directory.GetFiles(System.IO.Path.GetDirectoryName(path)!).Length!=1)
                throw new Exception("Invalid launch identity damaged the preceding evidence.");
            string blocked=System.IO.Path.Combine(root,"blocked");System.IO.Directory.CreateDirectory(blocked);System.IO.File.WriteAllText(System.IO.Path.Combine(blocked,"runtime"),"not a directory");
            rejected=false;try{NativeLaunchRequest.Save(blocked,state,cid);}catch(System.IO.IOException){rejected=true;}
            if(!rejected)throw new Exception("Unwritten launch evidence was accepted.");
        }
        finally{System.IO.Directory.Delete(root,true);}
    }
}
