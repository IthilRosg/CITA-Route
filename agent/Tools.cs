using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RoutePilot.Agent;

public static class Tools
{
    public static string VpnusExe=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"VPNUS","vpnus.exe");
    public static string WireSockCli
    {
        get
        {
            var config=Storage.Read<AgentConfig>(Path.Combine(AppContext.BaseDirectory,"agent-config.json"));
            return string.IsNullOrEmpty(config.WireSockCliPath)?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"WireSock Secure Connect","command-line","wiresock-connect-cli.exe"):config.WireSockCliPath;
        }
    }
    public static string Self=>Path.Combine(AppContext.BaseDirectory,"RoutePilot.Agent.exe");
    public static string Bridge=>Path.Combine(AppContext.BaseDirectory,"vpn-bridge.exe");
    public static string Router=>Path.Combine(AppContext.BaseDirectory,"smart-router.exe");
    public static string RunFolder(AgentConfig config,string id)
    {if(!Guid.TryParseExact(id,"N",out _))throw new InvalidDataException("Invalid run ID.");return Path.Combine(config.DataDirectory,"runs",id);}
    public static string Profile(string id)
    {if(!Guid.TryParseExact(id,"N",out _))throw new InvalidDataException("Invalid run ID.");return "RoutePilot-Probe-"+id[..12];}
    public static async Task<(int Code,string Text)> Run(string image,IEnumerable<string> args,int seconds=8,CancellationToken cancellation=default)
    {
        var info=new ProcessStartInfo(image){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in args)info.ArgumentList.Add(argument);
        using var process=Process.Start(info)??throw new IOException("Could not start command.");
        Task<byte[]> Read(Stream input)=>Task.Run(async()=>{using var bytes=new MemoryStream();await input.CopyToAsync(bytes);return bytes.ToArray();});
        var output=Read(process.StandardOutput.BaseStream);var error=Read(process.StandardError.BaseStream);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try{await process.WaitForExitAsync(timeout.Token);}
        catch{try{process.Kill(true);await process.WaitForExitAsync();}catch{}throw;}
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string Decode(byte[] bytes){try{return new UTF8Encoding(false,true).GetString(bytes);}catch(DecoderFallbackException){return Encoding.GetEncoding(866).GetString(bytes);}}
        string text=Decode(await output);string errorText=Decode(await error);
        if(process.ExitCode!=0)text+="\n"+errorText;
        return(process.ExitCode,System.Text.RegularExpressions.Regex.Replace(text,@"[A-Za-z0-9+/]{43}=","[redacted]").Trim());
    }
    public static async Task<string> PowerShell(string command,int seconds=10,CancellationToken cancellation=default)
    {
        string image=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe");
        var result=await Run(image,new[]{"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; "+command))},seconds,cancellation);
        if(result.Code!=0)throw new IOException("Network command failed: "+result.Text);
        return result.Text;
    }
    public static void Kill(ProcessIdentity identity)
    {
        if(!identity.Matches())return;
        using var process=Process.GetProcessById(identity.Pid);process.Kill(true);process.WaitForExit(3000);
        if(identity.Matches())throw new IOException("The owned process did not terminate.");
    }
    public static void StopVpnusGui(int sessionId)
    {
        foreach(var process in Process.GetProcessesByName("vpnus"))
        using(process)
        {
            if(process.SessionId!=sessionId)continue;
            var identity=ProcessIdentity.From(process);
            if(string.Equals(identity.Path,VpnusExe,StringComparison.OrdinalIgnoreCase))Kill(identity);
        }
    }
    public static NetworkInterface Physical(int index)=>NetworkInterface.GetAllNetworkInterfaces().First(n=>n.Supports(NetworkInterfaceComponent.IPv4) && n.GetIPProperties().GetIPv4Properties()?.Index==index);
    public static string NetworkSignature(int index)
    {
        var nic=Physical(index);var ip=nic.GetIPProperties();
        string value=nic.Id+"|"+nic.OperationalStatus+"|"+string.Join(",",ip.UnicastAddresses.Select(a=>a.Address))+"|"+string.Join(",",ip.GatewayAddresses.Select(a=>a.Address))+"|"+string.Join(",",ip.DnsAddresses);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
    public static async Task<OwnedRoute> PrepareEndpoint(AgentConfig config,int metric,CancellationToken cancellation=default)
    {
        if(!IPAddress.TryParse(config.EndpointIp,out var endpoint) || endpoint.AddressFamily!=AddressFamily.InterNetwork || IPAddress.IsLoopback(endpoint))throw new InvalidDataException("Invalid fixed VPN endpoint.");
        string text=await PowerShell("$p=Get-NetIPConfiguration | Where-Object {$_.NetAdapter.HardwareInterface -and $_.NetAdapter.Status -eq 'Up' -and $_.IPv4DefaultGateway} | Sort-Object {$_.NetIPv4Interface.InterfaceMetric} | Select-Object -First 1; if(-not $p){throw 'No physical gateway'}; @{Index=$p.InterfaceIndex;Gateway=$p.IPv4DefaultGateway.NextHop}|ConvertTo-Json -Compress",cancellation:cancellation);
        using var json=JsonDocument.Parse(text);int index=json.RootElement.GetProperty("Index").GetInt32();string gateway=json.RootElement.GetProperty("Gateway").GetString()!;
        if(!IPAddress.TryParse(gateway,out var hop) || hop.AddressFamily!=AddressFamily.InterNetwork)throw new InvalidDataException("Invalid physical gateway.");
        var existing=await PowerShell($"@(Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{endpoint}/32' -ErrorAction SilentlyContinue).Count",cancellation:cancellation);
        if(existing.Trim()!="0")throw new InvalidOperationException("An endpoint route already exists; it will not be replaced.");
        return new(endpoint+"/32",index,hop.ToString(),metric);
    }
    public static Task AddRoute(OwnedRoute route,CancellationToken cancellation=default)=>PowerShell($"New-NetRoute -DestinationPrefix '{route.Prefix}' -InterfaceIndex {route.InterfaceIndex} -NextHop '{route.Gateway}' -RouteMetric {route.Metric} -PolicyStore ActiveStore | Out-Null",cancellation:cancellation);
    public static Task RemoveRoute(OwnedRoute route,CancellationToken cancellation=default)=>PowerShell($"Get-NetRoute -DestinationPrefix '{route.Prefix}' -InterfaceIndex {route.InterfaceIndex} -NextHop '{route.Gateway}' -ErrorAction SilentlyContinue | Where-Object RouteMetric -eq {route.Metric} | Remove-NetRoute -Confirm:$false -ErrorAction Stop",10,cancellation);
}
