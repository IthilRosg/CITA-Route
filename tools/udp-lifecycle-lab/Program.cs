using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using RoutePilot.Agent;

// Real shipped binaries, loopback only. Does not load the user's configuration,
// start WireSock, create a TUN or modify any service, firewall or system route.
if(args.Length is not (3 or 4))throw new ArgumentException("Usage: client-root result-json idle-seconds [bound|tun]");
bool fast=args.Length==4&&args[3]=="tun-fast";
bool tun=args.Length==4&&args[3] is "tun" or "tun-fast";
if(tun&&(Environment.GetEnvironmentVariable("CITA_UDP_TUN_LAB")!="1"||Process.GetProcessesByName("smart-router").Length!=0))throw new IOException("TUN mode requires explicit isolated-VM opt-in and no existing engine.");
string root=Path.GetFullPath(args[0]), result=Path.GetFullPath(args[1]);
int idle=int.Parse(args[2]);if(idle is < 10 or > 390)throw new ArgumentException("Idle must be 10..390 seconds.");
string folder=Path.Combine(Path.GetDirectoryName(result)!,"udp-lifecycle-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
var observations=new List<object>();var owned=new List<Process>();
using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(idle+150));
var adapter=args.Length==4?NetworkInterface.GetAllNetworkInterfaces().First(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback&&n.GetIPProperties().GatewayAddresses.Any(g=>g.Address.AddressFamily==AddressFamily.InterNetwork)):null;
var local=adapter?.GetIPProperties().UnicastAddresses.First(a=>a.Address.AddressFamily==AddressFamily.InterNetwork).Address??IPAddress.Loopback;
using var echo=new UdpClient(new IPEndPoint(local,0));
var target=(IPEndPoint)echo.Client.LocalEndPoint!;
int echoIcmpErrors=0;
var echoTask=Task.Run(async()=>
{
    try
    {
        while(true)
        {
            // Windows reports late ICMP from deliberately aborted UDP clients to
            // the echo socket. Keep the fixture alive; count rather than hide it.
            try
            {
                var p=await echo.ReceiveAsync(deadline.Token);
                if(fast&&p.Buffer.AsSpan().StartsWith("CITA"u8))await Task.Delay(75,deadline.Token);
                await echo.SendAsync(p.Buffer,p.RemoteEndPoint,deadline.Token);
            }
            catch(SocketException e)when(e.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused){Interlocked.Increment(ref echoIcmpErrors);}
        }
    }
    catch(OperationCanceledException){}
});
int Port(){var l=new TcpListener(IPAddress.Loopback,0);l.Start();try{return ((IPEndPoint)l.LocalEndpoint).Port;}finally{l.Stop();}}
int bridgePort=Port(),routerPort=Port();while(routerPort==bridgePort)routerPort=Port();
int bridgeDebug=Port(),routerDebug=Port();
void Write(string path,object value)=>File.WriteAllText(path,JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));
void Save(string phase,bool? success=null,string? error=null)=>Write(result,new{phase,success,error,boundInterface=adapter!=null,tun,fast,echoIcmpErrors,scope=tun?"Isolated VM: one test /32 TUN route to own local echo, no WireSock/default route/service changes":"Local-machine SOCKS UDP lifecycle; own echo only; no TUN/WireSock/system network changes",observations});
object Inbound(int port)=>new{type="mixed",listen="127.0.0.1",listen_port=port};
Process Start(string name,object config)
{
    string path=Path.Combine(folder,name+".json");
    var node=JsonSerializer.SerializeToNode(config)!;
    if(fast)node["experimental"]=new JsonObject{["debug"]=new JsonObject{["listen"]="127.0.0.1:"+(name=="vpn-bridge"?bridgeDebug:routerDebug)}};
    if(fast)foreach(var item in node["inbounds"]!.AsArray())
        if(item?["type"]?.GetValue<string>()=="tun")item["udp_timeout"]="8s";
        else if(name=="vpn-bridge")item!["udp_timeout"]="5s";
    Write(path,node);
    var info=new ProcessStartInfo(Path.Combine(root,"runtime",name+".exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
    foreach(string arg in new[]{"run","-c",path,"--disable-color"})info.ArgumentList.Add(arg);
    var p=Process.Start(info)!;owned.Add(p);
    _=p.StandardOutput.ReadToEndAsync();_=p.StandardError.ReadToEndAsync();return p;
}
async Task DumpStacks()
{
    if(!fast)return;
    using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(3),MaxResponseContentBufferSize=2*1024*1024};
    foreach(var pair in new[]{(Name:"bridge",Port:bridgeDebug),(Name:"router",Port:routerDebug)})
        try{await File.WriteAllTextAsync(result+"."+pair.Name+"-goroutines.txt",await client.GetStringAsync("http://127.0.0.1:"+pair.Port+"/debug/pprof/goroutine?debug=2"));}catch(HttpRequestException){}catch(TaskCanceledException){}
}
object Sample(Process p){p.Refresh();return new{pid=p.Id,privateBytes=p.PrivateMemorySize64,handles=p.HandleCount,udp=UdpTable.Count(p.Id),exited=p.HasExited};}
Process? bridge=null,router=null;
void Observe(string label){observations.Add(new{label,utc=DateTime.UtcNow,bridge=Sample(bridge!),router=Sample(router!)});Save(label);}
async Task Pulse(SocksUdpAssociation a)
{
    using var pulse=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);pulse.CancelAfter(3000);
    var payload=new byte[64];Random.Shared.NextBytes(payload);await a.Send(payload,pulse.Token);
    byte[]? reply=await a.Receive(pulse.Token);if(reply==null||!reply.AsSpan().SequenceEqual(payload))throw new IOException("Echo mismatch");
}
async Task ResetControl(bool sendPacket)
{
    using var c=new TcpClient();using var u=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
    await c.ConnectAsync(IPAddress.Loopback,bridgePort,deadline.Token);var s=c.GetStream();
    await s.WriteAsync(new byte[]{5,1,0},deadline.Token);await s.ReadExactlyAsync(new byte[2],deadline.Token);
    await s.WriteAsync(new byte[]{5,3,0,1,127,0,0,1,0,0},deadline.Token);
    if(sendPacket)
    {
        var reply=new byte[10];await s.ReadExactlyAsync(reply,deadline.Token);
        if(reply[1]!=0||reply[3]!=1)throw new IOException("Abort fixture handshake");
        var destination=new IPEndPoint(new IPAddress(reply.AsSpan(4,4)),(reply[8]<<8)|reply[9]);
        var packet=new byte[74];packet[3]=1;target.Address.GetAddressBytes().CopyTo(packet,4);packet[8]=(byte)(target.Port>>8);packet[9]=(byte)target.Port;
        await u.SendAsync(packet,destination,deadline.Token);
    }
    c.Client.LingerState=new LingerOption(true,0);
}
var fakeTarget=new IPEndPoint(IPAddress.Parse("203.0.113.77"),target.Port);
async Task NativePulse(UdpClient u)
{
    using var pulse=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);pulse.CancelAfter(3000);
    var payload=new byte[64];Random.Shared.NextBytes(payload);"CITA"u8.CopyTo(payload);await u.SendAsync(payload,fakeTarget,pulse.Token);
    var response=await u.ReceiveAsync(pulse.Token);if(!response.Buffer.AsSpan().SequenceEqual(payload))throw new IOException("Native echo mismatch");
}
try
{
    var egress=new Dictionary<string,object>{{"type","direct"},{"tag","direct"}};if(adapter!=null)egress["bind_interface"]=adapter.Name;
    bridge=Start("vpn-bridge",new{log=new{disabled=true},inbounds=new[]{Inbound(bridgePort)},outbounds=new[]{egress},route=new{final="direct"}});
    var ins=new List<object>{Inbound(routerPort)};
    if(tun)ins.Add(new{type="tun",tag="udp-lab",interface_name="CitaUdpLab",address=new[]{"172.30.127.1/30"},route_address=new[]{"203.0.113.77/32"},mtu=1400,auto_route=true,strict_route=false,stack="mixed"});
    var route=new Dictionary<string,object>{{"final","vpn"}};
    if(tun)route["rules"]=new[]{new{inbound=new[]{"udp-lab"},action="route-options",override_address=local.ToString()}};
    router=Start("smart-router",new{log=new{disabled=true},inbounds=ins,outbounds=new[]{new{type="socks",tag="vpn",server="127.0.0.1",server_port=bridgePort,version="5"}},route});
    foreach(int port in new[]{bridgePort,routerPort})
    {
        bool ready=false;for(int i=0;i<100;i++){deadline.Token.ThrowIfCancellationRequested();if(bridge.HasExited||router.HasExited)throw new IOException("Fixture engine exited");try{using var c=new TcpClient();await c.ConnectAsync(IPAddress.Loopback,port,deadline.Token);ready=true;break;}catch(SocketException){await Task.Delay(100,deadline.Token);}}
        if(!ready)throw new IOException("Fixture engine not ready");
    }
    Observe("baseline");
    using(var active=await SocksUdpAssociation.Open(routerPort,target,deadline.Token))
    {
        using var nativeActive=tun?new UdpClient(AddressFamily.InterNetwork):null;
        if(tun){await Task.Delay(1000,deadline.Token);await NativePulse(nativeActive!);}
        await Pulse(active);
        foreach(int port in new[]{bridgePort,routerPort})
        {
            for(int wave=0;wave<3;wave++)
            {
                for(int i=0;i<32;i++){using var a=await SocksUdpAssociation.Open(port,target,deadline.Token);await Pulse(a);}
                for(int i=0;i<16;i++){using var a=await SocksUdpAssociation.Open(port,target,deadline.Token);}
                await Task.Delay(500,deadline.Token);await Pulse(active);Observe((port==bridgePort?"direct":"nested")+"-wave-"+wave);
            }
        }
        foreach(bool sendPacket in new[]{false,true})
        {
            for(int wave=0;wave<4;wave++){await Task.WhenAll(Enumerable.Range(0,32).Select(_=>ResetControl(sendPacket)));await Task.Delay(250,deadline.Token);}
            await Pulse(active);Observe(sendPacket?"reset-after-packet":"reset-during-handshake");
        }
        if(tun)
        {
            for(int wave=0;wave<3;wave++)
            {
                for(int i=0;i<32;i++){using var client=new UdpClient(AddressFamily.InterNetwork);await NativePulse(client);}
                await NativePulse(nativeActive!);await Pulse(active);Observe("tun-short-wave-"+wave);
            }
        }
        var clock=Stopwatch.StartNew();int pulses=0;
        while(clock.Elapsed.TotalSeconds<idle)
        {
            await Pulse(active);if(tun)await NativePulse(nativeActive!);pulses++;await Task.Delay(500,deadline.Token);
            if(pulses%20==0)Observe("idle-"+(int)clock.Elapsed.TotalSeconds);
        }
        Observe("active-before-close");
        if(UdpTable.Count(bridge.Id)>(tun?4:2))throw new IOException("Expired short UDP clients still retain bridge sockets.");
    }
    await Task.Delay(2000,deadline.Token);Observe("all-clients-closed");
    if(UdpTable.Count(bridge.Id)>(tun?2:0))throw new IOException("Closed control connections retain bridge sockets.");
    Save("Finished",true);
}
catch(Exception error){await DumpStacks();Save("Failed",false,error.GetType().Name+": "+error.Message);Environment.ExitCode=1;}
finally
{
    deadline.Cancel();echo.Dispose();
    try{await echoTask;}
    catch(Exception e)when(deadline.IsCancellationRequested&&e is ObjectDisposedException or OperationCanceledException or SocketException){}
    finally
    {
        foreach(var p in owned){try{if(!p.HasExited){p.Kill(true);await p.WaitForExitAsync();}}finally{p.Dispose();}}
    }
    // Retain only lab configs and the report, never user profile data.
}

