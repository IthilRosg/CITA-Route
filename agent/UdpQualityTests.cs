using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RoutePilot.Quality;

namespace RoutePilot.Agent;

internal static class UdpQualityTests
{
    internal static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
    internal static async Task Run(string root)
    {
        byte[] key=RandomNumberGenerator.GetBytes(32),nonce=RandomNumberGenerator.GetBytes(16);
        var packet=UdpProbeProtocol.Create(key,nonce,9);
        Assert(UdpProbeProtocol.Match(packet,key,nonce,out ulong id)&&id==9,"Authenticated UDP sequence round trip.");
        packet[30]^=1;Assert(!UdpProbeProtocol.Valid(packet,key),"A modified UDP packet must fail authentication.");
        Assert(!UdpProbeProtocol.Match(UdpProbeProtocol.Create(key,nonce,1),key,new byte[16],out _),"An old probe round cannot satisfy a new nonce.");
        var accounting=new UdpRoundAccounting(10,100);
        accounting.Sent(0,0);accounting.Sent(1,0);accounting.Sent(2,0);accounting.Sent(3,0);
        accounting.Accept(2,10);accounting.Accept(0,20);accounting.Accept(0,30);accounting.Accept(1,101);accounting.Accept(99,20);
        var sample=accounting.Finish(DateTime.UtcNow);
        Assert(sample.Sent==4&&sample.Received==2&&sample.Duplicates==1&&sample.Reordered==1&&sample.Late==1,"Duplicates, reordered and late replies need independent accounting.");
        var window=new UdpQualityWindow();var now=DateTime.UtcNow;
        var measured=window.Add(new(now,10,8,1,1,0),now);
        Assert(measured.LossPercent==20 && measured.Sent==10,"Controlled 2/10 missing replies must measure 20%.");
        measured=window.Add(new(now,10,0,0,0,0),now);
        Assert(measured.Status=="Unavailable" && measured.LossPercent==null,"A wholly unanswered round must not be reported as proven 100% packet loss.");
        for(int i=0;i<20;i++)measured=window.Add(new(now,10,10,0,0,0),now);
        Assert(measured.Sent==60 && measured.LossPercent==0,"The measurement window must remain bounded to 60 probes.");
        var health=new QualityHealth{SessionId="current",Healthy=true,BridgeGeneration=2,UpdatedUtc=now};
        var watch=new QualityHealth{SessionId="current",WireSockState="Connected",UpdatedUtc=now};
        var session=new QualitySession{Id="current",BridgeReady=true,BridgeGeneration=2};
        Assert(UdpQualityWorker.VpnReady(session,health,watch,now),"Fresh managed VPN health enables UDP probes.");
        session.BridgeGeneration++;Assert(!UdpQualityWorker.VpnReady(session,health,watch,now),"A bridge generation change invalidates VPN permission.");
        session.BridgeGeneration--;watch.WireSockState="Unknown";
        Assert(!UdpQualityWorker.VpnReady(session,health,watch,now),"Unknown WireSock status must suppress VPN probes.");
        var options=new UdpQualityOptions{Address="127.0.0.1",Key=Convert.ToHexString(key),Enabled=true};
        Assert(!UdpQualityWorker.ValidOptions(options) && UdpQualityWorker.ValidOptions(options,true),"Loopback targets are fixture-only, not production probes.");
        await using var reflector=new UdpReflectorFixture(key,true);
        await using var direct=new UdpSocksFixture(reflector.Endpoint);
        var result=await UdpQualityProbe.Run(direct.Port,reflector.Endpoint,key,CancellationToken.None,20,300);
        Assert(result.Sent==10&&result.Received==8&&result.Duplicates==1,"The SOCKS UDP fixture must observe actual dropped and duplicate replies.");
        int before=reflector.Requests;
        await using(var refused=new UdpSocksFixture(reflector.Endpoint,true))
        {
            bool failed=false;try{await UdpQualityProbe.Run(refused.Port,reflector.Endpoint,key,CancellationToken.None,20,300);}catch(IOException){failed=true;}
            Assert(failed && reflector.Requests==before,"Refused UDP ASSOCIATE cannot fall back to a direct UDP socket.");
        }
        using(var cancel=new CancellationTokenSource(60))
        {
            bool cancelled=false;try{await UdpQualityProbe.Run(direct.Port,reflector.Endpoint,key,cancel.Token,100,300);}catch(OperationCanceledException){cancelled=true;}
            Assert(cancelled,"Cancellation must discard a partial round instead of adding packet loss.");
        }
        string fixture=Path.Combine(root,"udp-quality");Directory.CreateDirectory(Path.Combine(fixture,"data"));Directory.CreateDirectory(Path.Combine(fixture,"runtime"));
        await using var vpn=new UdpSocksFixture(reflector.Endpoint);
        options.Address=reflector.Endpoint.Address.ToString();options.Port=reflector.Endpoint.Port;
        Storage.Write(Path.Combine(fixture,"data","udp-quality.json"),options);
        session=new(){Running=true,Id="quality-session",SourceProfile="fixture",NetworkSignature="fixture-network",BridgeReady=true,BridgeGeneration=1};
        string SessionPath()=>Path.Combine(fixture,"runtime","session.json");
        Storage.Write(SessionPath(),session);
        Storage.Write(Path.Combine(fixture,"runtime","session-health.json"),new QualityHealth{SessionId=session.Id,BridgeGeneration=1,Healthy=true,UpdatedUtc=DateTime.UtcNow});
        Storage.Write(Path.Combine(fixture,"runtime","watch-state.json"),new QualityHealth{SessionId=session.Id,WireSockState="Connected",UpdatedUtc=DateTime.UtcNow});
        using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(18));
        var worker=UdpQualityWorker.Run(fixture,session.Id,session.NetworkSignature,new(){Direct=direct.Port,Vpn=vpn.Port},lifetime.Token,true);
        string statePath=Path.Combine(fixture,"runtime","udp-quality.json");
        async Task<UdpQualityState> Until(Func<UdpQualityState,bool> accept)
        {
            while(!lifetime.IsCancellationRequested)
            {
                try{if(File.Exists(statePath)){var state=Storage.Read<UdpQualityState>(statePath);if(accept(state))return state;}}
                // Windows can briefly hold the published report during atomic
                // replacement. Retry only sharing/lock violations, under the
                // existing fixture deadline; malformed state still fails.
                catch(IOException ex)when(ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)){}
                await Task.Delay(100,lifetime.Token);
            }
            throw new Exception("UDP worker fixture timed out.");
        }
        try
        {
            var state=await Until(s=>s.Direct.Status=="Measured"&&s.Vpn.Status=="Measured");
            Assert(state.Direct.LossPercent==20 && state.Vpn.LossPercent==20,"Direct and VPN must use separate measured channels.");
            Storage.Write(Path.Combine(fixture,"runtime","watch-state.json"),new QualityHealth{SessionId=session.Id,WireSockState="Unknown",UpdatedUtc=DateTime.UtcNow});
            state=await Until(s=>s.Vpn.Reason=="PathNotVerified");
            Assert(state.Direct.Status=="Measured" && state.Direct.LossPercent==20,"Losing VPN proof must not reset the independent Direct measurement.");
            session.BridgeGeneration=2;Storage.Write(SessionPath(),session);
            state=await Until(s=>s.BridgeGeneration==2);
            Assert(state.Vpn.Status=="Unavailable"&&state.Vpn.LossPercent==null,"Old results cannot survive bridge replacement without new VPN proof.");
            session.Running=false;Storage.Write(SessionPath(),session);
            await worker.WaitAsync(TimeSpan.FromSeconds(2));int after=reflector.Requests;
            await Task.Delay(350);Assert(after==reflector.Requests,"Manual Stop must stop quality traffic independently of the GUI.");
        }
        finally{lifetime.Cancel();await worker;}
        Console.WriteLine("PASS: authenticated SOCKS UDP, 20% controlled loss, duplicate/order/deadline accounting, no direct fallback, bounded windows, session/bridge guards and Stop cancellation.");
    }
}

