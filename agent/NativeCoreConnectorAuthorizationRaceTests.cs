using System.Diagnostics;
using System.IO.Pipes;

namespace RoutePilot.Agent;

internal static class NativeCoreConnectorAuthorizationRaceTests
{
    sealed class EmptyConnection:INativeConnectorConnection
    {
        public Stream Stream{get;}=new MemoryStream();
        public void CompleteWrites(){}
        public ValueTask DisposeAsync(){Stream.Dispose();return ValueTask.CompletedTask;}
    }
    sealed class ControlledConnection:INativeConnectorConnection
    {
        internal readonly ControlledStream Duplex=new();
        public Stream Stream=>Duplex;
        public void CompleteWrites()=>Duplex.WriteEnded.TrySetResult();
        public ValueTask DisposeAsync(){Duplex.Dispose();return ValueTask.CompletedTask;}
    }
    sealed class ControlledStream:Stream
    {
        readonly TaskCompletionSource<byte[]> incoming=new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource end=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<byte[]> Written=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource WriteEnded=new(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[]? response;int offset;
        internal void Reply(byte[] data)=>incoming.TrySetResult(data);
        internal void End()=>end.TrySetResult();
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush(){} public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken stop=default)
        {
            response??=await incoming.Task.WaitAsync(stop);
            if(offset<response.Length){int count=Math.Min(buffer.Length,response.Length-offset);response.AsMemory(offset,count).CopyTo(buffer);offset+=count;return count;}
            await end.Task.WaitAsync(stop);return 0;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken stop=default)
        {stop.ThrowIfCancellationRequested();Written.TrySetResult(buffer.ToArray());return ValueTask.CompletedTask;}
        protected override void Dispose(bool disposing){incoming.TrySetCanceled();end.TrySetResult();base.Dispose(disposing);}
    }
    internal static async Task Run()
    {
        if(!OperatingSystem.IsWindows())return;
        using var process=Process.GetCurrentProcess();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int allowed=1;string epoch=Guid.NewGuid().ToString("N");var admission=new NativeConnectorAdmission();admission.Open();
        await using var connector=new NativeCoreConnector(process,epoch,()=>true,_=>Volatile.Read(ref allowed)==1,async(_,token)=>
        {entered.TrySetResult();await release.Task.WaitAsync(token);return new EmptyConnection();},stop.Token,admission);
        try
        {
            connector.Start();using var pipe=new NamedPipeClientStream(".",connector.Endpoint.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous);
            await pipe.ConnectAsync(stop.Token);var request=new NativeConnectorRequest(epoch,connector.Endpoint.Capability,Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),"example.org",Environment.ProcessPath!,"8.8.8.8",443,"tcp");
            await NativeCoreConnectorTests.WriteRequest(pipe,request,stop.Token);await entered.Task.WaitAsync(stop.Token);
            Volatile.Write(ref allowed,0);await admission.CloseAndDrainAsync(stop.Token);release.TrySetResult();
            bool closed=false;try{closed=await pipe.ReadAsync(new byte[1],stop.Token)==0;}catch(IOException){closed=true;}
            if(!closed||connector.Accepted!=0)throw new IOException("Revoked policy was acknowledged after connection setup.");
        }
        finally{release.TrySetResult();}
        // Once ACK completed, publication drains no active-flow lease and
        // must not cancel the established pipe's data transfer.
        var nextAdmission=new NativeConnectorAdmission();nextAdmission.Open();
        var controlled=new ControlledConnection();
        await using var established=new NativeCoreConnector(process,epoch,()=>true,_=>true,(_,_)=>Task.FromResult<INativeConnectorConnection>(controlled),stop.Token,nextAdmission);
        established.Start();
        using(var pipe=new NamedPipeClientStream(".",established.Endpoint.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous))
        {
            await pipe.ConnectAsync(stop.Token);var request=new NativeConnectorRequest(epoch,established.Endpoint.Capability,Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),"example.org",Environment.ProcessPath!,"8.8.8.8",443,"tcp");
            await NativeCoreConnectorTests.WriteRequest(pipe,request,stop.Token);byte[] ack=new byte[1];await pipe.ReadExactlyAsync(ack,stop.Token);
            if(ack[0]!=0)throw new IOException("Ready connection was not acknowledged.");
            while(established.Accepted!=1){stop.Token.ThrowIfCancellationRequested();await Task.Delay(1,stop.Token);}
            await nextAdmission.CloseAndDrainAsync(stop.Token);
            using var frames=new NativeConnectorFrames(pipe,true);byte[] sent=[1,2,3],reply=[4,5,6];
            await frames.WriteAsync(sent,stop.Token);
            if(!(await controlled.Duplex.Written.Task.WaitAsync(stop.Token)).SequenceEqual(sent))throw new IOException("Established flow lost upload after policy change.");
            controlled.Duplex.Reply(reply);byte[] buffer=new byte[3];await frames.ReadExactlyAsync(buffer,stop.Token);
            if(!buffer.SequenceEqual(reply))throw new IOException("Established flow lost download after policy change.");
            await frames.CompleteWritesAsync(stop.Token);await controlled.Duplex.WriteEnded.Task.WaitAsync(stop.Token);
            controlled.Duplex.End();if(await frames.ReadAsync(new byte[1],stop.Token)!=0)throw new IOException("Unexpected synthetic response.");
        }
        // Hold the real connector immediately before its one-byte ACK. A
        // timed-out publisher cannot proceed while setup still owns a lease.
        var ackGate=new NativeConnectorAdmission();ackGate.Open();
        var ackStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ackRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var uncertain=new NativeCoreConnector(process,epoch,()=>true,_=>true,(_,_)=>Task.FromResult<INativeConnectorConnection>(new EmptyConnection()),stop.Token,ackGate,
            async(token)=>{ackStarted.TrySetResult();await ackRelease.Task;});
        try
        {
            uncertain.Start();using var pipe=new NamedPipeClientStream(".",uncertain.Endpoint.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous);
            await pipe.ConnectAsync(stop.Token);var request=new NativeConnectorRequest(epoch,uncertain.Endpoint.Capability,Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),"example.org",Environment.ProcessPath!,"8.8.8.8",443,"tcp");
            await NativeCoreConnectorTests.WriteRequest(pipe,request,stop.Token);await ackStarted.Task.WaitAsync(stop.Token);
            using(var timeout=new CancellationTokenSource(TimeSpan.FromMilliseconds(40)))
            {bool canceled=false;try{await ackGate.CloseAndDrainAsync(timeout.Token);}catch(OperationCanceledException){canceled=true;}if(!canceled)throw new IOException("Unconfirmed ACK incorrectly drained.");}
            if(ackGate.TryEnter()!=null)throw new IOException("A timeout reopened admission.");
            ackRelease.TrySetResult();await ackGate.CloseAndDrainAsync(stop.Token);
            bool closed=false;try{closed=await pipe.ReadAsync(new byte[1],stop.Token)==0;}catch(IOException){closed=true;}
            if(!closed||uncertain.Accepted!=0)throw new IOException("Canceled ACK admitted a flow.");
        }
        finally{ackRelease.TrySetResult();}
        // Expiry while connect is still preparing must not produce an ACK.
        long tick=1,wall=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const string syntheticApp=@"C:\apps\browser.exe";
        var rule=new NativeDomainRule("example.org",syntheticApp,"tcp","zapret",443,["8.8.8.8"],wall+1000);
        var policy=NativeZapretAuthorization.CreateCandidate(epoch,1,[rule],_=>true,wall,tick,()=>Volatile.Read(ref tick));
        var ttlEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var ttlRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ttlGate=new NativeConnectorAdmission();ttlGate.Open();
        await using var ttlConnector=new NativeCoreConnector(process,epoch,()=>true,policy.Allows,async(_,token)=>{ttlEntered.TrySetResult();await ttlRelease.Task.WaitAsync(token);return new EmptyConnection();},stop.Token,ttlGate);
        try
        {
            ttlConnector.Start();using var pipe=new NamedPipeClientStream(".",ttlConnector.Endpoint.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous);
            await pipe.ConnectAsync(stop.Token);var request=new NativeConnectorRequest(epoch,ttlConnector.Endpoint.Capability,Guid.NewGuid().ToString("N"),wall,"example.org",syntheticApp,"8.8.8.8",443,"tcp");
            await NativeCoreConnectorTests.WriteRequest(pipe,request,stop.Token);await ttlEntered.Task.WaitAsync(stop.Token);
            Volatile.Write(ref tick,1001);ttlRelease.TrySetResult();
            bool closed=false;try{closed=await pipe.ReadAsync(new byte[1],stop.Token)==0;}catch(IOException){closed=true;}
            if(!closed||ttlConnector.Accepted!=0)throw new IOException("Expired policy was acknowledged.");
            await ttlGate.CloseAndDrainAsync(stop.Token);
        }
        finally{ttlRelease.TrySetResult();}
        // The same TTL may expire after connect completed but before the
        // actual write begins. No success byte may be sent in that window.
        tick=1;
        var beforeWrite=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var continueWrite=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expiryGate=new NativeConnectorAdmission();expiryGate.Open();
        await using var expiryConnector=new NativeCoreConnector(process,epoch,()=>true,policy.Allows,(_,_)=>Task.FromResult<INativeConnectorConnection>(new EmptyConnection()),stop.Token,expiryGate,
            async(_)=>{beforeWrite.TrySetResult();await continueWrite.Task;});
        try
        {
            expiryConnector.Start();using var pipe=new NamedPipeClientStream(".",expiryConnector.Endpoint.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous);
            await pipe.ConnectAsync(stop.Token);var request=new NativeConnectorRequest(epoch,expiryConnector.Endpoint.Capability,Guid.NewGuid().ToString("N"),wall,"example.org",syntheticApp,"8.8.8.8",443,"tcp");
            await NativeCoreConnectorTests.WriteRequest(pipe,request,stop.Token);await beforeWrite.Task.WaitAsync(stop.Token);
            Volatile.Write(ref tick,1001);continueWrite.TrySetResult();
            bool closed=false;try{closed=await pipe.ReadAsync(new byte[1],stop.Token)==0;}catch(IOException){closed=true;}
            if(!closed||expiryConnector.Accepted!=0)throw new IOException("Expired policy was acknowledged before writing.");
            await expiryGate.CloseAndDrainAsync(stop.Token);
        }
        finally{continueWrite.TrySetResult();}
    }
}
