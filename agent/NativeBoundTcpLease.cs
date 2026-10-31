using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace RoutePilot.Agent;

internal interface INativeSocketInterception
{
    // Must confirm the installed kernel filter for this socket, not merely
    // that a child process is alive. No connect/SYN is permitted before this.
    Task WaitReadyAsync(NativeBoundSocketIdentity socket,CancellationToken stop);
    Task RetireAsync(CancellationToken stop);
    bool RetirementConfirmed{get;}
}
internal sealed record NativeBoundSocketIdentity(Guid Id,string Epoch,string LocalAddress,ushort LocalPort,string RemoteAddress,ushort RemotePort,uint PhysicalIndex);

// The connector owns the socket itself: there is no reserve-close-rebind gap
// and no dependency on Go's before-bind Control callback. A private authorized
// core connector can relay encrypted bytes without terminating application TLS.
// Integration with that connector and actual WinDivert readiness remains a gate.
internal sealed class NativeBoundTcpLease:IAsyncDisposable
{
    static readonly SemaphoreSlim capacity=new(64,64);
    // A failed retirement must not allow GC/finalizers to release/reuse a port
    // still selected by an old interceptor. Quarantine is bounded by capacity.
    static readonly ConcurrentDictionary<Guid,NativeBoundTcpLease> retained=new();
    readonly Socket socket;
    readonly SemaphoreSlim gate=new(1,1);
    readonly CancellationTokenSource lifetime=new();
    CancellationTokenRegistration failureRegistration;
    readonly Guid id=Guid.NewGuid();
    readonly uint physicalIndex;
    readonly string epoch;
    INativeSocketInterception? interception;
    NetworkStream? stream;
    Task? connecting;
    Task? preparing;
    long connectTimestamp;
    Task<bool>? retiring;
    CancellationTokenSource? retireDeadline;
    string phase="Bound";
    NativeBoundTcpLease(Socket socket,uint physicalIndex,string epoch){this.socket=socket;this.physicalIndex=physicalIndex;this.epoch=epoch;}
    internal string Phase=>Volatile.Read(ref phase);
    internal static int RetainedCount=>retained.Count;
    internal IPEndPoint Local{get{var endpoint=(IPEndPoint)socket.LocalEndPoint!;return new(IPAddress.Parse(endpoint.Address.ToString()),endpoint.Port);}}
    internal Guid Id=>id;
    internal void FailWhen(CancellationToken failure)
    {
        if(Phase!="Bound")throw new IOException("Register flow failure before admission.");
        failureRegistration=failure.Register(()=>{
            try{lifetime.Cancel();}catch(AggregateException){}
            // Shutdown interrupts application I/O without closing/reusing the
            // exclusive source port before interception retirement completes.
            try{socket.Shutdown(SocketShutdown.Both);}catch(Exception error)when(error is SocketException or ObjectDisposedException){}
        });
    }
    internal long ConnectTimestamp=>Interlocked.Read(ref connectTimestamp);
    internal void CompleteWrites()
    {
        if(Phase!="Connected")throw new IOException("Socket is not connected.");
        socket.Shutdown(SocketShutdown.Send);
    }
    internal static NativeBoundTcpLease Bind(IPAddress local,uint physicalIndex,string epoch)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(epoch,"^[a-f0-9]{32}$"))throw new InvalidDataException("A current connector epoch is required.");
        if(!OperatingSystem.IsWindows()||local.AddressFamily!=AddressFamily.InterNetwork||local.Equals(IPAddress.Any)||physicalIndex is 0 or >int.MaxValue)
            throw new InvalidDataException("An explicit Windows IPv4 interface is required.");
        if(!capacity.Wait(0))throw new IOException("Bound socket budget exhausted.");
        Socket? socket=null;
        try
        {
            socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp){ExclusiveAddressUse=true,NoDelay=true};
            // Windows IP_UNICAST_IF is 31; its DWORD is in network byte order.
            // The loopback provider rejects this option with WSAEACCES even
            // when the index is valid. Loopback is used only by bounded tests;
            // production physical uplinks retain explicit interface binding.
            if(!IPAddress.IsLoopback(local))socket.SetSocketOption(SocketOptionLevel.IP,(SocketOptionName)31,IPAddress.HostToNetworkOrder(checked((int)physicalIndex)));
            socket.Bind(new IPEndPoint(local,0));
            var lease=new NativeBoundTcpLease(socket,physicalIndex,epoch);
            if(!retained.TryAdd(lease.id,lease))throw new IOException("Socket identity collision.");
            return lease;
        }
        catch{socket?.Dispose();capacity.Release();throw;}
    }
    internal async Task<NetworkStream> ConnectAsync(IPEndPoint destination,
        INativeSocketInterception interception,CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(interception);
        if(destination.AddressFamily!=AddressFamily.InterNetwork||destination.Port==0)throw new InvalidDataException("An explicit IPv4 TCP destination is required.");
        // IPEndPoint/IPAddress are mutable. Neither the caller nor the readiness
        // adapter may retarget the connect after its filter has been prepared.
        var remote=new IPEndPoint(IPAddress.Parse(destination.Address.ToString()),destination.Port);
        await gate.WaitAsync(stop);
        try
        {
            if(Phase!="Bound")throw new InvalidOperationException("A bound socket lease is single-use.");
            using var operation=CancellationTokenSource.CreateLinkedTokenSource(stop,lifetime.Token);
            operation.Token.ThrowIfCancellationRequested();
            Volatile.Write(ref phase,"Preparing");
            // Retain the resource owner before invoking any startup code. Even
            // an ordinary startup exception must retire that same owner.
            this.interception=interception;
            try
            {
                var local=Local;
                var identity=new NativeBoundSocketIdentity(id,epoch,local.Address.ToString(),checked((ushort)local.Port),remote.Address.ToString(),checked((ushort)remote.Port),physicalIndex);
                var cancellation=operation.Token;
                preparing=Task.Run(()=>interception.WaitReadyAsync(identity,cancellation));
                await preparing.WaitAsync(TimeSpan.FromSeconds(5),operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                Volatile.Write(ref phase,"Connecting");
                // .NET cancellation of ConnectAsync closes the socket. Wait
                // cancellation must instead retire the interceptor FIRST;
                // only StopLocked may release this exclusively bound port.
                Interlocked.Exchange(ref connectTimestamp,System.Diagnostics.Stopwatch.GetTimestamp());
                connecting=socket.ConnectAsync(remote);
                _=connecting.ContinueWith(task=>{_ = task.Exception;},TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously);
                await connecting.WaitAsync(operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                stream=new NetworkStream(socket,ownsSocket:false);
                Volatile.Write(ref phase,"Connected");
                return stream;
            }
            catch(Exception error)
            {
                error.Data["NativeSocketPhase"]=Phase;
                error.Data["NativeSocketPort"]=Local.Port;
                try{lifetime.Cancel();}catch(AggregateException){}
                try{await StopLocked();}
                catch(Exception cleanup){throw new AggregateException("Socket remains quarantined after unconfirmed interception retirement.",error,cleanup);}
                throw;
            }
        }
        finally{gate.Release();}
    }
    async Task StopLocked()
    {
        if(Phase=="Closed")return;
        Volatile.Write(ref phase,"Stopping");
        try
        {
            if(interception!=null)
            {
                // Never retire in parallel with an unfinished startup: a late
                // starter could reopen a filter after its port was released.
                if(preparing is {IsCompleted:false})
                {
                    try{await preparing.WaitAsync(TimeSpan.FromSeconds(5));}
                    catch when(preparing.IsCompleted){}
                }
                if(preparing?.IsFaulted==true)_=preparing.Exception;
                if(retiring==null||retiring.IsCompleted)
                {
                    // Reuse a successful ACK; failed attempts may retry, but
                    // pending attempts are never duplicated by recovery.
                    if(retiring?.Status!=TaskStatus.RanToCompletion||!retiring.Result)
                    {
                        retireDeadline?.Dispose();retireDeadline=new(TimeSpan.FromSeconds(5));
                        retiring=Task.Run(async()=>{await interception.RetireAsync(retireDeadline.Token);return interception.RetirementConfirmed;});
                    }
                }
                if(!await retiring!.WaitAsync(TimeSpan.FromSeconds(5)))throw new IOException("Interception retirement is unconfirmed.");
            }
        }
        catch{Volatile.Write(ref phase,"RetirementUnconfirmed");throw;}
        stream?.Dispose();socket.Dispose();
        Volatile.Write(ref phase,"Closed");
        if(retained.TryRemove(id,out _))capacity.Release();
        failureRegistration.Dispose();lifetime.Dispose();
        retireDeadline?.Dispose();
    }
    internal static Guid[] Quarantined(string epoch)=>retained.Values.Where(lease=>lease.epoch==epoch&&lease.Phase=="RetirementUnconfirmed").Select(lease=>lease.id).ToArray();
    internal static async Task<bool> RetryRetirementAsync(string epoch,Guid id,CancellationToken stop)
    {
        if(!retained.TryGetValue(id,out var lease))return true;
        if(lease.epoch!=epoch)throw new InvalidDataException("Connector recovery epoch mismatch.");
        await lease.gate.WaitAsync(stop);
        try
        {
            if(lease.Phase is not ("RetirementUnconfirmed" or "Closed"))return false;
            await lease.StopLocked();return lease.Phase=="Closed";
        }
        finally{lease.gate.Release();}
    }
    public async ValueTask DisposeAsync()
    {
        if(Phase=="Closed")return;
        try{lifetime.Cancel();}catch(ObjectDisposedException){}
        await gate.WaitAsync();
        try{await StopLocked();}
        finally{gate.Release();}
    }
}
