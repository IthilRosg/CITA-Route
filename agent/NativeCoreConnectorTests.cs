using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace RoutePilot.Agent;

internal static class NativeCoreConnectorTests
{
    static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    internal static async Task Run()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));var stop=deadline.Token;
        byte[] data=Enumerable.Range(0,50001).Select(i=>(byte)(i%251)).ToArray();
        using(var bytes=new MemoryStream())
        {
            using(var writer=new NativeConnectorFrames(bytes,true)){await writer.WriteAsync(data,stop);await writer.CompleteWritesAsync(stop);}
            bytes.Position=0;using var reader=new NativeConnectorFrames(bytes,true);using var received=new MemoryStream();
            await reader.CopyToAsync(received,stop);Assert(data.SequenceEqual(received.ToArray()),"Connector frames must preserve bounded multi-frame bytes and EOF.");
        }
        foreach(int length in new[]{-1,16385})
        {
            byte[] header=new byte[4];BinaryPrimitives.WriteInt32BigEndian(header,length);
            using var bytes=new MemoryStream(header);using var frames=new NativeConnectorFrames(bytes);
            bool refused=false;try{await frames.ReadAsync(new byte[1],stop);}catch(InvalidDataException){refused=true;}
            Assert(refused,"Malformed connector data frame must fail before allocation.");
        }
        string epoch=Guid.NewGuid().ToString("N");int factoryCalls=0;
        using var process=Process.GetCurrentProcess();using var session=new CancellationTokenSource();
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        var remote=Task.Run(async()=>
        {
            using var socket=await listener.AcceptSocketAsync(stop);using var stream=new NetworkStream(socket,ownsSocket:false);
            using var received=new MemoryStream();await stream.CopyToAsync(received,stop);
            Assert(data.SequenceEqual(received.ToArray()),"Directional EOF must deliver the whole upload.");
            // The setup deadline must no longer cancel a successful transfer.
            await Task.Delay(5200,stop);await stream.WriteAsync(data,stop);socket.Shutdown(SocketShutdown.Send);
        },stop);
        await using var server=new NativeCoreConnector(process,epoch,()=>true,
            r=>r.Domain=="example.org"&&r.Application==Environment.ProcessPath&&r.Address=="8.8.8.8",
            async(_,token)=>
            {
                Interlocked.Increment(ref factoryCalls);var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
                try{await socket.ConnectAsync(IPAddress.Loopback,port,token);return new Connection(socket);}
                catch{socket.Dispose();throw;}
            },session.Token);
        server.Start();var endpoint=server.Endpoint;
        using(var abandoned=new NamedPipeClientStream(".",endpoint.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous))
        {await abandoned.ConnectAsync(stop);}
        await Task.Delay(100,stop);
        Assert(server.Rejected<=1&&server.Phase=="Listening","Early EOF must reset the listener without an error hot loop.");
        var request=new NativeConnectorRequest(epoch,endpoint.Capability,Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),"example.org",Environment.ProcessPath!,"8.8.8.8",443,"tcp");
        async Task Refused(NativeConnectorRequest value)
        {
            using var pipe=new NamedPipeClientStream(".",endpoint.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous);
            await pipe.ConnectAsync(stop);await WriteRequest(pipe,value,stop);
            bool refused=false;try{refused=await pipe.ReadAsync(new byte[1],stop)==0;}catch(IOException){refused=true;}
            Assert(refused,"Unauthorized connector request must close without a target dial.");
        }
        await Refused(request with {Capability=new string('0',64)});
        await Refused(request with {Epoch=Guid.NewGuid().ToString("N")});
        await Refused(request with {Domain="other.example"});
        await Refused(request with {IssuedUnixMilliseconds=DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds()});
        Assert(factoryCalls==0,"Invalid connector requests must not reach the connection factory.");
        using(var pipe=new NamedPipeClientStream(".",endpoint.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous))
        {
            await pipe.ConnectAsync(stop);await WriteRequest(pipe,request,stop);byte[] ack=new byte[1];await pipe.ReadExactlyAsync(ack,stop);
            Assert(ack[0]==0,"Connector must acknowledge only the ready connection.");
            using var frames=new NativeConnectorFrames(pipe,true);await frames.WriteAsync(data,stop);await frames.CompleteWritesAsync(stop);
            using var response=new MemoryStream();await frames.CopyToAsync(response,stop);
            Assert(data.SequenceEqual(response.ToArray()),"Half-close must preserve the delayed reverse response beyond setup deadline.");
        }
        await remote;await Refused(request);Assert(factoryCalls==1,"Replayed flow nonce must not create another socket.");
        session.Cancel();await server.DisposeAsync();
        Assert(server.Accepted==1&&!server.CleanupUnconfirmed,"Normal core connector cleanup must be confirmed.");
        Assert(server.Rejected<32,"A rejected or disconnected request must not spin a broken listener.");

        using var job=new NativeProcessJob();
        using var other=job.StartOwned(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoProfile","-NonInteractive","-Command","Start-Sleep -Seconds 20"],Path.GetTempPath());
        await using(var wrongPid=new NativeCoreConnector(other,epoch,()=>true,_=>true,(_,_)=>throw new Exception("Foreign PID admitted"),stop))
        {
            wrongPid.Start();using var pipe=new NamedPipeClientStream(".",wrongPid.Endpoint.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous);
            await pipe.ConnectAsync(stop);bool refused=false;try{refused=await pipe.ReadAsync(new byte[1],stop)==0;}catch(IOException){refused=true;}
            Assert(refused,"Capability is insufficient: actual Windows pipe client PID must match the held core.");
            other.Kill();await other.WaitForExitAsync(stop);
        }
        Console.WriteLine("PASS: private core pipe PID/epoch/capability/scope/replay guards, bounded frames, directional EOF, delayed reverse transfer and cleanup.");
    }
    internal static async Task WriteRequest(Stream pipe,NativeConnectorRequest request,CancellationToken stop)
    {
        byte[] payload=JsonSerializer.SerializeToUtf8Bytes(request),header=new byte[4];BinaryPrimitives.WriteInt32BigEndian(header,payload.Length);
        await pipe.WriteAsync(header.AsMemory(0,2),stop);await pipe.WriteAsync(header.AsMemory(2),stop);
        await pipe.WriteAsync(payload,stop);await pipe.FlushAsync(stop);
    }
    sealed class Connection(Socket socket):INativeConnectorConnection
    {
        public Stream Stream{get;}=new NetworkStream(socket,ownsSocket:false);
        public void CompleteWrites()=>socket.Shutdown(SocketShutdown.Send);
        public ValueTask DisposeAsync(){Stream.Dispose();socket.Dispose();return ValueTask.CompletedTask;}
    }
}
