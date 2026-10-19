using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RoutePilot;

public sealed record PreparedRelease(string Tag,string Package,string Archive);
public sealed class UpdateState
{
    public int Schema{get;set;}=1;
    public string Phase{get;set;}="Idle";
    public string NotifiedTag{get;set;}="";
    public PreparedRelease? Prepared{get;set;}
    public string OperationId{get;set;}="";
    public string Error{get;set;}="";
    public DateTime DeferredUntilUtc{get;set;}
    public DateTime NextRetryUtc{get;set;}
    public int NetworkRetries{get;set;}
}
public static class UpdateJournal
{
    public static UpdateState Read(string root)
    {
        var value=AppPaths.Read(Path.Combine(root,"data","update-state.json"),()=>new UpdateState());
        if(value.Schema!=1)return new UpdateState();
        value.Phase??="Idle";value.NotifiedTag??="";value.Error??="";value.OperationId??="";
        value.NetworkRetries=Math.Clamp(value.NetworkRetries,0,3);
        if(value.Prepared is { } prepared && (string.IsNullOrEmpty(prepared.Tag)||string.IsNullOrEmpty(prepared.Package)||string.IsNullOrEmpty(prepared.Archive)))
            return new UpdateState{Phase="Failed",Error="Журнал загруженного обновления повреждён. Загрузи пакет повторно."};
        return value;
    }
    public static void Save(string root,UpdateState value)=>AppPaths.Write(Path.Combine(root,"data","update-state.json"),value);
    public static void DeferNetwork(UpdateState state,DateTime now)
    {
        if(state.NetworkRetries>=3){state.Phase="Failed";return;}
        int[] delays={60,300,900};state.Phase="WaitingForNetwork";state.NextRetryUtc=now.AddSeconds(delays[state.NetworkRetries++]);
    }
    public static string StagedPath(string root,string relative)
    {
        if(Path.IsPathRooted(relative)||relative.Contains(':'))throw new InvalidDataException("Некорректный путь обновления.");
        string prefix=Path.GetFullPath(Path.Combine(root,"runtime","updates"))+Path.DirectorySeparatorChar;
        string result=Path.GetFullPath(Path.Combine(root,relative));
        if(!result.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Обновление находится вне каталога загрузок.");
        for(string? current=result;current!=null && current.StartsWith(prefix.TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase);current=Path.GetDirectoryName(current))
            if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Перенаправления в каталоге обновления запрещены.");
        return result;
    }
    public static PreparedRelease Prepare(string root,ReleaseUpdate release,string package)
    {
        string relative=Path.GetRelativePath(root,package);StagedPath(root,relative);
        string updates=Path.GetFullPath(Path.Combine(root,"runtime","updates"));
        string stage=Path.GetRelativePath(updates,package).Split(Path.DirectorySeparatorChar)[0];
        if(!Guid.TryParseExact(stage,"N",out _))throw new InvalidDataException("Неизвестный каталог загрузки.");
        string archive=Path.GetRelativePath(root,Path.Combine(updates,stage,release.FileName));StagedPath(root,archive);
        return new(release.Tag,relative,archive);
    }
    public static string? BlockReason(string root,bool busy,bool engineRunning,bool diagnostic,bool globalEngine)
    {
        if(busy)return "Дождись завершения текущей операции.";
        if(engineRunning||globalEngine)return "Обновление готово. Установка после отключения маршрута.";
        if(diagnostic)return "Установка после завершения диагностики и восстановления.";
        string session=Path.Combine(root,"runtime","session.json");
        if(File.Exists(session))
        {
            try{using var data=JsonDocument.Parse(File.ReadAllText(session));if(data.RootElement.GetProperty("running").GetBoolean())return "Сначала отключи маршрут в этой копии приложения.";}
            catch{return "Состояние сессии не прочитано. Автоустановка отложена.";}
        }
        foreach(string name in new[]{"connection-operation.lock","setup.lock","upgrade.lock","network-control.lock"})
        {
            try{using var held=new FileStream(Path.Combine(root,"runtime",name),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch{return "Другая операция управляет установкой или сетью.";}
        }
        return null;
    }
    public static bool GlobalEngineRunning()
    {
        try
        {
            foreach(string name in new[]{"smart-router","vpn-bridge"})
            {
                var processes=Process.GetProcessesByName(name);
                try{if(processes.Length>0)return true;}finally{foreach(var process in processes)process.Dispose();}
            }
            return false;
        }
        catch{return true;}
    }
}
