using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace RoutePilot;

// One automatic start is admitted by InitializeBackgroundAsync per process.
// Publish its identity before IPC, so diagnostics can distinguish a lost ACK
// from another connection. This local record is evidence, not authorization.
internal static class NativeLaunchRequest
{
    internal static void Save(string root,NativeRouteView state,string connectionId)
    {
        if(!NativeConnectionPolicy.ValidId(state.Operation)||!NativeConnectionPolicy.ValidId(connectionId)||state.ProfileId.Length!=64)
            throw new IOException("Не удалось записать идентификатор автоподключения.");
        using var process=Process.GetCurrentProcess();
        var record=new{schema=1,processId=process.Id,processStartedUtcTicks=process.StartTime.ToUniversalTime().Ticks,
            connectionId,initialOperation=state.Operation,initialGeneration=state.Generation,profileId=state.ProfileId,createdUtc=DateTime.UtcNow};
        string directory=Path.Combine(root,"runtime"),path=Path.Combine(directory,"native-launch-request.json");
        Directory.CreateDirectory(directory);string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {JsonSerializer.Serialize(file,record,AppPaths.Json);file.Flush(true);}
            if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
        }
        finally{File.Delete(temporary);}
    }
}
