using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace RoutePilot.Agent;

internal sealed record NativeConnectorRequest(string Epoch,string Capability,string Nonce,long IssuedUnixMilliseconds,
    string Domain,string Application,string Address,ushort Port,string Network);
internal sealed record NativeConnectorEndpoint(string Pipe,string Epoch,string Capability,int ServerPid);
internal interface INativeConnectorConnection:IAsyncDisposable { Stream Stream{get;} void CompleteWrites(); }

// Private core data plane. The controller owns the process handle, current
// policy predicate and connection factory. Never exposed through user RPC.
internal sealed class NativeCoreConnector:IAsyncDisposable
{
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint pid);
    static readonly JsonSerializerOptions json=new(){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=4};
    readonly Process core;
    readonly Func<bool> current;
    readonly Func<NativeConnectorRequest,bool> authorized;
    readonly Func<NativeConnectorRequest,CancellationToken,Task<INativeConnectorConnection>> connect;
    readonly NativeConnectorAdmission? admission;
    readonly Func<CancellationToken,Task>? beforeAck;
    readonly CancellationTokenSource stop;
    readonly byte[] capability=RandomNumberGenerator.GetBytes(32);
    readonly string epoch,name="Cita.CoreConnector."+Guid.NewGuid().ToString("N");
    readonly Dictionary<string,long> seen=new(StringComparer.Ordinal);
    readonly object nonceGate=new();
    readonly SemaphoreSlim disposalGate=new(1,1);
    readonly List<NamedPipeServerStream> pipes=new();
    readonly List<Task> workers=new();
    Task? processMonitor;
    int closed,accepted,rejected;
    int cleanupUnconfirmed,disposed;
    internal bool CleanupUnconfirmed=>Volatile.Read(ref cleanupUnconfirmed)!=0;
    string phase="Created";
    internal string Phase=>Volatile.Read(ref phase);
    internal bool IsListening
    {get{try{return Phase=="Listening"&&!CleanupUnconfirmed&&Current()&&workers.Count==16&&workers.All(t=>!t.IsCompleted);}catch(Exception error)when(error is InvalidOperationException or ObjectDisposedException){return false;}}}
    internal NativeConnectorEndpoint Endpoint=>new(name,epoch,Convert.ToHexString(capability),Environment.ProcessId);
    internal int Accepted=>Volatile.Read(ref accepted);
    internal int Rejected=>Volatile.Read(ref rejected);

    internal NativeCoreConnector(Process trustedCore,string epoch,Func<bool> current,
        Func<NativeConnectorRequest,bool> authorized,
        Func<NativeConnectorRequest,CancellationToken,Task<INativeConnectorConnection>> connect,CancellationToken sessionStop,NativeConnectorAdmission? admission=null,Func<CancellationToken,Task>? beforeAck=null)
    {
        if(!OperatingSystem.IsWindows()||!Hex(epoch,32))throw new InvalidDataException("Current Windows core epoch required.");
        this.epoch=epoch;this.current=current;this.authorized=authorized;this.connect=connect;this.admission=admission;this.beforeAck=beforeAck;
        // Own an independent held handle; caller disposal/PID reuse cannot
        // turn a different process into this channel's authorized core.
        core=Process.GetProcessById(trustedCore.Id);
        try
        {
            _=core.Handle;
            if(core.HasExited||trustedCore.HasExited||core.StartTime!=trustedCore.StartTime)throw new IOException("Core process identity changed.");
            stop=CancellationTokenSource.CreateLinkedTokenSource(sessionStop);
        }
        catch{core.Dispose();throw;}
    }
    bool Current()=>Volatile.Read(ref closed)==0&&!stop.IsCancellationRequested&&!core.HasExited&&current();
    internal void Start()
    {
        if(pipes.Count!=0||!Current())throw new InvalidOperationException("Core connector cannot start.");
        using var identity=WindowsIdentity.GetCurrent();
        var user=identity.User??throw new IOException("Connector identity unavailable.");
        var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user,PipeAccessRights.FullControl,AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),PipeAccessRights.FullControl,AccessControlType.Allow));
        try
        {
            // Keep all instances alive until Stop: there is never a gap in
            // pipe namespace ownership between consecutive application flows.
            for(int i=0;i<16;i++)pipes.Add(NamedPipeServerStreamAcl.Create(name,PipeDirection.InOut,16,
                PipeTransmissionMode.Byte,PipeOptions.Asynchronous|(i==0?PipeOptions.FirstPipeInstance:0),16384,16384,security));
            foreach(var pipe in pipes)workers.Add(Serve(pipe));
            processMonitor=ObserveCore();
            Volatile.Write(ref phase,"Listening");
        }
        catch{stop.Cancel();foreach(var pipe in pipes)pipe.Dispose();throw;}
    }
    async Task ObserveCore()
    {
        try{await core.WaitForExitAsync(stop.Token);stop.Cancel();}
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
    }
    static bool Hex(string? value,int length)=>value?.Length==length&&value.All(Uri.IsHexDigit);
    bool Claim(NativeConnectorRequest request)
    {
        if(request.Epoch!=epoch||!Hex(request.Capability,64)||!Hex(request.Nonce,32)||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(request.Capability),capability)||
            request.Port!=443||request.Network!="tcp"||string.IsNullOrWhiteSpace(request.Application)||request.Application.Length>512||
            request.Domain is not {Length:>0 and <=253}||request.Address is not {Length:>0 and <=15})return false;
        long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if(request.IssuedUnixMilliseconds<now-30000||request.IssuedUnixMilliseconds>now+2000||!Current())return false;
        lock(nonceGate)
        {
            long monotonic=Environment.TickCount64;
            foreach(var key in seen.Where(pair=>monotonic-pair.Value>120000).Select(pair=>pair.Key).ToArray())seen.Remove(key);
            string nonce=request.Nonce.ToUpperInvariant();
            if(seen.Count>=4096||seen.ContainsKey(nonce)||!authorized(request)||!Current())return false;
            seen.Add(nonce,monotonic);return true;
        }
    }
    async Task Serve(NamedPipeServerStream pipe)
    {
        while(!stop.IsCancellationRequested)
        {
            bool connected=false;
            try
            {
                await pipe.WaitForConnectionAsync(stop.Token);
                connected=true;
                using var setup=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);setup.CancelAfter(TimeSpan.FromSeconds(5));
                if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out uint pid)||pid!=core.Id||!Current())
                    throw new UnauthorizedAccessException("Connector caller refused.");
                var request=await ReadRequest(pipe,setup.Token);
                if(!Claim(request))throw new UnauthorizedAccessException("Connector scope refused.");
                using var admissionLease=admission?.TryEnter();
                if(admission!=null&&admissionLease==null)throw new UnauthorizedAccessException("Connector admission closed.");
                using var admittedSetup=admissionLease==null?CancellationTokenSource.CreateLinkedTokenSource(setup.Token):CancellationTokenSource.CreateLinkedTokenSource(setup.Token,admissionLease.Cancellation);
                await using var connection=await connect(request,admittedSetup.Token);
                // A revocation cancels setup and waits for this lease through
                // the completed ACK before a new policy can be published.
                admittedSetup.Token.ThrowIfCancellationRequested();if(!Current()||!authorized(request))throw new IOException("Core connector authorization changed.");
                if(beforeAck!=null)await beforeAck(admittedSetup.Token);
                admittedSetup.Token.ThrowIfCancellationRequested();if(!Current()||!authorized(request))throw new IOException("Core connector authorization expired before ACK.");
                // Expiry may race the OS write itself: a peer might receive
                // this byte before the post-write check closes the stream. A
                // policy change is only committed after admission drains.
                await pipe.WriteAsync(new byte[]{0},admittedSetup.Token);
                admittedSetup.Token.ThrowIfCancellationRequested();if(!Current()||!authorized(request))throw new IOException("Core connector authorization changed during ACK.");
                admissionLease?.Dispose();
                setup.CancelAfter(Timeout.InfiniteTimeSpan);
                Interlocked.Increment(ref accepted);
                using var transfer=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                using var frames=new NativeConnectorFrames(pipe,leaveOpen:true);
                async Task Outgoing(){await frames.CopyToAsync(connection.Stream,16384,transfer.Token);connection.CompleteWrites();}
                async Task Incoming(){await connection.Stream.CopyToAsync(frames,16384,transfer.Token);await frames.CompleteWritesAsync(transfer.Token);}
                async Task Pump(Func<Task> direction){try{await direction();}catch{transfer.Cancel();throw;}}
                // An ordinary EOF is directional. Preserve the opposite side
                // until its own EOF; only errors/Stop cancel both directions.
                await Task.WhenAll(Pump(Outgoing),Pump(Incoming));
            }
            catch(NativeConnectorRetirementException){Interlocked.Exchange(ref cleanupUnconfirmed,1);stop.Cancel();Interlocked.Increment(ref rejected);}
            catch(Exception error)when(error is IOException or OperationCanceledException or UnauthorizedAccessException or
                JsonException or InvalidDataException or ArgumentException or ObjectDisposedException or System.ComponentModel.Win32Exception)
            {
                if(!stop.IsCancellationRequested)Interlocked.Increment(ref rejected);
                // A failed listener cannot be retried in a synchronous hot
                // loop on the same broken instance. Retire admission instead.
                if(!connected&&!stop.IsCancellationRequested){Volatile.Write(ref phase,"ListenerFailed");stop.Cancel();}
            }
            finally
            {
                // Read EOF can set IsConnected=false while the server instance
                // still MUST Disconnect before its next WaitForConnection.
                if(connected){try{pipe.Disconnect();}catch(IOException){stop.Cancel();}catch(ObjectDisposedException){stop.Cancel();}}
            }
        }
    }
    internal static async Task<NativeConnectorRequest> ReadRequest(Stream stream,CancellationToken stop)
    {
        byte[] header=new byte[4];await stream.ReadExactlyAsync(header,stop);
        int size=BinaryPrimitives.ReadInt32BigEndian(header);if(size is <1 or >4096)throw new InvalidDataException("Connector frame budget exceeded.");
        byte[] payload=new byte[size];await stream.ReadExactlyAsync(payload,stop);
        return JsonSerializer.Deserialize<NativeConnectorRequest>(payload,json)??throw new InvalidDataException("Empty connector request.");
    }
    public async ValueTask DisposeAsync()
    {
        await disposalGate.WaitAsync();
        try
        {
            if(Volatile.Read(ref disposed)==0)
            {
                Interlocked.Exchange(ref closed,1);stop.Cancel();Volatile.Write(ref phase,"Stopping");
                // A timeout retains the SAME workers and IPC handles. Calling
                // DisposeAsync again retries observation, never duplicates teardown.
                try{await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(12));}
                catch{Volatile.Write(ref phase,"RetirementPending");throw;}
                if(processMonitor!=null)await processMonitor;
                foreach(var pipe in pipes)pipe.Dispose();core.Dispose();stop.Dispose();Volatile.Write(ref disposed,1);
            }
            Volatile.Write(ref phase,CleanupUnconfirmed?"SocketQuarantined":"Closed");
            if(CleanupUnconfirmed)throw new IOException("Connector socket remains quarantined; recovery is required.");
        }
        finally{disposalGate.Release();}
    }
}
