using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoutePilot.Agent;

// A pending or unreadable marker is a persistent stop signal. Only the
// verified upgrade/recovery procedure may remove it.
internal sealed class NativeMaintenanceInProgress : IOException
{
    internal NativeMaintenanceInProgress():base("Native module maintenance requires completion or recovery."){}
}

internal static class NativeMaintenanceGate
{
    internal static string MarkerPath => Path.Combine(AppContext.BaseDirectory,"native-upgrade.pending");
    internal static bool IsPending()=>IsPendingAt(MarkerPath,File.GetAttributes);
    internal static bool IsPendingAt(string path,Func<string,FileAttributes> read)
    {
        try{_ = read(path);return true;}
        catch(FileNotFoundException){return false;}
        catch(DirectoryNotFoundException){return true;}
        catch{return true;}
    }
    internal static void ThrowIfPending(){if(IsPending())throw new NativeMaintenanceInProgress();}
    internal static JsonElement StatusOverlay(JsonElement? snapshot)
    {
        // Preserve read-only connection metadata; this is not a transition and
        // does not tear down or change the desired state of an existing session.
        var status=snapshot.HasValue?JsonNode.Parse(snapshot.Value.GetRawText()) as JsonObject:null;
        status??=new JsonObject();
        status["phase"]="Unavailable";
        status["errorCode"]=nameof(NativeMaintenanceInProgress);
        return JsonSerializer.SerializeToElement(status);
    }
    internal static T LoadChecked<T>(Func<bool> pending,Func<T> load)
    {
        if(pending())throw new NativeMaintenanceInProgress();
        return load();
    }
}
