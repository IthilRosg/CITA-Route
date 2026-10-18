using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using RoutePilot.Quality;

namespace RoutePilot.Agent;

internal sealed record UdpRoundResult(DateTime StartedUtc,int Sent,int Received,int Duplicates,int Reordered,int Late);

internal sealed class UdpRoundAccounting
{
    readonly long[] _sent;
    readonly bool[] _seen,_received;
    readonly object _gate=new();
    readonly int _deadline;
    int _highest=-1,_duplicates,_reordered,_late;
    internal UdpRoundAccounting(int count,int deadline){_sent=Enumerable.Repeat(-1L,count).ToArray();_seen=new bool[count];_received=new bool[count];_deadline=deadline;}
    internal void Sent(int index,long at){lock(_gate)_sent[index]=at;}
    internal void Accept(ulong index,long at)
    {
        lock(_gate)
        {
            if(index>=(ulong)_sent.Length || _sent[index]<0)return;
            int i=(int)index;
            if(_seen[i]){_duplicates++;return;}_seen[i]=true;
            if(at-_sent[i]>_deadline){_late++;return;}
            _received[i]=true;if(i<_highest)_reordered++;_highest=Math.Max(_highest,i);
        }
    }
    internal UdpRoundResult Finish(DateTime started){lock(_gate)return new(started,_sent.Count(t=>t>=0),_received.Count(t=>t),_duplicates,_reordered,_late);}
}

internal static class UdpQualityProbe
{
    internal const int Count=10,IntervalMs=500,DeadlineMs=1500;
    internal static async Task<UdpRoundResult> Run(int port,IPEndPoint target,byte[] key,CancellationToken stop,int intervalMs=IntervalMs,int deadlineMs=DeadlineMs)
    {
        using var connect=CancellationTokenSource.CreateLinkedTokenSource(stop);connect.CancelAfter(2000);
        using var relay=await SocksUdpAssociation.Open(port,target,connect.Token);
        using var receiveStop=CancellationTokenSource.CreateLinkedTokenSource(stop);
        var account=new UdpRoundAccounting(Count,deadlineMs);byte[] nonce=RandomNumberGenerator.GetBytes(16);
        var started=DateTime.UtcNow;var timer=Stopwatch.StartNew();
        async Task Receive()
        {
            int limit=0;
            try
            {
                while(true)
                {
                    var packet=await relay.Receive(receiveStop.Token);
                    if(++limit>256)throw new IOException("UDP relay receive budget exceeded.");
                    if(packet!=null && UdpProbeProtocol.Match(packet,key,nonce,out ulong sequence))account.Accept(sequence,timer.ElapsedMilliseconds);
                }
            }
            catch(OperationCanceledException)when(receiveStop.IsCancellationRequested){}
        }
        var receiving=Receive();
        try
        {
            for(int i=0;i<Count;i++)
            {
                stop.ThrowIfCancellationRequested();
                account.Sent(i,timer.ElapsedMilliseconds);
                await relay.Send(UdpProbeProtocol.Create(key,nonce,(ulong)i),stop);
                if(i<Count-1)await Task.Delay(intervalMs,stop);
            }
            await Task.Delay(deadlineMs+20,stop);
        }
        finally{receiveStop.Cancel();await receiving;}
        stop.ThrowIfCancellationRequested();return account.Finish(started);
    }
}

internal sealed class UdpQualityWindow
{
    readonly Queue<UdpRoundResult> _rounds=new();
    internal UdpPathQuality Add(UdpRoundResult round,DateTime now)
    {
        _rounds.Enqueue(round);while(_rounds.Count>6)_rounds.Dequeue();
        while(_rounds.Count>0 && now-_rounds.Peek().StartedUtc>TimeSpan.FromSeconds(75))_rounds.Dequeue();
        int sent=_rounds.Sum(r=>r.Sent),received=_rounds.Sum(r=>r.Received);
        return new(){Status=round.Received==0?"Unavailable":sent<10?"Collecting":"Measured",
            Reason=round.Received==0?"NoReplies":"",Sent=sent,Received=received,
            LossPercent=round.Received>0 && sent>=10?Math.Round(100.0*(sent-received)/sent,1):null,
            Duplicates=_rounds.Sum(r=>r.Duplicates),Reordered=_rounds.Sum(r=>r.Reordered),Late=_rounds.Sum(r=>r.Late),
            WindowStartedUtc=_rounds.Count>0?_rounds.Peek().StartedUtc:now,UpdatedUtc=now};
    }
}

