using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace RoutePilot.Agent;

internal static class NativeMaintenanceValidation
{
    internal static bool MayUseOperation(ProcessIdentity? owner,ProcessIdentity caller)
    {
        if(owner==null||!owner.Matches())return true;
        return owner.Pid==caller.Pid&&Math.Abs((owner.StartedUtc-caller.StartedUtc).TotalMilliseconds)<100&&string.Equals(owner.Path,caller.Path,StringComparison.OrdinalIgnoreCase)&&caller.Matches()&&owner.Matches();
    }
    internal static bool MayUseOperation(ProcessIdentity? owner,Process caller)=>MayUseOperation(owner,ProcessIdentity.From(caller));
    internal static void CheckMarkerBytes(ReadOnlySpan<byte> bytes,string? expected)
    {
        if(expected is not {Length:32}||expected.Any(c=>!char.IsAsciiHexDigit(c)||char.IsUpper(c))||bytes.Length!=33||bytes[32]!=10||!bytes[..32].SequenceEqual(Encoding.ASCII.GetBytes(expected)))throw new InvalidDataException("Maintenance transaction identity refused.");
    }
    internal static void CheckIntent(Stream stream)
    {
        if(stream.Length is <1 or >4096)throw new InvalidDataException("Maintenance intent size refused.");
        using var doc=JsonDocument.Parse(stream);var root=doc.RootElement;
        if(root.ValueKind!=JsonValueKind.Object||root.EnumerateObject().Count(x=>x.NameEquals("Desired"))!=1||root.GetProperty("Desired").ValueKind!=JsonValueKind.False)throw new InvalidDataException("Maintenance requires a disconnected intent.");
        stream.Position=0;
    }
    internal static T LoadChecked<T>(Stream intent,Func<T> load)
    {
        CheckIntent(intent);
        return load();
    }
    internal static FileStream OpenProtected(string path)
    {
        var file=new FileInfo(Path.GetFullPath(path));NativeTrustedBundle.CheckAcl(file);for(var parent=file.Directory;parent!=null;parent=parent.Parent)NativeTrustedBundle.CheckAcl(parent,true);
        return new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.Read);
    }
    internal static void CheckMarker(string? expected)
    {
        using var marker=OpenProtected(NativeMaintenanceGate.MarkerPath);
        if(marker.Length!=33)throw new InvalidDataException("Maintenance marker size refused.");
        Span<byte> bytes=stackalloc byte[33];marker.ReadExactly(bytes);CheckMarkerBytes(bytes,expected);
    }
    internal static JsonElement SafeStatus(JsonElement source)
    {
        string phase=source.GetProperty("phase").GetString()??throw new InvalidDataException("Missing native phase.");
        bool desired=source.GetProperty("desired").GetBoolean();
        if(desired)throw new InvalidDataException("Maintenance session is not disconnected.");
        return JsonSerializer.SerializeToElement(new{phase,desired,operation=source.GetProperty("operation").GetString(),generation=source.GetProperty("generation").GetUInt64(),errorCode=source.GetProperty("errorCode").GetString()});
    }
}
