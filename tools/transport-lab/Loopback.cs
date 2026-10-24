using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Cita.TransportLab;

internal sealed record Pulse(bool Tcp,bool Udp,int TcpError,int UdpError);
internal sealed class Echo : IDisposable
{
    readonly TcpListener tcp;
    readonly UdpClient udp;
    readonly CancellationTokenSource stop=new();
    long received;
    internal long Received=>Interlocked.Read(ref received);
    internal int TcpPort=>((IPEndPoint)tcp.LocalEndpoint).Port;
    internal int UdpPort=>((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    internal Echo(IPAddress address)
    {
        tcp=new(address,0);tcp.Start();udp=new(new IPEndPoint(address,0));
        _=Accept();_=Datagrams();
    }
    async Task Accept()
    {
        try{while(!stop.IsCancellationRequested){var client=await tcp.AcceptTcpClientAsync(stop.Token);_=Stream(client);}}
        catch(Exception)when(stop.IsCancellationRequested){}
    }
    async Task Stream(TcpClient client)
    {
        using(client)
        {
            try
            {
                byte[] buffer=new byte[16];var stream=client.GetStream();
                while(!stop.IsCancellationRequested)
                {
                    int n=await stream.ReadAsync(buffer,stop.Token);if(n==0)return;
                    Interlocked.Add(ref received,n);await stream.WriteAsync(buffer.AsMemory(0,n),stop.Token);
                }
            }
            catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException){}
        }
    }
    async Task Datagrams()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var packet=await udp.ReceiveAsync(stop.Token);Interlocked.Add(ref received,packet.Buffer.Length);
                await udp.SendAsync(packet.Buffer,packet.RemoteEndPoint,stop.Token);
            }
        }
        catch(Exception)when(stop.IsCancellationRequested){}
    }
    public void Dispose(){stop.Cancel();tcp.Stop();udp.Dispose();stop.Dispose();}
}
internal sealed class Peer : IDisposable
{
    readonly TcpClient tcp;
    readonly UdpClient udp;
    readonly IPEndPoint tcpEndpoint,udpEndpoint;
    int initialError;
    internal Peer(IPAddress address,int tcpPort,int udpPort,int bindIndex=0,bool tunnelFixture=false)
    {
        if(!IPAddress.IsLoopback(address) && !(tunnelFixture&&address.Equals(IPAddress.Parse("198.18.123.1"))&&WireSockExperiment.Disposable))
            throw new IOException("Transport lab probes require loopback or the isolated synthetic tunnel destination.");
        tcp=new(address.AddressFamily);udp=new(address.AddressFamily);
        if(bindIndex>0)
        {
            int index=IPAddress.HostToNetworkOrder(bindIndex);
            // Windows IP_UNICAST_IF = 31; .NET has no named enum member.
            tcp.Client.SetSocketOption(SocketOptionLevel.IP,(SocketOptionName)31,index);
            udp.Client.SetSocketOption(SocketOptionLevel.IP,(SocketOptionName)31,index);
        }
        tcpEndpoint=new(address,tcpPort);udpEndpoint=new(address,udpPort);
    }
    internal async Task Connect()
    {
        try{using var budget=new CancellationTokenSource(2000);await tcp.ConnectAsync(tcpEndpoint,budget.Token);}
        catch(Exception ex){initialError=Error(ex);}
        udp.Connect(udpEndpoint);
    }
    static int Error(Exception ex)=>ex is SocketException se?se.ErrorCode:ex.InnerException is SocketException inner?inner.ErrorCode:ex is OperationCanceledException?-2:-1;
    internal async Task<Pulse> Check()
    {
        // Independent budgets so TCP backpressure cannot suppress the UDP test.
        var a=Tcp();var b=Udp();await Task.WhenAll(a,b);return new(a.Result==0,b.Result==0,a.Result,b.Result);
    }
    async Task<int> Tcp()
    {
        if(initialError!=0)return initialError;
        try
        {
            using var budget=new CancellationTokenSource(1200);var stream=tcp.GetStream();
            await stream.WriteAsync(new byte[]{73},budget.Token);byte[] data=new byte[1];await stream.ReadExactlyAsync(data,budget.Token);
            return data[0]==73?0:-3;
        }
        catch(Exception ex){return Error(ex);}
    }
    async Task<int> Udp()
    {
        try
        {
            using var budget=new CancellationTokenSource(1200);await udp.SendAsync(new byte[]{91},budget.Token);
            var result=await udp.ReceiveAsync(budget.Token);return result.Buffer.AsSpan().SequenceEqual(new byte[]{91})?0:-3;
        }
        catch(Exception ex){return Error(ex);}
    }
    public void Dispose(){tcp.Dispose();udp.Dispose();}
    internal static async Task Run(string[] args,bool once)
    {
        bool tunnel=args.Length==5&&args[0]=="--tunnel-peer";
        if(!tunnel && (args.Length!=4 || args[1] is not("4" or "6")))throw new ArgumentException("Invalid loopback probe.");
        using var peer=new Peer(tunnel?IPAddress.Parse("198.18.123.1"):args[1]=="4"?IPAddress.Loopback:IPAddress.IPv6Loopback,
            int.Parse(args[2]),int.Parse(args[3]),tunnel?int.Parse(args[4]):0,tunnel);
        await peer.Connect();
        if(once){Console.WriteLine(JsonSerializer.Serialize(await peer.Check()));return;}
        Console.WriteLine("READY");
        while(await Console.In.ReadLineAsync() is string command)
        {
            if(command=="close")return;
            if(command!="pulse")throw new ArgumentException("Unknown peer command.");
            Console.WriteLine(JsonSerializer.Serialize(await peer.Check()));
        }
    }
}