internal sealed class UdpReflectorFixture : IAsyncDisposable
{
    readonly UdpClient _socket=new(new IPEndPoint(IPAddress.Loopback,0));
    readonly CancellationTokenSource _stop=new();readonly Task _run;int _requests;
    internal IPEndPoint Endpoint=>(IPEndPoint)_socket.Client.LocalEndPoint!;
    internal int Requests=>Volatile.Read(ref _requests);
    internal UdpReflectorFixture(byte[] key,bool drops)
    {
        _run=Task.Run(async()=>
        {
            try
            {
                while(true)
                {
                    var request=await _socket.ReceiveAsync(_stop.Token);
                    if(!UdpProbeProtocol.Valid(request.Buffer,key))continue;Interlocked.Increment(ref _requests);
                    ulong sequence=System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(request.Buffer.AsSpan(24));
                    if(drops && sequence is 2 or 7)continue;
                    await _socket.SendAsync(request.Buffer,request.RemoteEndPoint,_stop.Token);
                    if(drops && sequence==4)await _socket.SendAsync(request.Buffer,request.RemoteEndPoint,_stop.Token);
                }
            }
            catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
        });
    }
    public async ValueTask DisposeAsync(){_stop.Cancel();await _run;_socket.Dispose();_stop.Dispose();}
}

