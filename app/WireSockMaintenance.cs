using System;
using System.Diagnostics;
using System.IO;

namespace RoutePilot;

internal sealed class WireSockMaintenanceState
{
    public int Schema{get;set;}=1;
    public string Phase{get;set;}="";
    public int InstallerPid{get;set;}
    public DateTime InstallerStartedUtc{get;set;}
    public DateTime UpdatedUtc{get;set;}
}
internal static class WireSockMaintenance
{
    static string PathFor(string root)=>Path.Combine(root,"data","wiresock-maintenance.json");
    internal static void Write(string root,string phase,Process? installer=null)=>AppPaths.Write(PathFor(root),new WireSockMaintenanceState{
        Phase=phase,InstallerPid=installer?.Id??0,InstallerStartedUtc=installer?.StartTime.ToUniversalTime()??default,UpdatedUtc=DateTime.UtcNow});
    internal static bool InstallerRunning()
    {
        var processes=Process.GetProcessesByName("wiresock-secure-connect-x64-3.6.1.1");
        try{return processes.Length>0;}finally{foreach(var process in processes)process.Dispose();}
    }
    internal static string? BlockReason(string root)
    {
        if(InstallerRunning())return "Установщик WireSock ещё работает. Дождись завершения перед подключением.";
        string path=PathFor(root);if(!File.Exists(path))return null;
        if(new FileInfo(path).Length>8192)return "Журнал обслуживания WireSock повреждён. Повтори поиск WireSock в настройках.";
        var state=AppPaths.Read<WireSockMaintenanceState?>(path,()=>null);
        if(state?.Schema!=1||state.Phase is not ("Verified" or "Cancelled" or "FailedBeforeInstall"))
            return "Предыдущее обслуживание WireSock не подтверждено. Заверши установщик и повтори поиск WireSock в настройках.";
        return null;
    }
    internal static void Reconcile(string root,ConnectionInventory inventory)
    {
        if(!File.Exists(PathFor(root))||InstallerRunning()||!inventory.CliTrusted||!inventory.ManagementReady||inventory.Message.Length>0||
            WireSockCompatibility.Parse(inventory.Version)!=WireSockCompatibility.Reviewed)return;
        if(BlockReason(root)!=null)Write(root,"Verified");
    }
}
