using System.Net;
using System.Net.Sockets;

namespace Cita.TransportLab;

// A local synthetic endpoint relay. Records only WireGuard packet-type counts,
// never ciphertext bodies, plaintext, profiles, keys or remote user endpoints.
internal sealed class WirePackets : IDisposable
{
    readonly UdpClient socket=new(new IPEndPoint(IPAddress.Loopback,0));
    readonly CancellationTokenSource stop=new();
    readonly IPEndPoint server;
    IPEndPoint? client;
    readonly Dictionary<uint,IPEndPoint> receivers=new();
    long toServer,fromServer,initiations,responses,toServerTransport,fromServerTransport;
    internal int Port=>((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    internal WirePackets(int serverPort){server=new(IPAddress.Loopback,serverPort);_=Run();}
    async Task Run()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var packet=await socket.ReceiveAsync(stop.Token);
                if(!IPAddress.IsLoopback(packet.RemoteEndPoint.Address))continue;
                int type=packet.Buffer.Length>=4?System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(packet.Buffer):0;
                if(packet.RemoteEndPoint.Port==server.Port)
                {
                    Interlocked.Increment(ref fromServer);if(type==2)Interlocked.Increment(ref responses);if(type==4)Interlocked.Increment(ref fromServerTransport);
                    int offset=type==2?8:4;
                    IPEndPoint? destination=client;
                    if(packet.Buffer.Length>=offset+4&&receivers.TryGetValue(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Buffer.AsSpan(offset)),out var mapped))destination=mapped;
                    if(destination!=null)await socket.SendAsync(packet.Buffer,destination,stop.Token);
                }
                else
                {
                    client=packet.RemoteEndPoint;Interlocked.Increment(ref toServer);if(type==1)Interlocked.Increment(ref initiations);if(type==4)Interlocked.Increment(ref toServerTransport);
                    if(type==1&&packet.Buffer.Length>=8){if(receivers.Count>=64)receivers.Clear();receivers[System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Buffer.AsSpan(4))]=client;}
                    await socket.SendAsync(packet.Buffer,server,stop.Token);
                }
            }
        }
        catch(Exception)when(stop.IsCancellationRequested){}
    }
    internal object Snapshot()=>new{toServer=Interlocked.Read(ref toServer),fromServer=Interlocked.Read(ref fromServer),
        initiations=Interlocked.Read(ref initiations),responses=Interlocked.Read(ref responses),
        toServerTransport=Interlocked.Read(ref toServerTransport),fromServerTransport=Interlocked.Read(ref fromServerTransport)};
    public void Dispose(){stop.Cancel();socket.Dispose();}
}
