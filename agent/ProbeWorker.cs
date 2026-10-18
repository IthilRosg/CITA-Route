using System.Diagnostics;
using System.IO.Pipes;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoutePilot.Agent;

public sealed class WorkerReport
{
    public string RunId{get;set;}="";
    public List<ProbeResult> Results{get;set;}=new();
}
public static class ProbeWorker
{
    static AgentConfig Config=>Storage.Read<AgentConfig>(Path.Combine(AppContext.BaseDirectory,"agent-config.json"));
    public static async Task Run(string[] args)
    {
        if(args.Length!=3 || !Enum.TryParse<TestKind>(args[2],out var kind))throw new ArgumentException("Invalid worker arguments.");
        var config=Config;string id=args[1],folder=Tools.RunFolder(config,id);
        var report=new WorkerReport{RunId=id};
        try
        {
            var lease=await Request("worker-hello",id);
            if(lease.State!.Kind!=kind)throw new InvalidOperationException("Mismatched worker kind.");
            if(kind==TestKind.DryRun)
            {
                using var held=File.Open(Path.Combine(folder,"worker-held.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
                File.WriteAllText(Path.Combine(folder,"worker-held.ready"),"ready");
                await Task.Delay(Timeout.Infinite);return;
            }
            var vpnus=NetworkProbe.Vpnus??throw new InvalidOperationException("VPNUS is not connected.");
            var before=await NetworkProbe.Trace(bind:vpnus);
            report.Results.Add(new("VpnusBaseline",true,"IPv4 HTTPS through VPNUS succeeded.",before.Milliseconds));
            if(kind==TestKind.VpnusRestart)
            {
                if(!NetworkProbe.AutoConnectEnabled(config.UserProfile))throw new InvalidOperationException("VPNUS auto-connect is disabled.");
                if(!Disconnected(await Cli("status")))throw new InvalidOperationException("WireSock is in use; VPNUS restart was not attempted.");
                await Request("vpnus-intent",id);
                await Request("restart-vpnus-service",id);
                Tools.StopVpnusGui(Process.GetCurrentProcess().SessionId);
                using var app=Process.Start(new ProcessStartInfo(Tools.VpnusExe){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(Tools.VpnusExe),WindowStyle=ProcessWindowStyle.Hidden})??throw new IOException("VPNUS did not launch.");
                await Task.Delay(2000);
                var timer=Stopwatch.StartNew();Exception? last=null;
                while(timer.Elapsed.TotalSeconds<35)
                {
                    try
                    {
                        var current=NetworkProbe.Vpnus??throw new IOException("VPNUS adapter is not ready.");
                        var after=await NetworkProbe.Trace(bind:current);
                        report.Results.Add(new("VpnusRestart",true,"Official VPNUS client reconnected using Connect on launch.",timer.ElapsedMilliseconds));return;
                    }
                    catch(Exception ex){last=ex;await Task.Delay(1000);}
                }
                throw new IOException("VPNUS did not reconnect within its test budget.",last);
            }
            await WireSock(config,id,report,before.Ip.ToString(),kind==TestKind.SystemRouting);
        }
        catch(Exception ex){report.Results.Add(new("Failure",false,ex.Message));}
        finally{Storage.Write(Path.Combine(folder,"result.json"),report);}
    }
    public static async Task<AgentReply> Request(string command,string id)
    {
        using var pipe=new NamedPipeClientStream(".",Program.PipeName,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));await pipe.ConnectAsync(timeout.Token);
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new AgentRequest(command,id))+"\n"),timeout.Token);await pipe.FlushAsync(timeout.Token);
        var reply=JsonSerializer.Deserialize<AgentReply>(await PipeMessages.Read(pipe,timeout.Token),Storage.Json)??throw new InvalidDataException("Missing service response.");
        if(!reply.Success)throw new InvalidOperationException(reply.Message);return reply;
    }
    static bool Disconnected(string status)=>Regex.IsMatch(status.Trim(),@"^(Нет подключения|Not connected|No connection)$",RegexOptions.IgnoreCase);
    static async Task<string> Cli(params string[] args)
    {
        var result=await Tools.Run(Tools.WireSockCli,args,args[0]=="connect"?40:8);
        if(result.Code!=0)
        {
            if(args[0]=="disconnect")
            {var status=await Tools.Run(Tools.WireSockCli,new[]{"status"});if(status.Code==0 && Disconnected(status.Text))return result.Text;}
            throw new IOException("WireSock "+args[0]+" failed: "+result.Text);
        }
        return result.Text;
    }
    static async Task WireSock(AgentConfig config,string id,WorkerReport report,string vpnusIp,bool systemTest)
    {
        string status=await Cli("status");
        if(!Disconnected(status))throw new InvalidOperationException("WireSock is already in use. Its active profile was not changed.");
        string settings=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"WireSock Foundation","WireSock Secure Connect","wiresock.config");
        if(File.Exists(settings))
        {
            var document=new System.Xml.XmlDocument();document.Load(settings);
            foreach(string tag in new[]{"VirtualAdapterMode","OverrideSplitTunnelingSettings"})
                if(document.SelectSingleNode("//"+tag)?.InnerText.Equals("True",StringComparison.OrdinalIgnoreCase)==true)
                    throw new InvalidOperationException("WireSock global mode is incompatible: "+tag);
        }
        if(systemTest)
        {
            await Request("vpnus-intent",id);
            Tools.StopVpnusGui(Process.GetCurrentProcess().SessionId);
            await Request("stop-vpnus-service",id);
        }
        var route=await Request("endpoint",id);
        int interfaceIndex=route.State!.PhysicalInterfaceIndex;
        var direct=await NetworkProbe.Trace(bind:Tools.Physical(interfaceIndex));
        var directIpify=await NetworkProbe.Trace(bind:Tools.Physical(interfaceIndex),urls:new[]{"https://api.ipify.org"});
        report.Results.Add(new("PhysicalBaseline",direct.Ip.ToString()!=vpnusIp,direct.Ip.ToString()==vpnusIp?"Physical baseline matches VPNUS; independent egress was not proved.":"Physical egress differs from the existing VPNUS egress.",direct.Milliseconds));
        if(direct.Ip.ToString()==vpnusIp)return;
        string folder=Tools.RunFolder(config,id),source=Path.Combine(folder,"source.conf"),desired=Path.Combine(folder,Tools.Profile(id)+".conf");
        await Cli("export",config.WireSockSourceProfile,source);
        string original=File.ReadAllText(source);
        var endpoint=Regex.Match(original,@"(?im)^\s*Endpoint\s*=\s*([^\s]+)");
        string sourceEndpoint=string.IsNullOrEmpty(config.SourceEndpoint)?config.EndpointIp+":"+config.EndpointPort:config.SourceEndpoint;
        if(!endpoint.Success || !endpoint.Groups[1].Value.Equals(sourceEndpoint,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Source profile endpoint changed. Update the protected installation before another test.");
        if(Regex.Matches(original,@"(?im)^\s*\[Peer\]\s*$").Count!=1)throw new InvalidDataException("The diagnostic profile expects one peer.");
        var lines=new List<string>();
        foreach(string line in original.Split('\n'))
        {
            if(Regex.IsMatch(line,@"^\s*(?:#@ws:\s*)?(DNS|AllowedIPs|AllowedApps|DisallowedApps|DisallowedIPs)\s*=",RegexOptions.IgnoreCase))continue;
            if(Regex.IsMatch(line,@"^\s*Endpoint\s*=",RegexOptions.IgnoreCase)){lines.Add("Endpoint = "+config.EndpointIp+":"+config.EndpointPort);continue;}
            lines.Add(line.TrimEnd('\r'));
            if(Regex.IsMatch(line,@"^\s*\[Peer\]\s*$"))
            {lines.Add("AllowedIPs = 0.0.0.0/0, ::/0");lines.Add("#@ws:AllowedApps = "+Tools.Bridge);}
        }
        File.WriteAllText(desired,string.Join("\r\n",lines),new UTF8Encoding(false));
        try
        {
            await Cli("import",desired);
            var connect=Cli("connect",Tools.Profile(id),"-network-lock","disabled","-log-level","warning","-exit");
            var timer=Stopwatch.StartNew();bool connected=false;
            while(timer.Elapsed.TotalSeconds<35)
            {
                status=await Cli("status");
                connected=status.Contains(Tools.Profile(id),StringComparison.Ordinal) && Regex.IsMatch(status,"Подключено к серверу|Connected to",RegexOptions.IgnoreCase);
                if(connected)break;
                if(connect.IsFaulted)await connect;
                await Task.Delay(1000);
            }
            try{await connect;}catch when(!connected){}
            report.Results.Add(new("WireSockState",connected,connected?"WireSock reports connected; data path is not yet verified.":"WireSock did not report a connected state before the deadline.",timer.ElapsedMilliseconds));
            if(!connected)return;
            var bridge=await Request("bridge",id);
            await Task.Delay(1000);
            if(bridge.State!.Processes.Count<2 || !bridge.State.Processes[1].Matches())throw new IOException("The diagnostic bridge exited.");
            var egress=await NetworkProbe.Trace("socks5://127.0.0.1:20818");
            bool distinct=egress.Ip.ToString()!=direct.Ip.ToString() && egress.Ip.ToString()!=vpnusIp;
            report.Results.Add(new("WireSockEgress",distinct,distinct?"HTTPS through the diagnostic bridge: "+egress.Ip+"; physical: "+direct.Ip+"; VPNUS: "+vpnusIp:"Egress matches a baseline; independent WireSock egress was not proved.",egress.Milliseconds));
            if(distinct && (string.IsNullOrEmpty(config.ExpectedEgressIp) || egress.Ip.ToString()==config.ExpectedEgressIp))await Request("record-egress-proof",id);
            if(!distinct || !systemTest)return;
            await Request("router",id);
            if(NetworkProbe.Vpnus!=null)throw new IOException("VPNUS reappeared during the system test.");
            var tun=NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n=>n.Name=="RoutePilot-Probe" && n.OperationalStatus==OperationalStatus.Up);
            if(tun==null)throw new IOException("The system TUN is not up.");
            var systemVpn=await NetworkProbe.Trace(urls:new[]{"https://www.cloudflare.com/cdn-cgi/trace"});
            report.Results.Add(new("TunVpnEgress",systemVpn.Ip.Equals(egress.Ip),"HTTPS without a configured proxy must use the previously verified WireSock egress.",systemVpn.Milliseconds));
            var systemDirect=await NetworkProbe.Trace(urls:new[]{"https://api.ipify.org"});
            report.Results.Add(new("TunDirectEgress",!systemDirect.Ip.Equals(egress.Ip) && systemDirect.Ip.ToString()!=vpnusIp,"Interface-bound direct route; IPv4 before/after TUN: "+directIpify.Ip+" / "+systemDirect.Ip+". ISP NAT can change its public address per flow.",systemDirect.Milliseconds));
            var dpi=await NetworkProbe.Trace("socks5://127.0.0.1:20828",urls:new[]{"https://api.ipify.org"});
            report.Results.Add(new("DpiPhysicalEgress",!dpi.Ip.Equals(egress.Ip) && dpi.Ip.ToString()!=vpnusIp,"TLS-record strategy uses the interface-bound physical outbound and differs from both VPN exits. ISP NAT may use several public IPs; egress: "+dpi.Ip,dpi.Milliseconds));
            using(var selectionBudget=new CancellationTokenSource(TimeSpan.FromSeconds(22)))
            {
                var chosen=await PolicyWorker.SelectRoute(async(port,token)=>
                {
                    var candidate=await PolicyWorker.Probe("discord.com",port==20808?20818:port,token);
                    report.Results.Add(new("Candidate",candidate.Reachable,(port==20808?"VPN":port==20828?"DPI":"Direct")+": "+candidate.Detail,Informational:true));return candidate;
                },selectionBudget.Token);
                report.Results.Add(new("AdaptiveSelection",chosen!=null,"Live candidate selection for discord.com: "+(chosen?.Route??"no proven path")));
            }
            using var client=new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(6)};
            foreach(var url in new[]{"https://yandex.ru/","https://www.youtube.com/","https://api.openai.com/v1/models"})
            {
                try
                {
                    using var response=await client.GetAsync(url,System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                    int code=(int)response.StatusCode;
                    bool usable=code is >=200 and <400 || url.Contains("api.openai.com") && code==401;
                    report.Results.Add(new("ServiceAccess",usable,new Uri(url).Host+": HTTP "+code+"; this service result is separate from VPN transport health."));
                }
                catch(Exception ex){report.Results.Add(new("ServiceAccess",false,new Uri(url).Host+": "+ex.GetBaseException().Message));}
            }
            using var proxyClient=new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler{UseProxy=true,Proxy=new System.Net.WebProxy("socks5://127.0.0.1:20818"),AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(6)};
            try{using var response=await proxyClient.GetAsync("https://api.openai.com/v1/models",System.Net.Http.HttpCompletionOption.ResponseHeadersRead);report.Results.Add(new("OpenAIProxy",(int)response.StatusCode==401,"Explicit VPN proxy: HTTP "+(int)response.StatusCode));}
            catch(Exception ex){report.Results.Add(new("OpenAIProxy",false,ex.GetBaseException().Message));}
        }
        finally
        {
            // The service also runs this cleanup after a killed or timed-out worker.
            if(systemTest){try{await Request("stop-router",id);}catch{}}
            await Cleanup(id);
        }
    }
    public static async Task Cleanup(string id)
    {
        var config=Config;string folder=Tools.RunFolder(config,id),name=Tools.Profile(id);
        var result=new WorkerReport{RunId=id};
        try
        {
            string status=await Cli("status");
            if(status.Contains(name,StringComparison.Ordinal))await Cli("disconnect");
            string list=await Cli("list");
            if(Regex.IsMatch(list,@"(?m)^\s*-\s*"+Regex.Escape(name)+@"\s*$"))await Cli("delete",name);
            foreach(string file in new[]{"source.conf",name+".conf"})File.Delete(Path.Combine(folder,file));
            result.Results.Add(new("WireSockCleanup",true,"Only the run-owned profile was removed."));
        }
        catch(Exception ex){result.Results.Add(new("WireSockCleanup",false,ex.Message));}
        Storage.Write(Path.Combine(folder,"cleanup.json"),result);
    }
}
