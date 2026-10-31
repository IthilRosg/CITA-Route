using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RoutePilot.Agent;

internal static class NativeBoundTcpLeaseTests
{
    sealed class Interception:INativeSocketInterception
    {
        internal readonly TaskCompletionSource Ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? RetirementRelease;
        internal bool IgnoreReadyCancellation;
        internal int RetireCalls;
        internal Action? BeforeRetire;
        internal Action<NativeBoundSocketIdentity>? BeforeReady;
        internal Exception? StartupFailure;
        internal bool AllowRetire=true;
        public bool RetirementConfirmed{get;private set;}
        public Task WaitReadyAsync(NativeBoundSocketIdentity socket,CancellationToken stop){Started.TrySetResult();BeforeReady?.Invoke(socket);return StartupFailure!=null?Task.FromException(StartupFailure):IgnoreReadyCancellation?Ready.Task:Ready.Task.WaitAsync(stop);}
        public async Task RetireAsync(CancellationToken stop){Interlocked.Increment(ref RetireCalls);stop.ThrowIfCancellationRequested();BeforeRetire?.Invoke();if(RetirementRelease!=null)await RetirementRelease.Task;RetirementConfirmed=AllowRetire;}
    }
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static bool Exclusive(IPEndPoint endpoint)
    {
        using var other=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp){ExclusiveAddressUse=true};
        try{other.Bind(endpoint);return false;}catch(SocketException error)when(error.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied){return true;}
    }
    internal static async Task Run()
    {
        if(Environment.GetEnvironmentVariable("CITA_SKIP_LOOPBACK_SOCKET_TEST")=="1")
        {
            Console.WriteLine("SKIP: loopback bound-socket integration is blocked by the host firewall (WSAEACCES 10013); CI still runs the full test.");
            return;
        }
        uint index=checked((uint)NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.NetworkInterfaceType==NetworkInterfaceType.Loopback).GetIPProperties().GetIPv4Properties().Index);
        int initial=NativeBoundTcpLease.RetainedCount;
        string epoch=Guid.NewGuid().ToString("N");
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var destination=(IPEndPoint)listener.LocalEndpoint;
        await using(var lease=NativeBoundTcpLease.Bind(IPAddress.Loopback,index,epoch))
        {
            var endpoint=lease.Local;var hook=new Interception();
            var mutableDestination=new IPEndPoint(destination.Address,destination.Port);
            hook.BeforeRetire=()=>Check(Exclusive(endpoint),"The source port was released before interceptor retirement.");
            hook.BeforeReady=identity=>{
                Check(identity.Epoch==epoch&&identity.LocalAddress==endpoint.Address.ToString()&&identity.LocalPort==endpoint.Port&&
                    identity.RemoteAddress==destination.Address.ToString()&&identity.RemotePort==destination.Port&&identity.PhysicalIndex==index,
                    "Connector changed the owned tuple before preparing interception.");
                Check(Exclusive(endpoint),"Source socket is not exclusively bound before interception.");
                mutableDestination.Port=mutableDestination.Port==65535?65534:mutableDestination.Port+1;
            };
            var connected=lease.ConnectAsync(mutableDestination,hook,deadline.Token);
            await Task.Delay(40,deadline.Token);
            Check(!listener.Pending()&&!connected.IsCompleted,"A TCP connect/SYN escaped before interception readiness.");
            hook.Ready.SetResult();
            var accepting=listener.AcceptSocketAsync(deadline.Token).AsTask();
            // Surface a failed outgoing connection immediately instead of
            // masking its cause behind the listener's eventual timeout.
            var first=await Task.WhenAny(accepting,connected);
            if(first==connected&&(connected.IsFaulted||connected.IsCanceled))await connected;
            using var peer=await accepting;
            using var data=await connected;
            await data.WriteAsync(new byte[]{7,8,9},deadline.Token);
            byte[] bytes=new byte[3];using var incoming=new NetworkStream(peer,ownsSocket:false);
            await incoming.ReadExactlyAsync(bytes,deadline.Token);
            Check(bytes.SequenceEqual(new byte[]{7,8,9}),"The connector changed relayed bytes.");
            hook.AllowRetire=false;bool held=false;
            try{await lease.DisposeAsync();}catch(IOException){held=true;}
            Check(held&&lease.Phase=="RetirementUnconfirmed"&&Exclusive(endpoint)&&NativeBoundTcpLease.RetainedCount==initial+1,
                "An unconfirmed interceptor must quarantine its socket and capacity.");
            Check(NativeBoundTcpLease.Quarantined(epoch).Contains(lease.Id),"Quarantine must remain discoverable for owner recovery.");
            bool stale=false;try{await NativeBoundTcpLease.RetryRetirementAsync(Guid.NewGuid().ToString("N"),lease.Id,deadline.Token);}catch(InvalidDataException){stale=true;}
            Check(stale,"A foreign connector epoch recovered another owner's socket.");
            hook.AllowRetire=true;Check(await NativeBoundTcpLease.RetryRetirementAsync(epoch,lease.Id,deadline.Token),"Owner recovery did not confirm retirement.");
            Check(lease.Phase=="Closed"&&NativeBoundTcpLease.RetainedCount==initial,"Confirmed retirement did not release the connector.");
        }
        await using(var lease=NativeBoundTcpLease.Bind(IPAddress.Loopback,index,epoch))
        {
            var endpoint=lease.Local;var hook=new Interception();
            hook.BeforeRetire=()=>Check(Exclusive(endpoint),"Cancelled preparation closed the port before retirement.");
            using var stop=new CancellationTokenSource();
            var connected=lease.ConnectAsync(destination,hook,stop.Token);
            stop.Cancel();bool cancelled=false;
            try{await connected;}catch(OperationCanceledException){cancelled=true;}
            Check(cancelled&&lease.Phase=="Closed"&&hook.RetirementConfirmed&&!listener.Pending(),"Cancellation before readiness must stop interception without a TCP connection.");
        }
        await using(var lease=NativeBoundTcpLease.Bind(IPAddress.Loopback,index,epoch))
        {
            var hook=new Interception{AllowRetire=false,StartupFailure=new IOException("fixture partial startup failure")};var endpoint=lease.Local;bool held=false;
            hook.BeforeRetire=()=>Check(Exclusive(endpoint),"Partial startup lost port ownership.");
            try{await lease.ConnectAsync(destination,hook,deadline.Token);}
            catch(AggregateException){held=true;}
            Check(held&&lease.Phase=="RetirementUnconfirmed"&&Exclusive(endpoint),"Partial startup failure discarded its unretired interceptor.");
            hook.AllowRetire=true;await lease.DisposeAsync();
        }
        await using(var lease=NativeBoundTcpLease.Bind(IPAddress.Loopback,index,epoch))
        {
            using var failure=new CancellationTokenSource();lease.FailWhen(failure.Token);
            var endpoint=lease.Local;var hook=new Interception();hook.Ready.TrySetResult();
            using var peer=await AcceptConnected(lease,listener,destination,hook,deadline.Token);
            failure.Cancel();
            Check(Exclusive(endpoint),"Loss of dynamic permission released the source port before retirement.");
            var peerRead=peer.GetStream().ReadAsync(new byte[1],deadline.Token);
            Check(await peerRead==0,"Lost path did not stop the connected stream.");
            hook.BeforeRetire=()=>Check(Exclusive(endpoint),"Lost-path cleanup lost exclusive port ownership.");
            await lease.DisposeAsync();Check(hook.RetirementConfirmed&&lease.Phase=="Closed","Lost-path shutdown did not complete confirmed retirement.");
        }
        Check(NativeBoundTcpLease.RetainedCount==initial,"Connector tests leaked socket leases.");
        using var recoveryDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using(var lease=NativeBoundTcpLease.Bind(IPAddress.Loopback,index,epoch))
        {
            var endpoint=lease.Local;var hook=new Interception{IgnoreReadyCancellation=true};
            using var stop=new CancellationTokenSource();
            var pending=lease.ConnectAsync(destination,hook,stop.Token);await hook.Started.Task.WaitAsync(recoveryDeadline.Token);stop.Cancel();
            bool quarantined=false;try{await pending;}catch(AggregateException){quarantined=true;}
            Check(quarantined&&lease.Phase=="RetirementUnconfirmed"&&hook.RetireCalls==0&&Exclusive(endpoint),
                "A stuck starter must retain its port without racing an interceptor retirement.");
            hook.Ready.SetResult();
            Check(await NativeBoundTcpLease.RetryRetirementAsync(epoch,lease.Id,recoveryDeadline.Token),"A late completed starter could not be recovered.");
        }
        await using(var lease=NativeBoundTcpLease.Bind(IPAddress.Loopback,index,epoch))
        {
            var endpoint=lease.Local;var hook=new Interception{RetirementRelease=new(TaskCreationOptions.RunContinuationsAsynchronously)};
            hook.Ready.SetResult();
            var pending=lease.ConnectAsync(destination,hook,recoveryDeadline.Token);using var peer=await listener.AcceptSocketAsync(recoveryDeadline.Token);using var stream=await pending;
            bool quarantined=false;try{await lease.DisposeAsync();}catch(TimeoutException){quarantined=true;}
            Check(quarantined&&hook.RetireCalls==1&&Exclusive(endpoint),"A stuck retirement must time out without releasing or duplicating ownership.");
            hook.RetirementRelease.SetResult();
            Check(await NativeBoundTcpLease.RetryRetirementAsync(epoch,lease.Id,recoveryDeadline.Token)&&hook.RetireCalls==1,
                "Recovery launched a duplicate retirement instead of awaiting the existing attempt.");
        }
        Check(NativeBoundTcpLease.RetainedCount==initial,"Quarantine recovery retained a completed socket.");
        var saturated=new List<NativeBoundTcpLease>();
        try
        {
            for(int i=initial;i<64;i++)saturated.Add(NativeBoundTcpLease.Bind(IPAddress.Loopback,index,epoch));
            bool bounded=false;try{await using var extra=NativeBoundTcpLease.Bind(IPAddress.Loopback,index,epoch);}catch(IOException){bounded=true;}
            Check(bounded,"Connector admission exceeded its bounded socket capacity.");
        }
        finally{foreach(var lease in saturated)await lease.DisposeAsync();}
        Check(NativeBoundTcpLease.RetainedCount==initial,"Capacity-pressure test retained sockets.");
        Console.WriteLine("PASS: real bound TCP ownership, no connect before readiness, byte relay, cancel-before-connect and quarantine until confirmed interception retirement.");
    }
    static async Task<TcpClient> AcceptConnected(NativeBoundTcpLease lease,TcpListener listener,IPEndPoint destination,Interception hook,CancellationToken stop)
    {
        var accepting=listener.AcceptTcpClientAsync(stop);await lease.ConnectAsync(destination,hook,stop);return await accepting;
    }
}
