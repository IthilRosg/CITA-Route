using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace RoutePilot.Agent;

// RFC 1928 UDP ASSOCIATE. The TCP control connection owns the UDP lifetime.
// Only the selected loopback relay is ever contacted; there is no WAN fallback.
internal sealed class SocksUdpAssociation : IDisposable
{
    readonly TcpClient _control;
    readonly UdpClient _udp;
    readonly IPEndPoint _target;
    readonly byte[] _header;
    SocksUdpAssociation(TcpClient control,UdpClient udp,IPEndPoint target)
    {
        _control=control;_udp=udp;_target=target;
        _header=new byte[10];_header[3]=1;target.Address.GetAddressBytes().CopyTo(_header,4);
        BinaryPrimitives.WriteUInt16BigEndian(_header.AsSpan(8),(ushort)target.Port);
    }
    internal static async Task<SocksUdpAssociation> Open(int port,IPEndPoint target,CancellationToken stop)
    {
        if(target.Address.AddressFamily!=AddressFamily.InterNetwork)throw new IOException("Only IPv4 UDP probes are supported.");
        var control=new TcpClient(AddressFamily.InterNetwork){NoDelay=true};
        var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        try
        {
            await control.ConnectAsync(IPAddress.Loopback,port,stop);var stream=control.GetStream();
            await stream.WriteAsync(new byte[]{5,1,0},stop);var hello=new byte[2];await stream.ReadExactlyAsync(hello,stop);
            if(hello[0]!=5 || hello[1]!=0)throw new IOException("SOCKS authentication refused.");
            int local=((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            await stream.WriteAsync(new byte[]{5,3,0,1,127,0,0,1,(byte)(local>>8),(byte)local},stop);
            var reply=new byte[4];await stream.ReadExactlyAsync(reply,stop);
            if(reply[0]!=5 || reply[1]!=0 || reply[2]!=0 || reply[3]!=1)throw new IOException("SOCKS UDP relay unavailable.");
            var bound=new byte[6];await stream.ReadExactlyAsync(bound,stop);
            var address=new IPAddress(bound.AsSpan(0,4));
            if(address.Equals(IPAddress.Any))address=IPAddress.Loopback;
            int relayPort=BinaryPrimitives.ReadUInt16BigEndian(bound.AsSpan(4));
            if(!IPAddress.IsLoopback(address) || relayPort==0)throw new IOException("SOCKS relay must stay on loopback.");
            udp.Connect(new IPEndPoint(address,relayPort));return new(control,udp,target);
        }
        catch{control.Dispose();udp.Dispose();throw;}
    }
    internal async Task Send(byte[] payload,CancellationToken stop)
    {
        if(_control.Client.Poll(0,SelectMode.SelectRead) && _control.Client.Available==0)throw new IOException("SOCKS UDP association closed.");
        byte[] packet=new byte[_header.Length+payload.Length];_header.CopyTo(packet,0);payload.CopyTo(packet,_header.Length);
        await _udp.SendAsync(packet,stop);
    }
    internal async Task<byte[]?> Receive(CancellationToken stop)
    {
        var packet=(await _udp.ReceiveAsync(stop)).Buffer;
        // Reject fragmentation, domain replies, IPv6 and a different reflector.
        if(packet.Length!=10+Quality.UdpProbeProtocol.Size || packet[0]!=0 || packet[1]!=0 || packet[2]!=0 || packet[3]!=1 ||
            !packet.AsSpan(4,4).SequenceEqual(_target.Address.GetAddressBytes()) || BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8))!=_target.Port)return null;
        return packet[10..];
    }
    public void Dispose(){_udp.Dispose();_control.Dispose();}
}
