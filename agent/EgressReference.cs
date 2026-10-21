using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RoutePilot.Agent;

// A previously verified exit is an assertion for one protected configuration,
// not a new candidate to learn from a failed probe or changing CLI metadata.
internal static class EgressReference
{
    sealed record Reference(int Schema,string Scope,string Exit,DateTime VerifiedUtc);
    static readonly JsonSerializerOptions Json=new(){PropertyNameCaseInsensitive=true};
    static void Regular(string path)
    {
        for(string? current=Path.GetFullPath(path);current!=null;current=Path.GetDirectoryName(current))
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("Linked egress reference paths are not allowed.");
    }
    static byte[] Read(string path)
    {
        Regular(path);
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(stream.Length>16384)throw new IOException("Egress configuration/reference exceeds its limit.");
        byte[] bytes=new byte[(int)stream.Length];stream.ReadExactly(bytes);return bytes;
    }
    static string Scope(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    static string? Load(string path,string scope)
    {
        byte[] bytes;
        try{bytes=Read(path);}catch(FileNotFoundException){return null;}catch(DirectoryNotFoundException){return null;}
        var value=JsonSerializer.Deserialize<Reference>(bytes,Json)??throw new IOException("Empty egress reference.");
        if(value.Schema!=1||value.Scope==null||value.Scope.Length!=64||!System.Text.RegularExpressions.Regex.IsMatch(value.Scope,"\\A[A-F0-9]{64}\\z"))
            throw new IOException("Invalid egress reference schema.");
        if(value.Scope!=scope)return null;
        if(!IPAddress.TryParse(value.Exit,out var ip)||ip.AddressFamily!=AddressFamily.InterNetwork)
            throw new IOException("Invalid egress reference address.");
        return ip.ToString();
    }
    internal static async Task<EgressHealth> Verify(string configPath,string referencePath,Func<string,Task<string>> status,Func<Task<IPAddress>> trace,CancellationToken cancellation=default)
    {
        byte[] bytes=Read(configPath);string scope=Scope(bytes);
        using var config=JsonDocument.Parse(bytes);
        bool automatic=config.RootElement.TryGetProperty("ExpectedEgressIp",out var expected)&&string.IsNullOrEmpty(expected.GetString());
        string? pinned=automatic?Load(referencePath,scope):null;
        var result=await NetworkProbe.VerifyConfiguredEgressDetailed(config.RootElement,status,trace,cancellation,pinned);
        cancellation.ThrowIfCancellationRequested();
        if(Scope(Read(configPath))!=scope)return new(false,"ProfileChanged","Настройка подключения изменилась во время проверки.");
        if(!result.Verified||!automatic||pinned!=null)return result;
        // Serialize first confirmation across concurrent service/UI probes. Never
        // overwrite a same-configuration assertion with a different observed exit.
        Regular(referencePath);Regular(referencePath+".lock");
        FileStream? acquired=null;
        for(int attempt=0;acquired==null;attempt++)
        {
            try{acquired=new FileStream(referencePath+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException)when(attempt<10){await Task.Delay(20,cancellation);}
        }
        using var lease=acquired;
        if(Scope(Read(configPath))!=scope)return new(false,"ProfileChanged","Настройка подключения изменилась во время проверки.");
        string? current=Load(referencePath,scope);
        if(current!=null)return current==result.VerifiedExit?result:new(false,"EgressMismatch","Выход VPN отличается от ранее подтверждённого для этого профиля.");
        cancellation.ThrowIfCancellationRequested();
        string temporary=referencePath+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                byte[] json=JsonSerializer.SerializeToUtf8Bytes(new Reference(1,scope,result.VerifiedExit!,DateTime.UtcNow));
                file.Write(json);file.Flush(true);
            }
            File.Move(temporary,referencePath,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
        return result;
    }
}
