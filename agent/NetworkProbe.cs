using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RoutePilot.Agent;

public sealed record EgressHealth(bool Verified,string Code,string Message)
{
    // Kept internal: health IPC and diagnostic exports must not expose the exit.
    internal string? VerifiedExit{get;init;}
}

public static class NetworkProbe
{
    public static async Task<(bool Verified,string Message)> CheckEgress(CancellationToken cancellation=default)
    {
        var result=await CheckEgressDetailed(cancellation);return(result.Verified,result.Message);
    }
    public static async Task<EgressHealth> CheckEgressDetailed(CancellationToken cancellation=default)
    {
        try
        {
            string config=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RoutePilot","Agent","agent-config.json");
            string reference=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RoutePilot","egress-reference.json");
            return await EgressReference.Verify(config,reference,image=>ReadWireSockStatus(image,cancellation),async()=>(await Trace("socks5://127.0.0.1:20808",cancellation:cancellation)).Ip,cancellation);
        }
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested){throw;}
        catch(Exception){return new(false,"InvalidConfiguration","Проверь настройку выбранного подключения.");}
    }
    internal static async Task<(bool Verified,string Message)> VerifyConfiguredEgress(System.Text.Json.JsonElement values,Func<string,Task<string>> readStatus,Func<Task<IPAddress>> trace)
    {
        var result=await VerifyConfiguredEgressDetailed(values,readStatus,trace);return(result.Verified,result.Message);
    }
    internal static async Task<EgressHealth> VerifyConfiguredEgressDetailed(System.Text.Json.JsonElement values,Func<string,Task<string>> readStatus,Func<Task<IPAddress>> trace,CancellationToken cancellation=default,string? pinnedEgress=null)
    {
            string expected;
            string? cli=null;
            if(values.TryGetProperty("ExpectedEgressIp",out var configured))
            {
                expected=configured.GetString()??"";
                cli=values.GetProperty("WireSockCliPath").GetString();
                string status;
                try{status=await readStatus(cli!);}catch(OperationCanceledException)when(cancellation.IsCancellationRequested){throw;}catch(Exception){return new(false,"StatusUnavailable","Не удалось прочитать состояние WireSock. Проверка будет повторена.");}
                if(!ManagedProfileConnected(status))return new(false,"ProfileNotConnected","Выбранный профиль CITA Route в WireSock не подключён.");
                if(string.IsNullOrEmpty(expected))expected=pinnedEgress??ReportedEgress(status)??"";
                if(expected.Length==0)return new(false,"MissingExpectedEgress","WireSock не сообщил внешний IPv4. Укажи IP выхода VPN в настройках подключения.");
            }
            else expected=values.GetProperty("EndpointIp").GetString()!; // Existing protected installations.
            IPAddress vpn;
            try{vpn=await trace();}catch(OperationCanceledException)when(cancellation.IsCancellationRequested){throw;}catch(Exception){return new(false,"ControlHttpsUnavailable","Контрольная проверка HTTPS временно не прошла.");}
            if(cli!=null)
            {
                string status;
                try{status=await readStatus(cli);}catch(OperationCanceledException)when(cancellation.IsCancellationRequested){throw;}catch(Exception){return new(false,"StatusUnavailable","Состояние WireSock после проверки не подтверждено.");}
                if(!ManagedProfileConnected(status))return new(false,"ProfileChanged","Профиль WireSock сменился во время проверки.");
                if(pinnedEgress==null && string.IsNullOrEmpty(values.GetProperty("ExpectedEgressIp").GetString()) && ReportedEgress(status)!=expected)
                    return new(false,"ProfileChanged","Выход WireSock изменился во время проверки; повторяем проверку.");
            }
            // A VPN can use a separate NAT exit. Compare with the selected client's
            // reported exit (or an explicit protected assertion), never its UDP endpoint.
            return vpn.ToString()==expected
                ?new EgressHealth(true,"Verified","HTTPS/IPv4 через выбранный выход WireSock подтверждён."){VerifiedExit=vpn.ToString()}
                :new(false,"EgressMismatch","IP выхода отличается от выбранного профиля. Проверь настройки подключения.");
    }
    public static bool ManagedProfileConnected(string status)
    {
        // Multiline $ stops before LF, leaving CR in real Windows CLI replies.
        var names=System.Text.RegularExpressions.Regex.Matches(status,@"(?im)^.*?\b(?:profile|профиля)[ \t]+([^\r\n]+?)[ \t]*\r?$");
        return names.Count==1 && string.Equals(names[0].Groups[1].Value.Trim(),"SmartRouting-AWG2",StringComparison.OrdinalIgnoreCase) &&
            !System.Text.RegularExpressions.Regex.IsMatch(status,@"\b(?:Not connected|NotConnected|No connection|Disconnected|Disconnecting|Connecting|Establishing connection|Не\s+подключено|Отключено|Отключение|Подключение|Устанавливается соединение)\b",System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
            System.Text.RegularExpressions.Regex.IsMatch(status,@"Connected to|Подключено к серверу",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
    public static string? ReportedEgress(string status)
    {
        var match=System.Text.RegularExpressions.Regex.Match(status,@"(?im)^\s*(?:Внешний (?:адрес|IP)|External (?:IP(?: address)?|address))\s*:\s*(\d{1,3}(?:\.\d{1,3}){3})(?:[,\s]|$)");
        return match.Success && IPAddress.TryParse(match.Groups[1].Value,out var ip) && ip.AddressFamily==AddressFamily.InterNetwork?ip.ToString():null;
    }
    static async Task<string> ReadWireSockStatus(string image,CancellationToken cancellation)
    {
        var info=new ProcessStartInfo(image){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add("status");
        using var process=Process.Start(info)??throw new IOException("Не удалось проверить WireSock.");
        using var output=new MemoryStream();using var error=new MemoryStream();
        var stdout=process.StandardOutput.BaseStream.CopyToAsync(output);var stderr=process.StandardError.BaseStream.CopyToAsync(error);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try{await process.WaitForExitAsync(timeout.Token);}catch{try{process.Kill(true);}catch{}throw;}
        await Task.WhenAll(stdout,stderr).WaitAsync(timeout.Token);
        if(process.ExitCode!=0)throw new IOException("WireSock status недоступен.");
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        try{return new System.Text.UTF8Encoding(false,true).GetString(output.ToArray());}
        catch(System.Text.DecoderFallbackException){return System.Text.Encoding.GetEncoding(866).GetString(output.ToArray());}
    }
    public static NetworkInterface? Vpnus => NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n=>n.Name.Equals("VPNUS",StringComparison.OrdinalIgnoreCase) && n.OperationalStatus==OperationalStatus.Up);
    public static bool AutoConnectEnabled(string userProfile)
    {
        string path=Path.Combine(userProfile,"AppData","Roaming","VPNUS","VPNUS","shared_preferences.json");
        using var document=System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("flutter.auto_connect",out var value) && value.ValueKind==System.Text.Json.JsonValueKind.True;
    }
    public static async Task<(IPAddress Ip,long Milliseconds)> Trace(string? proxy=null,NetworkInterface? bind=null,CancellationToken cancellation=default,string[]? urls=null)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var handler=new SocketsHttpHandler{UseProxy=proxy!=null,Proxy=proxy==null?null:new WebProxy(proxy),ConnectTimeout=TimeSpan.FromSeconds(5),AllowAutoRedirect=false};
        if(bind!=null)
        {
            int index=bind.GetIPProperties().GetIPv4Properties().Index;
            var source=bind.GetIPProperties().UnicastAddresses.First(a=>a.Address.AddressFamily==AddressFamily.InterNetwork).Address;
            handler.ConnectCallback=async(context,token)=>
            {
                var addresses=await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host,token);Exception? last=null;
                foreach(var address in addresses.Where(a=>a.AddressFamily==AddressFamily.InterNetwork))
                {
                    var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
                    try
                    {
                        socket.Bind(new IPEndPoint(source,0));
                        // Windows IP_UNICAST_IF = 31; interface index is supplied in network byte order.
                        socket.SetSocketOption(SocketOptionLevel.IP,(SocketOptionName)31,IPAddress.HostToNetworkOrder(index));
                        await socket.ConnectAsync(address,context.DnsEndPoint.Port,token);return new NetworkStream(socket,true);
                    }
                    catch(Exception ex){socket.Dispose();last=ex;}
                }
                throw new IOException("No IPv4 connection through the selected interface.",last);
            };
        }
        using var client=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
        var timer=Stopwatch.StartNew();
        Exception? failure=null;
        foreach(string url in urls??new[]{"https://www.cloudflare.com/cdn-cgi/trace","https://api.ipify.org"})
        {
            using var attempt=CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);attempt.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                using var response=await client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,attempt.Token);response.EnsureSuccessStatusCode();
                using var stream=await response.Content.ReadAsStreamAsync(attempt.Token);byte[] bytes=new byte[8193];int used=0,read;
                while(used<bytes.Length && (read=await stream.ReadAsync(bytes.AsMemory(used),attempt.Token))>0)used+=read;
                if(used==bytes.Length)throw new InvalidDataException("Unexpected trace response size.");
                string text=System.Text.Encoding.UTF8.GetString(bytes,0,used);
                string? value=url.Contains("ipify")?text.Trim():text.Split('\n').FirstOrDefault(line=>line.StartsWith("ip=",StringComparison.Ordinal))?[3..].Trim();
                if(!IPAddress.TryParse(value,out var ip) || ip.AddressFamily!=AddressFamily.InterNetwork)throw new InvalidDataException("Trace did not return a comparable IPv4 source.");
                return(ip,timer.ElapsedMilliseconds);
            }
            catch(Exception ex)when(ex is HttpRequestException or IOException or OperationCanceledException){failure=ex;if(timeout.IsCancellationRequested)break;}
        }
        throw new IOException("The control HTTPS probes failed.",failure);
    }
}
