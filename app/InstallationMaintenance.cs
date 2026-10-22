using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;

namespace RoutePilot;

public static class InstallationMaintenance
{
    public static bool Active(string root)=>Problem(root)!=null;
    public static string? Problem(string root)
    {
        string path=Path.Combine(root,"runtime","installation-maintenance.lock");
        if(File.Exists(path))
        {
            try{using var held=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException){return "Идёт установка или удаление CITA Route. Дождись завершения и открой приложение снова.";}
        }
        string journal=Path.Combine(root,"runtime","installation-transaction.json");
        if(!File.Exists(journal))return null;
        try
        {
            if(new FileInfo(journal).Length>4*1024*1024)throw new InvalidDataException();
            using var file=new FileStream(journal,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            using var document=JsonDocument.Parse(file);var value=document.RootElement;
            if(value.GetProperty("schema").GetInt32()==1&&
               string.Equals(Path.GetFullPath(value.GetProperty("root").GetString()!).TrimEnd(Path.DirectorySeparatorChar),Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase)&&
               value.GetProperty("phase").GetString() is "Committed" or "RolledBack")return null;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException){}
        return "Предыдущая установка CITA Route не завершена. Повтори установщик: он восстановит сохранённые файлы перед продолжением.";
    }
}
