using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RoutePilot.Agent;

// Durable user intent deliberately excludes DNS answers and probe TTLs: those
// are ephemeral evidence and must be rebuilt for the current network/session.
public sealed record NativeForcedIntentRule(string Domain,string Application,string Network,ushort Port,string Route);
internal sealed record NativeForcedIntentDocument(int Schema,ulong Revision,NativeForcedIntentRule[] Rules);

internal static class NativeForcedIntent
{
    const int MaxBytes=65536;
    static readonly JsonSerializerOptions Options=new(){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=6};
    internal static NativeForcedIntentDocument Parse(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length is <1 or >MaxBytes||bytes.StartsWith(new byte[]{0xef,0xbb,0xbf}))throw new InvalidDataException("Forced intent document size or encoding refused.");
        using(var json=JsonDocument.Parse(bytes.ToArray(),new JsonDocumentOptions{MaxDepth=6}))RejectDuplicateFields(json.RootElement);
        var document=JsonSerializer.Deserialize<NativeForcedIntentDocument>(bytes,Options)??throw new InvalidDataException("Forced intent missing.");
        return Validate(document);
    }
    static void RejectDuplicateFields(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var property in element.EnumerateObject()){if(!names.Add(property.Name))throw new InvalidDataException("Duplicate forced intent field.");RejectDuplicateFields(property.Value);}
        }
        else if(element.ValueKind==JsonValueKind.Array)foreach(var item in element.EnumerateArray())RejectDuplicateFields(item);
    }
    internal static byte[] Encode(NativeForcedIntentDocument document)
    {
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(Validate(document),Options);
        if(bytes.Length>MaxBytes)throw new InvalidDataException("Forced intent document budget exceeded.");
        return bytes;
    }
    internal static NativeForcedIntentDocument Validate(NativeForcedIntentDocument document)
    {
        if(document.Schema!=1||document.Revision==0||document.Rules is null||document.Rules.Length>128)throw new InvalidDataException("Forced intent revision or budget refused.");
        var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var result=new NativeForcedIntentRule[document.Rules.Length];
        for(int i=0;i<result.Length;i++)
        {
            var r=document.Rules[i]??throw new InvalidDataException("Forced intent rule missing.");
            if(r.Domain is null||r.Application is null||r.Network is null||r.Route is null)throw new InvalidDataException("Forced intent fields missing.");
            if(r.Domain.EndsWith("..",StringComparison.Ordinal))throw new InvalidDataException("Forced intent domain refused.");
            string domain=r.Domain.TrimEnd('.').ToLowerInvariant();string app=r.Application.ToLowerInvariant();
            if(domain.Length is <1 or >253||IPAddress.TryParse(domain,out _)||domain.Split('.').Any(label=>!Regex.IsMatch(label,@"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z")))throw new InvalidDataException("Forced intent domain refused.");
            if(app.Length>512||app.Any(c=>c<32||c==127)||app.Length>0&&(!Regex.IsMatch(app,@"\A[a-z]:\\")||!string.Equals(Path.GetFullPath(app),app,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Forced intent application refused.");
            if(r.Port==0||r.Network is not ("tcp" or "udp")||r.Route is not ("direct" or "vpn" or "zapret" or "blocked")||r.Route=="zapret"&&(r.Network!="tcp"||r.Port!=443||app.Length==0))throw new InvalidDataException("Forced intent route refused.");
            if(!keys.Add(domain+"\0"+app+"\0"+r.Network+"\0"+r.Port))throw new InvalidDataException("Duplicate forced intent scope.");
            result[i]=r with{Domain=domain,Application=app};
        }
        return document with{Rules=result};
    }
}
