using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RoutePilot.Quality;

// Fixed-size authenticated echo; request and reply have equal sizes.
public static class UdpProbeProtocol
{
    public const int Size=64;
    public static byte[] Create(byte[] key,byte[] nonce,ulong sequence)
    {
        if(key.Length!=32 || nonce.Length!=16)throw new ArgumentException("Invalid probe key or nonce.");
        var packet=new byte[Size];"CITAUDP1"u8.CopyTo(packet);nonce.CopyTo(packet,8);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(24),sequence);
        HMACSHA256.HashData(key,packet.AsSpan(0,32),packet.AsSpan(32));return packet;
    }
    public static bool Valid(ReadOnlySpan<byte> packet,byte[] key)
    {
        if(packet.Length!=Size || key.Length!=32 || !packet[..8].SequenceEqual("CITAUDP1"u8))return false;
        Span<byte> mac=stackalloc byte[32];HMACSHA256.HashData(key,packet[..32],mac);
        return CryptographicOperations.FixedTimeEquals(mac,packet[32..]);
    }
    public static bool Match(ReadOnlySpan<byte> packet,byte[] key,byte[] nonce,out ulong sequence)
    {
        sequence=0;if(!Valid(packet,key) || !packet.Slice(8,16).SequenceEqual(nonce))return false;
        sequence=BinaryPrimitives.ReadUInt64BigEndian(packet.Slice(24,8));return true;
    }
}
