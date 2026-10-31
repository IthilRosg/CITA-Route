using System.Buffers.Binary;

namespace RoutePilot.Agent;

// V1 private connector data frames: 4-byte big-endian length, 1..16384 data,
// length 0 = directional EOF. Never buffer an unbounded application payload.
internal sealed class NativeConnectorFrames(Stream transport,bool leaveOpen=false):Stream
{
    readonly SemaphoreSlim writer=new(1,1);
    int remaining;
    bool readClosed,writeClosed;
    public override bool CanRead=>true;
    public override bool CanWrite=>true;
    public override bool CanSeek=>false;
    public override long Length=>throw new NotSupportedException();
    public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken stop=default)
    {
        if(buffer.Length==0||readClosed)return 0;
        if(remaining==0)
        {
            byte[] header=new byte[4];await transport.ReadExactlyAsync(header,stop);
            remaining=BinaryPrimitives.ReadInt32BigEndian(header);
            if(remaining is <0 or >16384)throw new InvalidDataException("Connector data frame exceeds budget.");
            if(remaining==0){readClosed=true;return 0;}
        }
        int count=await transport.ReadAsync(buffer[..Math.Min(buffer.Length,remaining)],stop);
        if(count==0)throw new EndOfStreamException("Truncated connector data frame.");remaining-=count;return count;
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken stop=default)
    {
        await writer.WaitAsync(stop);
        try
        {
            if(writeClosed)throw new IOException("Connector write direction is closed.");
            byte[] header=new byte[4];
            while(buffer.Length>0)
            {
                int count=Math.Min(buffer.Length,16384);BinaryPrimitives.WriteInt32BigEndian(header,count);
                await transport.WriteAsync(header,stop);await transport.WriteAsync(buffer[..count],stop);buffer=buffer[count..];
            }
        }
        finally{writer.Release();}
    }
    internal async Task CompleteWritesAsync(CancellationToken stop)
    {
        await writer.WaitAsync(stop);
        try{if(writeClosed)return;writeClosed=true;await transport.WriteAsync(new byte[4],stop);await transport.FlushAsync(stop);}
        finally{writer.Release();}
    }
    public override int Read(byte[] buffer,int offset,int count)=>ReadAsync(buffer.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer,int offset,int count)=>WriteAsync(buffer.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
    public override void Flush()=>transport.Flush();
    public override Task FlushAsync(CancellationToken stop)=>transport.FlushAsync(stop);
    protected override void Dispose(bool disposing){if(disposing&&!leaveOpen)transport.Dispose();base.Dispose(disposing);}
    public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
    public override void SetLength(long length)=>throw new NotSupportedException();
}
