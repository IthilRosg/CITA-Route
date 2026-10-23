using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace RoutePilot.Agent;

internal sealed class PolicyFirstUse:IAsyncDisposable
{
    internal sealed record Request(int Version,string Nonce,int RouterPid,string Domain,string Application,string Mac);
    sealed record Reply(int Version,string Nonce,int RouterPid,string Route,string Epoch,string Mac);
    sealed record Message(Request? Request,TaskCompletionSource<string>? Reply,PolicyKey? Key,int Generation,CandidateTiming? Proof,long Started,int Transport=0);
    sealed record Waiter(Request Request,TaskCompletionSource<string> Reply,long Started);
    readonly PolicyWorker _worker;
    readonly string _secret;
    readonly ProcessIdentity _router;
    readonly TcpListener _listener;
    readonly CancellationTokenSource _stop=new();
    readonly Channel<Message> _queue=Channel.CreateBounded<Message>(64);
    readonly Dictionary<PolicyKey,List<Waiter>> _waiting=new();
    readonly Dictionary<PolicyKey,HashSet<string>> _proofs=new();
    readonly Dictionary<PolicyKey,long> _proofAt=new();
    readonly HashSet<PolicyKey> _refused=new();
    readonly Queue<long> _latency=new();
    readonly Queue<object> _recent=new();
    readonly SemaphoreSlim _slots=new(32);
    readonly List<Task> _clients=new();
    readonly Task _server;
    Task? _signal;
    int _seenGeneration=-1,_seenTransport=-1;
    internal bool HasWaiting=>_waiting.Count>0;
    internal PolicyKey? Take(Func<PolicyKey,bool> eligible)
    {
        foreach(var pair in _waiting.OrderBy(p=>p.Value.Min(w=>w.Started)))
            if(eligible(pair.Key)&&_worker.FirstUseTake(pair.Key))return pair.Key;
        return null;
    }
    internal int Accepted{get;private set;}
    internal int Proven{get;private set;}
    internal int TimedOut{get;private set;}
    internal int DefaultSettled{get;private set;}
    internal int ProvisionalVpn{get;private set;}
    internal int Port=>((IPEndPoint)_listener.LocalEndpoint).Port;
    internal object Snapshot()
    {
        var times=_latency.Order().ToArray();
        return new{enabled=true,accepted=Accepted,proven=Proven,timedOut=TimedOut,defaultSettled=DefaultSettled,provisionalVpn=ProvisionalVpn,pending=_waiting.Values.Sum(v=>v.Count),attribution="Controller replies, not per-flow delivery proof",
            samples=times.Length,meanMs=times.Length==0?0:times.Average(),p50Ms=times.Length==0?0:times[(times.Length-1)/2],
            p95Ms=times.Length==0?0:times[(int)Math.Ceiling(times.Length*.95)-1],maxMs=times.Length==0?0:times[^1],recent=_recent.ToArray()};
    }
    internal Task Signal=>_signal??=_queue.Reader.WaitToReadAsync(_stop.Token).AsTask();
    internal PolicyFirstUse(PolicyWorker worker,string root,PolicySession session,int port)
    {
        _worker=worker;_secret=File.ReadAllText(Path.Combine(root,"private/controller-token.txt")).Trim();
        if(_secret.Length!=64||!_secret.All(Uri.IsHexDigit))throw new IOException("Invalid first-use credential.");
        _router=ProcessIdentity.From(System.Diagnostics.Process.GetProcessById(session.RouterPid));
        _listener=new(IPAddress.Loopback,port);_listener.Start(32);_server=Serve();
    }
    internal static string Sign(string secret,string text)=>Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),Encoding.UTF8.GetBytes(text)));
    internal static string RequestText(Request r)=>$"1\n{r.Nonce}\n{r.RouterPid}\n{r.Domain}\n{r.Application}";
    internal static bool Authentic(string secret,Request r)
    {
        if(r.Version!=1||r.Nonce==null||r.Nonce.Length!=32||!r.Nonce.All(Uri.IsHexDigit)||r.RouterPid<=0||
            r.Domain==null||PolicySafety.Domain(r.Domain)!=r.Domain||r.Application==null||r.Application.Length>1024||
            r.Application.Any(char.IsControl)||!Path.IsPathFullyQualified(r.Application)||r.Mac==null||r.Mac.Length!=64)return false;
        try{return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(r.Mac),Convert.FromHexString(Sign(secret,RequestText(r))));}
        catch(FormatException){return false;}
    }
    async Task Serve()
    {
        try
        {
            while(!_stop.IsCancellationRequested)
            {
                var client=await _listener.AcceptTcpClientAsync(_stop.Token);
                if(!_slots.Wait(0)){client.Dispose();continue;}
                _clients.RemoveAll(t=>t.IsCompleted);_clients.Add(Handle(client));
            }
        }
        catch(Exception ex)when(ex is OperationCanceledException or SocketException or ObjectDisposedException){}
    }
    async Task Handle(TcpClient client)
    {
        using(client)
        using(var budget=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            budget.CancelAfter(2400);
            try
            {
                client.NoDelay=true;var stream=client.GetStream();
                byte[] bytes=new byte[4096];int count=0;
                using(var read=CancellationTokenSource.CreateLinkedTokenSource(budget.Token))
                {
                    read.CancelAfter(250);
                    while(count<bytes.Length)
                    {
                        int n=await stream.ReadAsync(bytes.AsMemory(count),read.Token);if(n==0)return;
                        int end=Array.IndexOf(bytes,(byte)10,count,n);count+=n;
                        if(end>=0){count=end+1;break;}
                    }
                }
                if(count==bytes.Length)return;
                var request=JsonSerializer.Deserialize<Request>(bytes.AsSpan(0,count),Storage.Wire);
                if(request==null||!Authentic(_secret,request)||request.RouterPid!=_router.Pid||!_router.Matches())return;
                var reply=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                if(!_queue.Writer.TryWrite(new(request,reply,null,0,null,Environment.TickCount64)))return;
                string route;
                try{route=await reply.Task.WaitAsync(budget.Token);}finally{reply.TrySetCanceled();}
                string epoch=_worker.FirstUseEpoch;
                string mac=Sign(_secret,$"1\n{request.Nonce}\n{request.RouterPid}\n{route}\n{epoch}");
                byte[] output=JsonSerializer.SerializeToUtf8Bytes(new Reply(1,request.Nonce,request.RouterPid,route,epoch,mac),Storage.Wire);
                await stream.WriteAsync(output,budget.Token);await stream.WriteAsync(new byte[]{10},budget.Token);
            }
            catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException or JsonException or ArgumentException){}
            finally{_slots.Release();}
        }
    }
    internal void Candidate(PolicyKey key,int generation,int transport,CandidateTiming proof)
    {if(proof.Outcome==ProbeOutcome.TlsAuthenticationError||proof.Outcome==ProbeOutcome.Reachable&&proof.Evidence is "BodyComplete" or "BodySample")_queue.Writer.TryWrite(new(null,null,key,generation,proof,Environment.TickCount64,transport));}
    internal void Pump()
    {
        if(_signal?.IsCompleted==true)_signal=null;long tick=Environment.TickCount64;
        if(_waiting.Count==0&&!_queue.Reader.TryPeek(out _))return;
        bool current=_worker.Current();
        int transport=_worker.FirstUseTransport;
        if(_seenGeneration!=_worker.FirstUseGeneration||_seenTransport!=transport)
        {
            foreach(var group in _waiting.Values)foreach(var waiter in group)waiter.Reply.TrySetResult("");
            _waiting.Clear();_proofs.Clear();_proofAt.Clear();_refused.Clear();_seenGeneration=_worker.FirstUseGeneration;_seenTransport=transport;
        }
        foreach(var key in _proofAt.Where(p=>tick<p.Value||tick-p.Value>10000).Select(p=>p.Key).ToArray()){_proofAt.Remove(key);_proofs.Remove(key);}
        while(_queue.Reader.TryRead(out var message))
        {
            if(message.Request is {} request&&message.Reply is {} reply)
            {
                var key=new PolicyKey(_worker.FirstUseEpoch,request.Domain,request.Application,443);
                if(reply.Task.IsCompleted||!current||!_worker.FirstUseEligible(key,false)){reply.TrySetResult("");continue;}
                if(!_waiting.TryGetValue(key,out var waiters))_waiting[key]=waiters=new();
                if(waiters.Count>=16){reply.TrySetResult("");continue;}
                waiters.Add(new(request,reply,message.Started));Accepted++;
                _worker.FirstUseDiscover(key);
            }
            else if(message.Key is {} key&&message.Proof is {} proof&&message.Generation==_worker.FirstUseGeneration&&message.Transport==transport&&current&&_worker.FirstUseEligible(key,false))
            {
                if(proof.Outcome==ProbeOutcome.TlsAuthenticationError){_proofs.Remove(key);_proofAt.Remove(key);if(_waiting.ContainsKey(key))_refused.Add(key);continue;}
                if(!_proofs.TryGetValue(key,out var routes))_proofs[key]=routes=new();routes.Add(proof.Route);_proofAt[key]=message.Started;
                while(_proofAt.Count>256){var old=_proofAt.MinBy(p=>p.Value).Key;_proofAt.Remove(old);_proofs.Remove(old);}
            }
        }
        foreach(var pair in _waiting.ToArray())
        {
            bool eligible=current&&!_refused.Contains(pair.Key)&&_worker.FirstUseEligible(pair.Key,false);
            bool settled=eligible&&_worker.FirstUseDefaultSettled(pair.Key);
            string? route=eligible?_worker.FirstUseApplied(pair.Key):null;
            if(route==null&&eligible&&_proofs.TryGetValue(pair.Key,out var routes))
                route=new[]{"direct","dpi","vpn"}.FirstOrDefault(r=>routes.Contains(r)&&!_worker.AvoidedRoutes(pair.Key,DateTime.UtcNow).Contains(r));
            bool? vpnReady=null;
            foreach(var waiter in pair.Value.ToArray())
            {
                // A request can arrive while Current() checks the session, after
                // the Pump entry timestamp. Never interpret that stale sample as
                // a clock rollback and release a brand-new flow unconditionally.
                long age=Math.Max(0,Environment.TickCount64-waiter.Started);
                if(waiter.Reply.Task.IsCompleted){pair.Value.Remove(waiter);continue;}
                bool provisional=eligible&&!settled&&route==null&&age>=1000&&
                    (vpnReady??=_worker.FirstUseVpnReady(pair.Key,transport));
                if(!eligible||settled||provisional||age>=2000||route!=null&&(route=="direct"||age>=300||_proofAt.TryGetValue(pair.Key,out long proofAt)&&proofAt<=waiter.Started))
                {
                    string chosen=eligible?route??(provisional?"vpn":""):"";
                    if(provisional)ProvisionalVpn++;else if(chosen.Length>0)Proven++;else if(settled)DefaultSettled++;else TimedOut++;
                    _latency.Enqueue(Math.Max(0,age));while(_latency.Count>256)_latency.Dequeue();
                    _recent.Enqueue(new{domain=pair.Key.Domain,application=Path.GetFileName(pair.Key.Application),route=chosen.Length>0?chosen:"default",milliseconds=age,independentProof=chosen.Length>0&&!provisional,provisionalTransport=provisional,releaseReason=!eligible?"GuardRejected":provisional?"HealthyTransport":chosen.Length>0?"DomainBodyProof":settled?"NeutralHeaders":"Deadline"});
                    while(_recent.Count>32)_recent.Dequeue();
                    waiter.Reply.TrySetResult(chosen);pair.Value.Remove(waiter);
                }
            }
            if(pair.Value.Count==0){_waiting.Remove(pair.Key);_refused.Remove(pair.Key);}
        }
    }
    internal static bool HealthyVpn(string root,string id,int generation,DateTime now)
    {
        string Runtime(string name)=>Path.Combine(root,"runtime",name);
        try
        {
            if(File.Exists(Runtime("stop.request")))return false;
            var session=ComponentLiveness.Read<QualitySession>(Runtime("session.json"));
            if(!session.Running||session.Id!=id||session.BridgeGeneration!=generation)return false;
            var health=ComponentLiveness.Read<QualityHealth>(Runtime("session-health.json"));
            var watch=ComponentLiveness.Read<QualityHealth>(Runtime("watch-state.json"));
            if(!UdpQualityWorker.VpnReady(session,health,watch,now))return false;
            try
            {
                var hold=ComponentLiveness.Read<BridgeSafety>(Runtime("bridge-safety.json"));
                if(hold.Schema!=1||string.IsNullOrEmpty(hold.SessionId)||!hold.Hold||hold.SessionId==id)return false;
            }
            catch(FileNotFoundException){}
            var check=ComponentLiveness.Read<QualitySession>(Runtime("session.json"));
            return check.Running&&check.Id==id&&check.BridgeGeneration==generation&&check.BridgeReady&&
                !check.Recovering&&string.IsNullOrEmpty(check.VpnHoldReason)&&!File.Exists(Runtime("stop.request"));
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException){return false;}
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();_listener.Stop();await _server;
        foreach(var waiters in _waiting.Values)foreach(var waiter in waiters)waiter.Reply.TrySetCanceled();
        await Task.WhenAll(_clients);_stop.Dispose();_slots.Dispose();
    }
}

