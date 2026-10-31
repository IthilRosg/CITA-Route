using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RoutePilot.Agent;

const string root=@"C:\Program Files\RoutePilot\Agent\zapret-live-lab\module";
if((args.Length!=0&&!(args.Length==1&&args[0] is "--go-client" or "--go-wfp"))||Environment.MachineName!="USER")throw new InvalidOperationException("Physical lab only.");
using var receipt=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"upstream-receipt.json")));
var hashes=receipt.RootElement.GetProperty("files").EnumerateArray().ToDictionary(
    f=>f.GetProperty("name").GetString()!,f=>f.GetProperty("sha256").GetString()!);
var bundle=new NativeZapretBundle(root,hashes);
bool fileLockVerified=false;
using(var trusted=NativeZapretTrust.Open(bundle))
{
    try{using var write=File.Open(Path.Combine(root,"lua","zapret-lib.lua"),FileMode.Open,FileAccess.Write,FileShare.ReadWrite);}
    catch(IOException){fileLockVerified=true;}
    if(!fileLockVerified)throw new IOException("Protected component lock failed.");
}
var nic=NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.NetworkInterfaceType==NetworkInterfaceType.Wireless80211&&
    n.OperationalStatus==OperationalStatus.Up&&n.GetIPProperties().UnicastAddresses.Any(a=>a.Address.ToString()=="192.168.1.124"));
uint index=checked((uint)nic.GetIPProperties().GetIPv4Properties().Index);
var local=IPAddress.Parse("192.168.1.124");
const string domain="www.wikipedia.org";
using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
var resolved=await Dns.GetHostAddressesAsync(domain,deadline.Token);
var remote=resolved.First(a=>a.AddressFamily==AddressFamily.InterNetwork);
if(Environment.GetEnvironmentVariable("CITA_CONTROL_IPV4") is {} pin)
{
    if(!IPAddress.TryParse(pin,out var pinned)||pinned.AddressFamily!=AddressFamily.InterNetwork||!resolved.Contains(pinned))throw new IOException("Control IP must belong to this DNS answer.");
    remote=pinned;
}
if(args.Length==1)return await GoPeerLab.Run(bundle,index,local,domain,remote,fileLockVerified,args[0]=="--go-wfp");
string epoch=Guid.NewGuid().ToString("N");
using var serviceStop=new CancellationTokenSource();
NativeZapretConnection? connection=null;
using var self=Process.GetCurrentProcess();
var server=new NativeCoreConnector(self,epoch,()=>true,
    request=>request.Domain==domain&&request.Application==Environment.ProcessPath&&request.Address==remote.ToString(),
    async(request,stop)=>connection=await NativeZapretConnection.OpenAsync(bundle,
        new(epoch,domain,request.Application,request.Address,request.Port),index,local.ToString(),
        new HashSet<string>{"143.20.37.241"},()=>true,serviceStop.Token,stop),serviceStop.Token);
server.Start();
string status="",failure="";bool tlsValidated=false,readyBeforeConnect=false,serviceCancellation=false;
try
{
    using var pipe=new NamedPipeClientStream(".",server.Endpoint.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous);
    await pipe.ConnectAsync(deadline.Token);
    await NativeCoreConnectorTests.WriteRequest(pipe,new(epoch,server.Endpoint.Capability,Guid.NewGuid().ToString("N"),
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),domain,Environment.ProcessPath!,remote.ToString(),443,"tcp"),deadline.Token);
    byte[] ack=new byte[1];await pipe.ReadExactlyAsync(ack,deadline.Token);
    if(ack[0]!=0||connection==null)throw new IOException("Connector readiness acknowledgement missing.");
    readyBeforeConnect=connection.Owner.ReadyTimestamp>0&&connection.ConnectTimestamp>=connection.Owner.ReadyTimestamp;
    using var frames=new NativeConnectorFrames(pipe,true);
    using(var tls=new SslStream(frames,leaveInnerStreamOpen:true))
    {
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost=domain},deadline.Token);
        tlsValidated=tls.IsAuthenticated&&tls.IsEncrypted;
        await tls.WriteAsync(Encoding.ASCII.GetBytes($"HEAD / HTTP/1.1\r\nHost: {domain}\r\nConnection: close\r\n\r\n"),deadline.Token);
        byte[] response=new byte[2048];int count=await tls.ReadAsync(response,deadline.Token);
        status=Encoding.ASCII.GetString(response,0,count).Split('\n')[0].Trim();
        if(!status.StartsWith("HTTP/1."))throw new IOException("No HTTP status received.");
    }
    // This cancellation occurs after WaitReadyAsync returned: it must still
    // reach the child worker, rather than relying on explicit lease disposal.
    serviceStop.Cancel();
    var wait=Stopwatch.StartNew();
    while(ProcessAlive(connection.Owner.WorkerPid)&&wait.Elapsed<TimeSpan.FromSeconds(2))await Task.Delay(25);
    serviceCancellation=!ProcessAlive(connection.Owner.WorkerPid);
}
catch(Exception error){failure=error.GetType().Name+": "+error.Message;}
finally
{
    try{await server.DisposeAsync();}
    catch(Exception error){failure+=" cleanup: "+error.GetType().Name+": "+error.Message;}
}
bool success=failure.Length==0&&fileLockVerified&&tlsValidated&&readyBeforeConnect&&serviceCancellation&&
    connection?.Owner.RetirementConfirmed==true&&!server.CleanupUnconfirmed&&NativeBoundTcpLease.RetainedCount==0&&NativeZapretWorker.SlotsInUse==0;
Console.WriteLine(JsonSerializer.Serialize(new{success,fileLockVerified,tlsValidated,readyBeforeConnect,serviceCancellation,status,
    failure,retirementConfirmed=connection?.Owner.RetirementConfirmed==true,socketLeases=NativeBoundTcpLease.RetainedCount,
    workerSlots=NativeZapretWorker.SlotsInUse,domain,remote=remote.ToString(),workerPid=connection?.Owner.WorkerPid??0,
    connectorFlows=server.Accepted,corePeer="in-process lab caller, not actual Go core",
    scope="Real private pipe + Zapret TLS connection; not proof of blocked-site bypass or production integration"}));
return success?0:1;

static bool ProcessAlive(int pid)
{
    try{using var p=Process.GetProcessById(pid);return !p.HasExited;}
    catch(ArgumentException){return false;}
}
