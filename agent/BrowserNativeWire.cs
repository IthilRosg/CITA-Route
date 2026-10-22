using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoutePilot.Agent;

internal sealed class BrowserNativeEvent
{
    public string Id{get;set;}="";
    public string Kind{get;set;}="";
    public string Domain{get;set;}="";
    public string Method{get;set;}="";
    public string Resource{get;set;}="";
    public string Error{get;set;}="";
    public bool Cancelled{get;set;}
}
internal sealed class BrowserNativeBatch
{
    public string Id{get;set;}="";
    public BrowserNativeEvent[] Events{get;set;}=[];
}
internal sealed record BrowserNativeEntry(string Id,string Status,string? Domain=null);
internal sealed record BrowserNativeReply(string Id,BrowserNativeEntry[] Entries);

internal static class BrowserNativeWire
{
    internal const int Limit=16384;
    internal const string Origin="chrome-extension://gokcbalkmlaaakcdnlflpknihmpaekga/";
    internal const string Host="xyz.citaeris.route.feedback";
    internal static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=8};
    internal static BrowserNativeBatch Parse(byte[] data)
    {
        if(data.Length>Limit)throw new InvalidDataException("Browser message exceeds limit.");
        var batch=JsonSerializer.Deserialize<BrowserNativeBatch>(data,Json)??throw new InvalidDataException("Missing browser batch.");
        if(!Guid.TryParseExact(batch.Id,"N",out _)||batch.Events==null||batch.Events.Length is <1 or >32)throw new InvalidDataException("Invalid browser batch.");
        foreach(var item in batch.Events)
        {
            if(item==null||!Guid.TryParseExact(item.Id,"N",out _)||item.Kind is not ("begin" or "fail" or "finish" or "poll")||item.Error==null||item.Error.Length>96||item.Domain==null||item.Domain.Length>253||item.Method==null||item.Resource==null)throw new InvalidDataException("Invalid browser event.");
            if(item.Kind=="begin")
            {
                if(PolicySafety.Domain(item.Domain)!=item.Domain||PolicySafety.Pinned(item.Domain)||item.Method!="GET"||item.Resource is not ("Image" or "Media" or "Fetch" or "XHR")||item.Error!="")throw new InvalidDataException("Unsupported browser resource.");
            }
            else if(item.Domain!=""||item.Method!=""||item.Resource!="")throw new InvalidDataException("Only resource identity is permitted after begin.");
            if(item.Kind!="fail"&&(item.Cancelled||item.Error!=""))throw new InvalidDataException("Unexpected browser failure fields.");
        }
        return batch;
    }
    internal static async Task<byte[]?> Read(Stream stream,CancellationToken stop)
    {
        byte[] header=new byte[4];if(await stream.ReadAsync(header.AsMemory(0,1),stop)==0)return null;
        using var frame=CancellationTokenSource.CreateLinkedTokenSource(stop);frame.CancelAfter(2000);
        await stream.ReadExactlyAsync(header.AsMemory(1),frame.Token);
        uint length=BinaryPrimitives.ReadUInt32LittleEndian(header);if(length is 0 or >Limit)throw new InvalidDataException("Browser frame exceeds limit.");
        byte[] data=new byte[length];await stream.ReadExactlyAsync(data,frame.Token);return data;
    }
    internal static async Task Write(Stream stream,byte[] data,CancellationToken stop)
    {
        if(data.Length is 0 or >Limit)throw new InvalidDataException("Browser reply exceeds limit.");
        using var frame=CancellationTokenSource.CreateLinkedTokenSource(stop);frame.CancelAfter(2000);
        byte[] header=new byte[4];BinaryPrimitives.WriteUInt32LittleEndian(header,(uint)data.Length);
        await stream.WriteAsync(header,frame.Token);await stream.WriteAsync(data,frame.Token);await stream.FlushAsync(frame.Token);
    }
}