internal sealed class QualitySession
{
    public bool Running{get;set;}
    public bool Recovering{get;set;}
    public bool BridgeReady{get;set;}
    public int BridgeGeneration{get;set;}
    public string Id{get;set;}="";
    public string SourceProfile{get;set;}="";
    public string NetworkSignature{get;set;}="";
    public string VpnHoldReason{get;set;}="";
}
internal sealed class QualityHealth
{
    public string SessionId{get;set;}="";
    public int BridgeGeneration{get;set;}
    public bool Healthy{get;set;}
    public string WireSockState{get;set;}="";
    public DateTime UpdatedUtc{get;set;}
}

internal static class UdpQualityWorker
{
    static T Read<T>(string path) where T:new()
    {
        try
        {
            if(!File.Exists(path) || new FileInfo(path).Length>8192)return new();
            return Storage.Read<T>(path);
        }
        catch(Exception ex)when(ex is IOException or JsonException or UnauthorizedAccessException){return new();}
    }
    internal static bool ValidOptions(UdpQualityOptions options,bool fixture=false)=>options.ValidKey && options.Port is >=1024 and <=65535 &&
        IPAddress.TryParse(options.Address,out var ip) && ip.AddressFamily==AddressFamily.InterNetwork &&
        (fixture && IPAddress.IsLoopback(ip) || PolicySafety.Public(ip));
    static bool Fresh(DateTime at,DateTime now,int seconds)=>at<=now.AddSeconds(2) && at>now.AddSeconds(-seconds);
    internal static bool VpnReady(QualitySession session,QualityHealth health,QualityHealth watch,DateTime now)=>
        session.BridgeReady && !session.Recovering && string.IsNullOrEmpty(session.VpnHoldReason) && health.Healthy &&
        health.SessionId==session.Id && health.BridgeGeneration==session.BridgeGeneration && Fresh(health.UpdatedUtc,now,90) &&
        watch.SessionId==session.Id && watch.WireSockState=="Connected" && Fresh(watch.UpdatedUtc,now,25);
    internal static async Task Run(string root,string sessionId,string network,PolicyPorts ports,CancellationToken stop,bool fixture=false)
    {
        string Runtime(string name)=>Path.Combine(root,"runtime",name);
        string epoch="";var state=new UdpQualityState();
        var windows=new[]{new UdpQualityWindow(),new UdpQualityWindow()};
        var tasks=new Task<UdpRoundResult>?[2];var next=new DateTime[2];
        var pathStops=new CancellationTokenSource?[2];
        using var roundStop=CancellationTokenSource.CreateLinkedTokenSource(stop);
        CancellationTokenSource? epochStop=null;DateTime lastWrite=DateTime.MinValue,lastWriteError=DateTime.MinValue;
        async Task CancelPath(int path)
        {
            pathStops[path]?.Cancel();
            if(tasks[path]!=null)try{await tasks[path]!;}catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException){}
            tasks[path]=null;pathStops[path]?.Dispose();pathStops[path]=null;
        }
        async Task CancelRounds()
        {
            epochStop?.Cancel();
            for(int path=0;path<2;path++)await CancelPath(path);
            epochStop?.Dispose();epochStop=null;
        }
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var session=Read<QualitySession>(Runtime("session.json"));
                if(!session.Running || session.Id!=sessionId || File.Exists(Runtime("stop.request")) || session.NetworkSignature!=network)break;
                var options=Read<UdpQualityOptions>(Path.Combine(root,"data","udp-quality.json"));
                var now=DateTime.UtcNow;
                bool configured=options.Enabled && ValidOptions(options,fixture);
                bool vpn=configured && VpnReady(session,Read<QualityHealth>(Runtime("session-health.json")),Read<QualityHealth>(Runtime("watch-state.json")),now);
                string current=options.Identity+":"+session.SourceProfile+":"+session.BridgeGeneration+":"+session.Recovering;
                if(epoch!=current)
                {
                    await CancelRounds();epoch=current;epochStop=CancellationTokenSource.CreateLinkedTokenSource(roundStop.Token);
                    windows=new[]{new UdpQualityWindow(),new UdpQualityWindow()};Array.Clear(next);
                    state=new(){SessionId=sessionId,NetworkSignature=network,SourceProfile=session.SourceProfile,
                        BridgeGeneration=session.BridgeGeneration,ConfigurationId=options.Identity};lastWrite=DateTime.MinValue;
                }
                for(int path=0;path<2;path++)
                {
                    UdpPathQuality Result()=>path==0?state.Direct:state.Vpn;
                    void Set(UdpPathQuality value){if(path==0)state.Direct=value;else state.Vpn=value;}
                    if(!configured || session.Recovering || path==1&&!vpn)
                    {
                        await CancelPath(path);windows[path]=new();next[path]=DateTime.MinValue;
                        string reason=!options.Enabled?"NotConfigured":!configured?"InvalidConfiguration":"PathNotVerified";
                        if(Result().Reason!=reason)lastWrite=DateTime.MinValue;
                        Set(new(){Status=options.Enabled?"Unavailable":"Unmeasured",Reason=reason,UpdatedUtc=now});continue;
                    }
                    if(tasks[path]?.IsCompleted==true)
                    {
                        try{Set(windows[path].Add(await tasks[path]!,now));}
                        catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException)
                        {Set(new(){Status="Unavailable",Reason="LocalRelayUnavailable",UpdatedUtc=now});windows[path]=new();}
                        tasks[path]=null;pathStops[path]?.Dispose();pathStops[path]=null;lastWrite=DateTime.MinValue;
                    }
                    if(tasks[path]==null && now>=next[path])
                    {
                        if(Result().Status=="Unmeasured")Set(new(){Status="Collecting",Reason="Warmup",UpdatedUtc=now});
                        next[path]=now.AddSeconds(10);
                        pathStops[path]=CancellationTokenSource.CreateLinkedTokenSource(epochStop!.Token);
                        tasks[path]=UdpQualityProbe.Run(path==0?ports.Direct:ports.Vpn,new(IPAddress.Parse(options.Address),options.Port),Convert.FromHexString(options.Key),pathStops[path]!.Token);
                    }
                }
                // Re-read before publishing so an old generation cannot restore old measurements.
                var check=Read<QualitySession>(Runtime("session.json"));
                if(check.Id!=session.Id || !check.Running || check.BridgeGeneration!=session.BridgeGeneration ||
                    check.NetworkSignature!=network || check.SourceProfile!=session.SourceProfile || check.Recovering!=session.Recovering || File.Exists(Runtime("stop.request")))continue;
                if(now-lastWrite>=TimeSpan.FromSeconds(5))
                {
                    state.UpdatedUtc=now;
                    try{Storage.Write(Runtime("udp-quality.json"),state);lastWrite=now;}
                    catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
                    {
                        if(now-lastWriteError>TimeSpan.FromSeconds(60)){Console.Error.WriteLine("UDP quality snapshot unavailable: "+ex.GetType().Name);lastWriteError=now;}
                        // A temporary file lock must not permanently disable the collector.
                    }
                }
                await Task.Delay(configured?500:1000,stop);
            }
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(Exception ex)when(ex is IOException or JsonException or UnauthorizedAccessException)
        {Console.Error.WriteLine("UDP quality collector stopped: "+ex.GetType().Name);}
        finally{await CancelRounds();}
    }
}
