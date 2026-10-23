using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace RoutePilot.Agent;

internal static class BrowserNativeTests
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    sealed class Fragmented(byte[] data):MemoryStream(data)
    {public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken stop=default)=>base.ReadAsync(buffer[..Math.Min(1,buffer.Length)],stop);}
    internal static async Task Run()
    {
        string id=Guid.NewGuid().ToString("N");
        var valid=new BrowserNativeBatch{Id=id,Events=[new(){Id=id,Kind="begin",Domain="cdn.example.com",Method="GET",Resource="Image"}]};
        byte[] payload=JsonSerializer.SerializeToUtf8Bytes(valid,BrowserNativeWire.Json);
        Assert(BrowserNativeWire.Parse(payload).Events[0].Domain=="cdn.example.com","The channel accepts only the typed resource envelope.");
        var dependency=new BrowserNativeBatch{Id=id,Events=[new(){Id=id,Kind="begin",Domain="cdn.example.com",Method="GET",Resource="Image",ParentDomain="page.example.com",ContextId=id}]};
        Assert(BrowserNativeWire.Parse(JsonSerializer.SerializeToUtf8Bytes(dependency,BrowserNativeWire.Json)).Events[0].ParentDomain=="page.example.com","A dependency carries only a domain and opaque document context.");
        foreach(string parent in new[]{"https://page.example.com/a?token=secret","chatgpt.com","host.ts.net",""})
        {dependency.Events[0].ParentDomain=parent;bool refused=false;try{BrowserNativeWire.Parse(JsonSerializer.SerializeToUtf8Bytes(dependency,BrowserNativeWire.Json));}catch(InvalidDataException){refused=true;}Assert(refused,"Invalid or half-specified dependency envelopes are refused.");}
        foreach(string resource in new[]{"Document","Script","Stylesheet","Font"})
        {var b=new BrowserNativeBatch{Id=id,Events=[new(){Id=id,Kind="begin",Domain="cdn.example.com",Method="GET",Resource=resource},new(){Id=id,Kind="complete"}]};Assert(BrowserNativeWire.Parse(JsonSerializer.SerializeToUtf8Bytes(b,BrowserNativeWire.Json)).Events.Length==2,"Extended resource completion crosses the typed envelope without URLs/status/headers.");}
        foreach(string invalid in new[]{Encoding.UTF8.GetString(payload).Replace("GET","POST"),Encoding.UTF8.GetString(payload).Replace("cdn.example.com","https://cdn.example.com/a?token=private"),Encoding.UTF8.GetString(payload).Replace("\"domain\"","\"url\""),Encoding.UTF8.GetString(payload).Replace("\"GET\"","null"),"{\"id\":\""+id+"\",\"events\":[]}"})
        {bool refused=false;try{BrowserNativeWire.Parse(Encoding.UTF8.GetBytes(invalid));}catch(Exception ex)when(ex is JsonException or IOException or InvalidDataException){refused=true;}Assert(refused,"POST, URLs, unknown/private fields and malformed envelopes must be refused.");}
        valid.Events=Enumerable.Repeat(valid.Events[0],33).ToArray();bool bounded=false;try{BrowserNativeWire.Parse(JsonSerializer.SerializeToUtf8Bytes(valid,BrowserNativeWire.Json));}catch(InvalidDataException){bounded=true;}Assert(bounded,"Native batches must remain bounded.");
        using var stream=new MemoryStream();await BrowserNativeWire.Write(stream,payload,CancellationToken.None);await BrowserNativeWire.Write(stream,payload,CancellationToken.None);
        using var fragmented=new Fragmented(stream.ToArray());
        Assert((await BrowserNativeWire.Read(fragmented,CancellationToken.None))!.SequenceEqual(payload)&&(await BrowserNativeWire.Read(fragmented,CancellationToken.None))!.SequenceEqual(payload)&&await BrowserNativeWire.Read(fragmented,CancellationToken.None)==null,"Fragmented binary reads preserve message boundaries and EOF.");
        byte[] oversized=new byte[4];BinaryPrimitives.WriteUInt32LittleEndian(oversized,BrowserNativeWire.Limit+1);
        foreach(byte[] broken in new[]{oversized,new byte[]{1,0},new byte[]{5,0,0,0,1}})
        {bool refused=false;try{await BrowserNativeWire.Read(new MemoryStream(broken),CancellationToken.None);}catch(Exception ex)when(ex is IOException or InvalidDataException){refused=true;}Assert(refused,"Oversized and truncated frames must not allocate/read unbounded data.");}
        bool originRejected=false;try{await BrowserNativeHost.Run(["chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/"]);}catch(IOException){originRejected=true;}
        Assert(originRejected,"An unapproved extension cannot enter native messaging.");
        Assert(BrowserNativeIdentity.Browser(Environment.ProcessId)==null,"A self-test launched outside Chrome/Edge cannot attest itself as a browser.");
        using(var caller=System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            var security=BrowserNativeBroker.UserSecurity();var grants=security.GetAccessRules(true,true,typeof(System.Security.Principal.SecurityIdentifier));
            Assert(security.AreAccessRulesProtected&&security.GetOwner(typeof(System.Security.Principal.SecurityIdentifier))?.Equals(caller.User)==true&&grants.Count==1,"The browser pipe must have one protected user-SID grant, not an Administrators/everyone grant.");
            var grant=grants[0] as System.IO.Pipes.PipeAccessRule??throw new IOException("Missing user pipe rule.");Assert(grant.IdentityReference.Equals(caller.User)&&grant.AccessControlType==System.Security.AccessControl.AccessControlType.Allow,"Only the actual user SID may connect.");
            using var pipe=BrowserNativeBroker.UserPipe("Cita.Browser.Test."+Guid.NewGuid().ToString("N"),1);
            Assert(BrowserNativeBroker.HasMediumLabel(pipe),"The real kernel pipe must have an explicit medium label; low integrity cannot write up.");
        }
        Console.WriteLine("PASS: native-browser framing, partial reads/EOF, unknown and private fields, bounds, POST refusal and untrusted origin/process rejection.");
    }
}