public sealed partial class PolicyWorker
{
    PolicyFirstUse? _firstUse;
    internal string FirstUseEpoch=>_epoch;
    internal int FirstUseGeneration=>_generation;
    internal int FirstUseTransport=>Storage.Read<PolicySession>(Runtime("session.json")).BridgeGeneration;
    internal bool FirstUseEligible(PolicyKey key,bool checkCurrent=true)=>_options.Mode=="Auto"&&_options.FirstUseGate&&_options.VerifyBody&&
        key.Epoch==_epoch&&PolicySafety.Valid(key)&&!PolicySafety.ManagedApplication(key.Application)&&!Manual(key.Domain)&&(!checkCurrent||Current());
    internal void FirstUseDiscover(PolicyKey key)
    {ObserveDestination(key,"tcp","tls",DateTime.UtcNow);_web.Prioritize(key);_queuedAt.TryAdd(key,DateTime.UtcNow);while(_queuedAt.Count>256)_queuedAt.Remove(_queuedAt.MinBy(p=>p.Value).Key);}
    internal bool FirstUseTake(PolicyKey key)=>_web.TakeSpecific(key);
    internal bool FirstUseVpnReady(PolicyKey key,int transport)=>!AvoidedRoutes(key,DateTime.UtcNow).Contains("vpn")&&
        PolicyFirstUse.HealthyVpn(_root,_epoch.Split(':')[0],transport,DateTime.UtcNow);
    internal bool FirstUseDefaultSettled(PolicyKey key)=>_decisions.TryGetValue(key,out var decision)&&decision.Route=="direct"&&decision.Evidence=="Headers"&&PolicySafety.Live(decision,_epoch,DateTime.UtcNow);
    internal string? FirstUseApplied(PolicyKey key)=>_publisher?.Phase=="Applied"&&_decisions.TryGetValue(key,out var decision)&&
        PolicySafety.Live(decision,_epoch,DateTime.UtcNow)&&decision.Evidence is "BodyComplete" or "BodySample"?decision.Route:null;
}