static class UdpTable
{
    [DllImport("iphlpapi.dll")]static extern uint GetExtendedUdpTable(IntPtr table,ref uint size,bool order,int family,int tableClass,uint reserved);
    internal static int Count(int pid)
    {
        int total=0;
        foreach(int family in new[]{2,23})
        {
            uint size=0;uint rc=GetExtendedUdpTable(IntPtr.Zero,ref size,false,family,1,0);if(rc!=122&&rc!=0)throw new IOException("UDP table unavailable");
            if(size<4||size>16*1024*1024)throw new IOException("UDP table size");
            bool read=false;
            for(int retry=0;retry<3;retry++)
            {
                int capacity=(int)size;IntPtr memory=Marshal.AllocHGlobal(capacity);
                try
                {
                    rc=GetExtendedUdpTable(memory,ref size,false,family,1,0);if(rc==122)continue;
                    if(rc!=0||size>capacity)throw new IOException("UDP table read");
                    int rows=Marshal.ReadInt32(memory),stride=family==2?12:28;
                    if(rows<0||4L+(long)rows*stride>size)throw new IOException("UDP table bounds");
                    for(int i=0;i<rows;i++)if(Marshal.ReadInt32(memory,4+i*stride+stride-4)==pid)total++;
                    read=true;break;
                }
                finally{Marshal.FreeHGlobal(memory);}
            }
            if(!read)throw new IOException("UDP table changed repeatedly");
        }
        return total;
    }
}