internal sealed class UdpSocksFixture : IAsyncDisposable
{
    readonly TcpListener _listener=new(IPAddress.Loopback,0);readonly CancellationTokenSource _stop=new();
    readonly List<Task> _clients=new();readonly Task _run;
    internal int Port=>((IPEndPoint)_listener.LocalEndpoint).Port;
    internal UdpSocksFixture(IPEndPoint target,bool refuse=false)
    {
        _listener.Start();
        _run=Task.Run(async()=>
        {
            try{while(true){var client=await _listener.AcceptTcpClientAsync(_stop.Token);_clients.Add(Serve(client,target,refuse,_stop.Token));}}
            catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
        });
    }
    static async Task Serve(TcpClient control,IPEndPoint target,bool refuse,CancellationToken stop)
    {
        using(control)using(var done=CancellationTokenSource.CreateLinkedTokenSource(stop))
        using(var relay=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
        {
            try
            {
                var stream=control.GetStream();var hello=new byte[3];await stream.ReadExactlyAsync(hello,done.Token);
                await stream.WriteAsync(new byte[]{5,0},done.Token);var request=new byte[10];await stream.ReadExactlyAsync(request,done.Token);
                if(request[1]!=3)throw new IOException("Fixture requires UDP ASSOCIATE.");
                int port=((IPEndPoint)relay.Client.LocalEndPoint!).Port;
                await stream.WriteAsync(new byte[]{5,(byte)(refuse?7:0),0,1,127,0,0,1,(byte)(port>>8),(byte)port},done.Token);
                if(refuse)return;
                var clientEndpoint=new IPEndPoint(IPAddress.Loopback,(request[8]<<8)|request[9]);
                async Task Transfer()
                {
                    while(true)
                    {
                        var packet=await relay.ReceiveAsync(done.Token);
                        if(packet.RemoteEndPoint.Equals(clientEndpoint))
                        {if(packet.Buffer.Length==74)await relay.SendAsync(packet.Buffer.AsMemory(10),target,done.Token);}
                        else if(packet.RemoteEndPoint.Equals(target))
                        {
                            var response=new byte[10+packet.Buffer.Length];response[3]=1;target.Address.GetAddressBytes().CopyTo(response,4);
                            response[8]=(byte)(target.Port>>8);response[9]=(byte)target.Port;packet.Buffer.CopyTo(response,10);
                            await relay.SendAsync(response,clientEndpoint,done.Token);
                        }
                    }
                }
                var forwarding=Transfer();try{var buffer=new byte[1];await stream.ReadAsync(buffer,done.Token);}finally{done.Cancel();try{await forwarding;}catch(OperationCanceledException){}}
            }
            catch(Exception ex)when(ex is OperationCanceledException or IOException or SocketException){}
        }
    }
    public async ValueTask DisposeAsync(){_stop.Cancel();await _run;await Task.WhenAll(_clients);_listener.Stop();_stop.Dispose();}
}
