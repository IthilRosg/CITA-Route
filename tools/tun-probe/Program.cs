using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

// Guest-only acceptance client: ordinary Windows DNS and native TCP/TLS.
// No SOCKS, HTTP proxy, certificate callback or routing-controller dependency.
internal static class Program
{
    const string Domain="www.cloudflare.com";
    static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    static void Write(string path,object value)
    {
        string temp=path+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(value,Json));
        File.Move(temp,path,true);
    }
    static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    static bool Fake(IPAddress address)=>address.AddressFamily==AddressFamily.InterNetwork&&address.GetAddressBytes()[0]==198&&(address.GetAddressBytes()[1]&254)==18;
    static async Task<int> Main(string[] args)
    {
        string? prefix=null;
        try
        {
            using var bios=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if((bios?.GetValue("SystemManufacturer") as string)?.Contains("VMware",StringComparison.OrdinalIgnoreCase)!=true)
                throw new IOException("This acceptance client runs only in the authorized VMware lab.");
            if(args.Length!=2||args[0] is not ("--request" or "--direct" or "--hold" or "--resolve"))throw new ArgumentException("Expected a lab mode and report prefix.");
            prefix=Path.GetFullPath(args[1]);
            if(!prefix.StartsWith(@"C:\CitaLab\TunProbe\",StringComparison.OrdinalIgnoreCase))throw new IOException("Unexpected laboratory output directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
            using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(150));
            int connections=0,localPort=0;bool remoteFake=false;
            using var handler=new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(6),MaxConnectionsPerServer=1,
                ConnectCallback=async(context,stop)=>
                {
                    // A hold test must fail rather than conceal a reconnect.
                    if(args[0]=="--hold"&&Volatile.Read(ref connections)>0)throw new IOException("The original TCP connection was lost.");
                    var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(context.DnsEndPoint,stop);
                        Interlocked.Increment(ref connections);localPort=((IPEndPoint)socket.LocalEndPoint!).Port;
                        remoteFake=Fake(((IPEndPoint)socket.RemoteEndPoint!).Address);
                        return new NetworkStream(socket,true);
                    }
                    catch{socket.Dispose();throw;}
                }};
            using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(10),DefaultRequestVersion=HttpVersion.Version11,DefaultVersionPolicy=HttpVersionPolicy.RequestVersionExact};
            async Task<object> Sample()
            {
                var clock=Stopwatch.StartNew();
                string target=args[0]=="--direct"?"1.1.1.1":Domain;
                using var request=new HttpRequestMessage(HttpMethod.Get,"https://"+target+"/cdn-cgi/trace");
                request.Headers.CacheControl=new CacheControlHeaderValue{NoCache=true};
                using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,budget.Token);
                response.EnsureSuccessStatusCode();
                await using var input=await response.Content.ReadAsStreamAsync(budget.Token);using var body=new MemoryStream();byte[] bytes=new byte[2048];int size;
                while((size=await input.ReadAsync(bytes,budget.Token))>0){if(body.Length+size>16384)throw new IOException("Oversized public fixture response.");body.Write(bytes,0,size);}
                string? value=Encoding.UTF8.GetString(body.ToArray()).Split('\n').FirstOrDefault(line=>line.StartsWith("ip="))?[3..].Trim();
                if(!IPAddress.TryParse(value,out var address))throw new IOException("The authenticated response contains no egress proof.");
                return new{success=true,checkedUtc=DateTime.UtcNow,domain=target,httpStatus=(int)response.StatusCode,tlsAuthenticated=true,
                    egressHash=Hash(address.ToString()),tcpConnections=connections,localPort,remoteIsFakeIp=remoteFake,milliseconds=clock.ElapsedMilliseconds,
                    explicitProxy=false,ipFamily="IPv4"};
            }
            if(args[0]=="--resolve")
            {
                using var request=new HttpRequestMessage(HttpMethod.Get,"https://1.1.1.1/dns-query?type=A&name="+Domain);request.Headers.Accept.ParseAdd("application/dns-json");
                using var response=await client.SendAsync(request,budget.Token);response.EnsureSuccessStatusCode();
                byte[] body=await response.Content.ReadAsByteArrayAsync(budget.Token);if(body.Length>32768)throw new IOException("Oversized DNS fixture response.");
                using var document=JsonDocument.Parse(body);if(document.RootElement.GetProperty("Status").GetInt32()!=0)throw new IOException("Independent DNS fixture is unavailable.");
                var addresses=document.RootElement.GetProperty("Answer").EnumerateArray().Where(a=>a.GetProperty("type").GetInt32()==1)
                    .Select(a=>IPAddress.Parse(a.GetProperty("data").GetString()!)).Distinct().ToArray();
                if(addresses.Length==0||addresses.Any(a=>a.AddressFamily!=AddressFamily.InterNetwork||Fake(a)||IPAddress.IsLoopback(a)))throw new IOException("Unexpected fixture addresses.");
                // Public fixture IPs stay in the guest; wrappers export only a count.
                Write(prefix+".ready.json",new{success=true,addresses=addresses.Select(a=>a.ToString())});return 0;
            }
            Write(prefix+".ready.json",await Sample());
            if(args[0] is "--request" or "--direct")return 0;
            int last=0;
            while(!budget.IsCancellationRequested)
            {
                await Task.Delay(100,budget.Token);string file=prefix+".command.json";if(!File.Exists(file))continue;
                using var command=JsonDocument.Parse(File.ReadAllText(file));int id=command.RootElement.GetProperty("id").GetInt32();
                if(id<=last||id>20)continue;last=id;
                string action=command.RootElement.GetProperty("action").GetString()!;
                if(action=="exit")return 0;
                if(action!="sample")throw new IOException("Unexpected hold command.");
                Write(prefix+"."+id+".json",await Sample());
            }
            throw new IOException("The bounded hold fixture expired.");
        }
        catch(Exception error)
        {
            // No URI, IP, header or framework exception text in exported errors.
            if(prefix!=null&&prefix.StartsWith(@"C:\CitaLab\TunProbe\",StringComparison.OrdinalIgnoreCase))
            {
                var http=error as HttpRequestException;
                string stage=error is OperationCanceledException?"Timeout":http?.StatusCode!=null?"HTTP":http?.HttpRequestError switch
                {
                    HttpRequestError.NameResolutionError=>"DNS",HttpRequestError.SecureConnectionError=>"TLS",HttpRequestError.ConnectionError=>"TCP",_=>"TransportOrFixture"
                };
                Write(prefix+".failed.json",new{success=false,checkedUtc=DateTime.UtcNow,stage,errorType=error.GetType().Name,httpStatus=http?.StatusCode is HttpStatusCode status?(int?)status:null});
            }
            return 1;
        }
    }
}
