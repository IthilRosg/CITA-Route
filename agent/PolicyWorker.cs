using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoutePilot.Agent;

public sealed record PolicyKey(string Epoch,string Domain,string Application,int Port,string Protocol="tcp",string Strategy="tls-record-ipv4-v2");
public sealed record PolicyDecision(PolicyKey Key,string Route,string Reason,DateTime CheckedUtc,DateTime ExpiresUtc,long Milliseconds);
public sealed class PolicyOptions {public string Mode{get;set;}="Lists";public bool AllowListedDpi{get;set;}}
public sealed class PolicySession {public string Id{get;set;}="";public bool Running{get;set;}public int RouterPid{get;set;}}
public sealed record CandidateResult(bool Reachable,bool Definitive,string Detail,long Milliseconds);
internal sealed record PolicyCache(string Fingerprint,string Mode,bool AllowListedDpi,string ClearRequest,PolicyDecision[] Decisions);
internal sealed record PolicyActiveSnapshot(string Epoch,string Mode,bool AllowListedDpi,string ClearRequest,PolicyDecision[] Decisions);
internal sealed class PolicyMeasurement
{
    public PolicyKey Key{get;init;}=null!;
    public string Trigger{get;init;}="";
    public DateTime FirstSignalUtc{get;init;}
    public DateTime StartedUtc{get;init;}
    public DateTime FinishedUtc{get;init;}
    public double QueueMilliseconds{get;init;}
    public ProbeOutcome Outcome{get;init;}
    public string? Route{get;init;}
    public long DnsMilliseconds{get;init;}
    public long TotalMilliseconds{get;init;}
    public CandidateTiming[] Candidates{get;init;}=Array.Empty<CandidateTiming>();
    public DateTime? DecisionCheckedUtc{get;init;}
    public DateTime? AppliedUtc{get;set;}
    public long? AppliedVersion{get;set;}
    public string Disposition{get;init;}="Accepted";
}

public static class PolicySafety
{
    public static string? Domain(string value)
    {
        try
        {
            value=new System.Globalization.IdnMapping().GetAscii(value.Trim().TrimEnd('.')).ToLowerInvariant();
            if(value.Length>253 || IPAddress.TryParse(value,out _) || !Regex.IsMatch(value,@"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9-]{2,63}$"))return null;
            if(value.EndsWith(".local") || value.EndsWith(".lan") || value.EndsWith(".home.arpa"))return null;
            return value;
        }
        catch(ArgumentException){return null;}
    }
    public static bool Matches(string domain,string suffix)=>domain==suffix || domain.EndsWith("."+suffix,StringComparison.Ordinal);
    public static bool Pinned(string domain)=>new[]{"openai.com","chatgpt.com","oaiusercontent.com","oaistatic.com","sora.com","t.me","telegram.org","telegram.me","tdesktop.com"}.Any(s=>Matches(domain,s));
    public static bool Valid(PolicyKey key)=>Domain(key.Domain)==key.Domain && key.Port==443 && key.Protocol=="tcp" && !string.IsNullOrWhiteSpace(key.Application) && Path.IsPathFullyQualified(key.Application);
    public static bool Live(PolicyDecision d,string epoch,DateTime now)=>d.Key.Epoch==epoch && d.ExpiresUtc>now && d.CheckedUtc<=now && d.ExpiresUtc-d.CheckedUtc<=TimeSpan.FromMinutes(16) && Valid(d.Key);
    public static bool Public(IPAddress ip)=>Quality.UdpQualityOptions.PublicIpv4(ip);
}

public sealed class PolicyWorker
{
    internal const int DecisionLimit=256;
    readonly string _root,_epoch;
    readonly PolicyOptions _options;
    readonly Dictionary<PolicyKey,PolicyDecision> _decisions=new();
    readonly Dictionary<(string Host,int Port),Dictionary<string,DateTime>> _applications=new();
    readonly Dictionary<PolicyKey,(int Count,DateTime At)> _failures=new();
    readonly HashSet<string> _seenFailures=new();
    readonly Dictionary<PolicyKey,Task<CalibrationResult>> _pending=new();
    readonly Dictionary<PolicyKey,int> _pendingGeneration=new();
    readonly Dictionary<PolicyKey,CancellationTokenSource> _pendingStops=new();
    readonly Dictionary<PolicyKey,DateTime> _lastUse=new(),_nextProbe=new(),_queuedAt=new();
    readonly HashSet<string> _earlyFlows=new();
    readonly List<PolicyMeasurement> _measurements=new();
    readonly Func<PolicyKey,CancellationToken,Task<CalibrationResult>>? _calibration;
    readonly PolicyPorts _ports;
    PolicyPublisher? _publisher;
    PolicyProgress? _progress;
    bool _dirty=true;
    DateTime _nextPublish;
    int _foregroundSinceRefresh;
    int _generation;
    readonly HttpClient _api=new(new SocketsHttpHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(2)};
    readonly List<string> _events=new();
    readonly Dictionary<string,long> _bytes=new(){{"direct",0},{"dpi",0},{"vpn",0}};
    readonly Dictionary<string,long> _connectionBytes=new();
    readonly Dictionary<string,(PolicyKey Key,DateTime Started,long Up,long Down)> _flowHealth=new();
    readonly Dictionary<string,(string? Host,string? App,DateTime At)> _logFlows=new();
    CancellationToken _runStop;
    long _offset;string _partial="";int _switches;
    readonly string _fingerprint;
    readonly PolicyStability _stability=new();
    PolicyNetworkMonitor? _networkMonitor;
    long _nextStatusTick;
    string _statusPhase="";
    string _clearRequest="";
    readonly HashSet<string> _listed=new(StringComparer.OrdinalIgnoreCase);
    string _listVersion="";
    internal PolicyWorker(string root,PolicySession session,PolicyOptions options,Func<PolicyKey,CancellationToken,Task<CalibrationResult>>? calibration=null)
    {_root=root;_options=options;_ports=PolicyPorts.Read(root);_fingerprint=Fingerprint();_epoch=session.Id+":"+_fingerprint;_calibration=calibration;
        string clear=Runtime("clear-policy.request");if(File.Exists(clear))_clearRequest=File.ReadAllText(clear).Trim();}
    string Runtime(string name)=>Path.Combine(_root,"runtime",name);
    string Fingerprint()
    {
        return _networkMonitor?.Read(true)??PolicyNetwork.Signature(_root);
    }
    internal bool Current()
    {
        if(File.Exists(Runtime("stop.request")))return false;
        var session=Storage.Read<PolicySession>(Runtime("session.json"));
        string clear=File.Exists(Runtime("clear-policy.request"))?File.ReadAllText(Runtime("clear-policy.request")).Trim():"";
        return session.Running && session.Id==_epoch.Split(':')[0] && clear==_clearRequest && _fingerprint==Fingerprint();
    }
    public static async Task Run(string root)
    {
        root=Path.GetFullPath(root);
        if(!File.Exists(Path.Combine(root,"runtime","session.json")))throw new IOException("Missing routing session.");
        using var exclusive=File.Open(Path.Combine(root,"runtime","policy.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read);
        var session=Storage.Read<PolicySession>(Path.Combine(root,"runtime","session.json"));
        var options=Storage.Read<PolicyOptions>(Path.Combine(root,"data","routing-policy.json"));
        var worker=new PolicyWorker(root,session,options);
        worker._networkMonitor=new(()=>PolicyNetwork.Signature(root));
        worker._progress=new(root,session.Id);
        worker._progress.Mark("Starting",true);
        var identity=ProcessIdentity.From(System.Diagnostics.Process.GetProcessById(session.RouterPid));
        using var cancel=new CancellationTokenSource();
        worker._runStop=cancel.Token;
        string log=Path.Combine(root,"logs","router.log");
        worker._offset=File.Exists(log)?new FileInfo(log).Length:0;
        string terminalPhase="Stopped";
        Task quality=UdpQualityWorker.Run(root,session.Id,worker._fingerprint,worker._ports,cancel.Token);
        try
        {
            worker.RefreshListed();worker.LoadCache();worker.LoadActiveBindings();
            worker._publisher=new(root,worker._ports,worker.Current);
            await InitializePublication(worker.PublishAsync,worker.Current,cancel.Token);
            while(identity.Matches())
            {
                var current=Storage.Read<PolicySession>(Path.Combine(root,"runtime","session.json"));
                if(current.Id!=session.Id || !current.Running || File.Exists(worker.Runtime("stop.request")))break;
                if(worker._fingerprint!=worker._networkMonitor.Read())
                {
                    terminalPhase="NetworkChanged";cancel.Cancel();worker.Event("Сеть изменилась. Адаптация остановлена до переподключения.");return;
                }
                worker.RefreshListed();
                string clear=worker.Runtime("clear-policy.request");
                if(File.Exists(clear))
                {
                    string request=File.ReadAllText(clear).Trim();
                    if(request!=worker._clearRequest && Guid.TryParseExact(request,"N",out _))
                    {worker._clearRequest=request;worker.Clear();}
                }
                await worker.Observe();
                if(options.Mode=="Auto")worker.Failures(log,cancel.Token);
                await worker.CompletePending();
                worker.Prune();
                if(worker._dirty && DateTime.UtcNow>=worker._nextPublish)
                {
                    try{await worker.PublishAsync(cancel.Token);}
                    catch(OperationCanceledException)when(!worker.Current()){continue;}
                    catch(Exception ex)when(ex is IOException or OperationCanceledException or System.Net.Sockets.SocketException)
                    {worker._nextPublish=DateTime.UtcNow.AddSeconds(5);worker.Event("Правила не подтверждены движком; прежние пути сохранены. "+ex.GetType().Name);}
                }
                if(options.Mode=="Auto")worker.DrainFailures(cancel.Token);
                worker.MaybeStatus(worker._dirty?"Applying":options.Mode=="Auto"?"Auto":"Lists");
                worker._progress.Mark("Running");
                await WaitForWork(worker._pending.Values,cancel.Token);
            }
        }
        catch(Exception ex)
        {terminalPhase="Error";worker.Event("Контроллер остановлен: "+ex.GetBaseException().Message);throw;}
        finally
        {
            try{worker._progress.Mark(terminalPhase,true);}catch{}
            cancel.Cancel();await quality;await Task.WhenAll(worker._pending.Values.Select(async t=>{try{await t;}catch{}}));
            // Keep the last applied rules across a controller crash/restart.
            // Start/Stop owns the engine; an old controller never clears a newer generation.
            try{if(Storage.Read<PolicySession>(worker.Runtime("session.json")).Id==session.Id)worker.Status(terminalPhase);}catch{}
            if(worker._publisher!=null)await worker._publisher.DisposeAsync();
            foreach(var pendingStop in worker._pendingStops.Values)pendingStop.Dispose();
            worker._api.Dispose();
            worker._networkMonitor?.Dispose();
        }
    }
    internal static async Task InitializePublication(Func<CancellationToken,Task> publish,Func<bool> current,CancellationToken stop,TimeSpan? budget=null)
    {
        // Engine sockets and file watchers can become ready after the worker.
        // Keep the same checked generation and bounded startup deadline; a
        // missing acknowledgement must never be promoted to successful routing.
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(budget??TimeSpan.FromSeconds(25));
        while(true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if(!current())throw new OperationCanceledException("Routing generation changed.");
            try
            {
                await publish(deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                if(!current())throw new OperationCanceledException("Routing generation changed.");
                return;
            }
            catch(Exception ex)when(ex is IOException or OperationCanceledException or System.Net.Sockets.SocketException)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if(!current())throw new OperationCanceledException("Routing generation changed.");
                await Task.Delay(250,deadline.Token);
            }
        }
    }
    internal static void Prepare(string root)
    {
        root=Path.GetFullPath(root);
        var worker=new PolicyWorker(root,new(){Id="prepared"},Storage.Read<PolicyOptions>(Path.Combine(root,"data","routing-policy.json")));
        try{worker.RefreshListed();worker.LoadCache();worker.PublishPrepared();}
        finally{worker._api.Dispose();}
    }
    internal static bool Eligible(string root,PolicySession session,string domain)
    {
        var worker=new PolicyWorker(root,session,Storage.Read<PolicyOptions>(Path.Combine(root,"data/routing-policy.json")));
        try{worker.RefreshListed();return worker._options.Mode=="Auto" && !worker.Manual(domain);}
        finally{worker._api.Dispose();}
    }
    void PublishPrepared()
    {
        foreach(var pair in Desired())Storage.Write(Runtime("rules/auto-"+pair.Key+".json"),new{version=3,rules=pair.Value});
    }
    internal void Clear()
    {
        _generation++;foreach(var pendingStop in _pendingStops.Values)pendingStop.Cancel();
        _decisions.Clear();_failures.Clear();_seenFailures.Clear();_lastUse.Clear();_nextProbe.Clear();_queuedAt.Clear();_bindingStates.Clear();
        _stability.Clear();
        lock(_events)_events.Clear();_dirty=true;
    }
    readonly Dictionary<PolicyKey,string> _bindingStates=new();
    readonly Dictionary<PolicyKey,(DateTime Queued,DateTime Started,string Trigger)> _jobMetadata=new();
    internal void Touch(PolicyKey key,DateTime now)
    {
        _lastUse[key]=now;
        while(_lastUse.Count>1024)_lastUse.Remove(_lastUse.MinBy(p=>p.Value).Key);
    }
    internal async Task CompletePending()
    {
        foreach(var pair in _pending.Where(p=>p.Value.IsCompleted).ToArray())
        {
            _pending.Remove(pair.Key);_pendingGeneration.Remove(pair.Key,out int generation);
            if(_pendingStops.Remove(pair.Key,out var pendingStop))pendingStop.Dispose();
            _jobMetadata.Remove(pair.Key,out var metadata);
            try
            {
                var result=await pair.Value;
                if(generation!=_generation || !Current() || Manual(pair.Key.Domain))continue;
                _nextProbe[pair.Key]=DateTime.UtcNow.AddSeconds(result.Outcome==ProbeOutcome.TlsAuthenticationError?120:30);
                while(_nextProbe.Count>512)_nextProbe.Remove(_nextProbe.MinBy(p=>p.Value).Key);
                bool accepted=false;
                if(result.Decision is { } decision && PolicySafety.Live(decision,_epoch,DateTime.UtcNow))
                {
                    _decisions.TryGetValue(pair.Key,out var previous);
                    accepted=_stability.Accept(previous,decision,result.Candidates,DateTime.UtcNow);
                    if(accepted)
                    {
                        if(previous==null || previous.Route!=decision.Route)_switches++;
                        StoreDecision(decision);_dirty=true;Event(decision.Key.Domain+" → "+decision.Route+": "+decision.Reason);
                    }
                    else
                    {
                        _nextProbe[pair.Key]=DateTime.UtcNow+PolicyStability.ConfirmationDelay;
                        Event(decision.Key.Domain+": проверяется устойчивость "+decision.Route+"; текущий путь сохранён.");
                    }
                }
                else {_stability.Remove(pair.Key);Event(pair.Key.Domain+": "+result.Outcome+"; неподтверждённая замена не применена.");}
                _measurements.Insert(0,new(){Key=pair.Key,Trigger=metadata.Trigger,FirstSignalUtc=metadata.Queued,StartedUtc=metadata.Started,FinishedUtc=DateTime.UtcNow,QueueMilliseconds=Math.Max(0,(metadata.Started-metadata.Queued).TotalMilliseconds),Outcome=result.Outcome,Route=result.Decision?.Route,DnsMilliseconds=result.DnsMilliseconds,TotalMilliseconds=result.TotalMilliseconds,Candidates=result.Candidates,DecisionCheckedUtc=accepted?result.Decision?.CheckedUtc:null,Disposition=accepted?"Accepted":result.Decision!=null?"AwaitingStableReturn":"Unconfirmed"});
                if(_measurements.Count>100)_measurements.RemoveAt(_measurements.Count-1);
                Storage.Write(Path.Combine(_root,"data","policy-metrics.json"),new{epoch=_epoch,updatedUtc=DateTime.UtcNow,measurements=_measurements});
            }
            catch(OperationCanceledException){}
            catch(Exception ex){_nextProbe[pair.Key]=DateTime.UtcNow.AddSeconds(30);Event("Проба не завершена: "+ex.GetType().Name);}
        }
    }
    internal string Binding(PolicyDecision decision,DateTime now)
    {
        if(PolicySafety.Live(decision,_epoch,now))return decision.Route;
        if(decision.Key.Epoch!=_epoch || decision.CheckedUtc>now || decision.Route=="direct")return "";
        // Confidence TTL and active routing direction are separate. A failed
        // refresh must neither block a working site nor silently send it direct.
        // Retain this direction ONLY inside the exact network/session epoch;
        // a confirmed replacement, Clear, Stop or a new epoch retires it.
        return decision.Route;
    }
    void Prune()
    {
        foreach(var pair in _decisions.ToArray())
        {
            if(Manual(pair.Key.Domain))_stability.Remove(pair.Key);
            // A newly saved manual rule is applied by a separate engine watcher.
            // Keep its shadowed automatic binding until the manual rule takes
            // priority; removing it early could create a direct-route window.
            string binding=Binding(pair.Value,DateTime.UtcNow);
            if(binding==""){_decisions.Remove(pair.Key);_bindingStates.Remove(pair.Key);_stability.Remove(pair.Key);_dirty=true;continue;}
            if(!_bindingStates.TryGetValue(pair.Key,out string? previous) || previous!=binding){_bindingStates[pair.Key]=binding;_dirty=true;}
        }
        foreach(var key in _lastUse.Where(p=>DateTime.UtcNow-p.Value>TimeSpan.FromHours(1)).Select(p=>p.Key).ToArray())_lastUse.Remove(key);
        foreach(var key in _nextProbe.Where(p=>p.Value<DateTime.UtcNow.AddMinutes(-2) && !_decisions.ContainsKey(p.Key)).Select(p=>p.Key).ToArray())_nextProbe.Remove(key);
    }
    bool Manual(string domain)
    {
        if(PolicySafety.Pinned(domain))return true;
        if(!_options.AllowListedDpi)
        {
            string suffix=domain;
            while(suffix.Contains('.')){if(_listed.Contains(suffix))return true;suffix=suffix[(suffix.IndexOf('.')+1)..];}
        }
        foreach(string file in new[]{"force-direct.txt","force-vpn.txt"})
            if(File.ReadLines(Path.Combine(_root,file)).Select(l=>PolicySafety.Domain(l.Split('#')[0])).Any(s=>s!=null && PolicySafety.Matches(domain,s)))return true;
        return false;
    }
    void RefreshListed()
    {
        string[] paths=new[]{"russia_inside.json","discord.json"}.Select(n=>Runtime("rules/"+n)).ToArray();
        string version=string.Join("|",paths.Select(p=>File.Exists(p)?File.GetLastWriteTimeUtc(p).Ticks.ToString():"missing"));
        if(version==_listVersion)return;
        var next=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(string path in paths)
        {
            if(!File.Exists(path))continue;
            using var json=JsonDocument.Parse(File.ReadAllText(path));
            foreach(var rule in json.RootElement.GetProperty("rules").EnumerateArray())
            foreach(string kind in new[]{"domain","domain_suffix"})
                if(rule.TryGetProperty(kind,out var names))foreach(var name in names.EnumerateArray()){string? value=PolicySafety.Domain(name.GetString()??"");if(value!=null)next.Add(value);}
        }
        _listed.Clear();_listed.UnionWith(next);_listVersion=version;
    }
    async Task Observe()
    {
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,"http://127.0.0.1:"+_ports.Api+"/connections");
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",File.ReadAllText(Path.Combine(_root,"private","controller-token.txt")).Trim());
            using var response=await _api.SendAsync(request);response.EnsureSuccessStatusCode();
            using var content=await response.Content.ReadAsStreamAsync();
            using var limited=new MemoryStream();byte[] buffer=new byte[8192];int read;
            while((read=await content.ReadAsync(buffer))>0){if(limited.Length+read>2097152)throw new IOException("Oversized connection snapshot.");limited.Write(buffer,0,read);}
            using var data=JsonDocument.Parse(limited.ToArray());var now=DateTime.UtcNow;
            var seen=new HashSet<string>();
            foreach(var connection in data.RootElement.GetProperty("connections").EnumerateArray())
            {
                var meta=connection.GetProperty("metadata");
                string Get(string name)=>meta.TryGetProperty(name,out var value)?value.ToString():"";
                string? domain=PolicySafety.Domain(Get("host"));string app=Regex.Replace(Get("processPath"),@" \(\d+\)$","");
                if(domain!=null && Path.IsPathFullyQualified(app) && !Path.GetFileName(app).Equals("RoutePilot.Agent.exe",StringComparison.OrdinalIgnoreCase) && int.TryParse(Get("destinationPort"),out int port) && port==443 && Get("network")=="tcp")
                {
                    if(!_applications.TryGetValue((domain,port),out var apps))_applications[(domain,port)]=apps=new(StringComparer.OrdinalIgnoreCase);
                    apps[app]=now;
                }
                string id=connection.GetProperty("id").GetString()!;seen.Add(id);
                long total=Math.Max(0,connection.GetProperty("upload").GetInt64())+Math.Max(0,connection.GetProperty("download").GetInt64());
                string route=connection.GetProperty("chains").EnumerateArray().Select(c=>c.GetString()).FirstOrDefault(c=>c is "vpn" or "dpi" or "direct")??"direct";
                _bytes[route]+=Math.Max(0,total-(_connectionBytes.TryGetValue(id,out long previous)?previous:0));_connectionBytes[id]=total;
                if(domain!=null && !Path.GetFileName(app).Equals("RoutePilot.Agent.exe",StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(app) && Get("destinationPort")=="443" && Get("network")=="tcp")
                {
                    if(IPAddress.TryParse(Get("destinationIP"),out var actual) && actual.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork)continue;
                    var started=_flowHealth.TryGetValue(id,out var old)?old.Started:now;
                    var key=new PolicyKey(_epoch,domain,app,443);
                    Touch(key,now);
                    long up=connection.GetProperty("upload").GetInt64(),down=connection.GetProperty("download").GetInt64();
                    _flowHealth[id]=(key,started,up,down);
                    // Bytes alone never change a route. A stall merely starts
                    // two fresh probes on each candidate before browser timeout.
                    if(_options.Mode=="Auto" && up>0 && down==0 && (now-started).TotalSeconds>=2 && _earlyFlows.Add(id))RegisterFailure(key,"early:"+id,_runStop);
                }
            }
            foreach(var key in _connectionBytes.Keys.Where(k=>!seen.Contains(k)).ToArray())_connectionBytes.Remove(key);
            foreach(var pair in _flowHealth.Where(p=>!seen.Contains(p.Key)).ToArray())
            {
                _flowHealth.Remove(pair.Key);
                _earlyFlows.Remove(pair.Key);
                // A repeated flow ending after sending bytes without receiving any response
                // is a calibration hint, not a DPI diagnosis. No application bytes are replayed.
                if(_options.Mode=="Auto" && pair.Value.Up>0 && pair.Value.Down==0 && (now-pair.Value.Started).TotalSeconds>=1)
                    RegisterFailure(pair.Value.Key,"api:"+pair.Key,_runStop);
            }
            foreach(var pair in _applications.ToArray())
            {foreach(string app in pair.Value.Where(p=>(now-p.Value).TotalSeconds>60).Select(p=>p.Key).ToArray())pair.Value.Remove(app);if(pair.Value.Count==0)_applications.Remove(pair.Key);}
            while(_applications.Count>512)_applications.Remove(_applications.Keys.First());
        }
        catch(Exception ex)when(ex is HttpRequestException or IOException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException){/* No routing inference from a missing statistics API. */}
    }
    void Failures(string path,CancellationToken cancellation)
    {
        if(!File.Exists(path))return;
        if(new FileInfo(path).Length>8*1024*1024)
        {
            try
            {
                // Keep the same append handle used by the router; an atomic rename would
                // leave it writing into an unlinked file. Limit diagnostic domain logging.
                using var trim=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite|FileShare.Delete);
                byte[] tail=new byte[2*1024*1024];trim.Position=Math.Max(0,trim.Length-tail.Length);int count=trim.Read(tail);
                int keep=Math.Min(count,1024*1024),older=count-keep;
                // Retain the preceding bounded segment as well as the active tail.
                // The active file stays open under the router's append handle.
                if(older>0)
                {
                    string previous=Path.Combine(Path.GetDirectoryName(path)!,"router.previous.log"),temporary=previous+"."+Guid.NewGuid().ToString("N")+".tmp";
                    try{File.WriteAllBytes(temporary,tail[..older]);File.Move(temporary,previous,true);}
                    catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
                    finally{try{if(File.Exists(temporary))File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}
                }
                trim.SetLength(0);trim.Position=0;trim.Write(tail,older,keep);trim.Flush();_offset=0;_partial="";
            }
            catch(IOException){}
        }
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(file.Length<_offset){_offset=0;_partial="";}
        if(file.Length-_offset>65536){_offset=file.Length-65536;_partial="";}
        file.Position=_offset;using var reader=new StreamReader(file);string added=reader.ReadToEnd();_offset=file.Position;
        string[] lines=(_partial+added).Split('\n');_partial=lines[^1];
        foreach(string line in lines[..^1])
        {
            var flow=Regex.Match(line,@"\[(\d+)\s+[^\]]+\]");
            if(flow.Success)
            {
                string id=flow.Groups[1].Value;
                var context=_logFlows.TryGetValue(id,out var existing)?existing:(Host:(string?)null,App:(string?)null,At:DateTime.UtcNow);
                var destination=Regex.Match(line,@"inbound connection to ([a-zA-Z0-9.-]+):443$");
                if(destination.Success)context.Host=PolicySafety.Domain(destination.Groups[1].Value);
                var process=Regex.Match(line,@"router: found process path: (.+)$");
                if(process.Success)context.App=Regex.Replace(process.Groups[1].Value.Trim(),@" \(\d+\)$","");
                _logFlows[id]=context;
            }
            var match=Regex.Match(line,@"connection: open connection to ([a-zA-Z0-9.-]+):443 using outbound/(?:direct|socks)\[(direct|dpi|vpn)\]");
            string? host=match.Success?PolicySafety.Domain(match.Groups[1].Value):null;
            if(host==null || Manual(host) || !flow.Success)continue;
            string? app=null;
            if(_logFlows.TryGetValue(flow.Groups[1].Value,out var known) && known.Host==host && (DateTime.UtcNow-known.At).TotalSeconds<60)app=known.App;
            if(app==null && _applications.TryGetValue((host,443),out var applications) && applications.Count==1)app=applications.Keys.Single();
            if(app==null || !Path.IsPathFullyQualified(app) || Path.GetFileName(app).Equals("RoutePilot.Agent.exe",StringComparison.OrdinalIgnoreCase))continue;
            var key=new PolicyKey(_epoch,host,app,443);
            RegisterFailure(key,"log:"+flow.Groups[1].Value,cancellation);
        }
        foreach(var id in _logFlows.Where(p=>(DateTime.UtcNow-p.Value.At).TotalSeconds>60).Select(p=>p.Key).ToArray())_logFlows.Remove(id);
        while(_logFlows.Count>1024)_logFlows.Remove(_logFlows.Keys.First());
        while(_failures.Count>256)_failures.Remove(_failures.Keys.First());
    }
    internal void RegisterFailure(PolicyKey key,string flow,CancellationToken cancellation)
    {
        if(Manual(key.Domain) || _pending.ContainsKey(key) || !PolicySafety.Valid(key) ||
            (_nextProbe.TryGetValue(key,out var next) && DateTime.UtcNow<next) ||
            (_decisions.TryGetValue(key,out var cached) && (DateTime.UtcNow-cached.CheckedUtc).TotalSeconds<10) || !_seenFailures.Add(key.Application+":"+key.Domain+":"+flow))return;
        // API and log may describe the SAME flow. They coalesce into one hint,
        // never two confirmations. Only the independent TLS probes decide.
        _failures[key]=(1,DateTime.UtcNow);
        _queuedAt.TryAdd(key,DateTime.UtcNow);
        while(_failures.Count>256)_failures.Remove(_failures.Keys.First());
        while(_queuedAt.Count>256)_queuedAt.Remove(_queuedAt.Keys.First());
        if(_seenFailures.Count>1024)_seenFailures.Clear();
    }
    internal void DrainFailures(CancellationToken cancellation)
    {
        var now=DateTime.UtcNow;
        var refresh=_decisions.Values.Where(d=>!Manual(d.Key.Domain) && !_pending.ContainsKey(d.Key) &&
            _lastUse.TryGetValue(d.Key,out var used) && now-used<TimeSpan.FromMinutes(5) &&
            (!_nextProbe.TryGetValue(d.Key,out var next) || next<=now) &&
            (_stability.Pending(d.Key) || d.ExpiresUtc-now<TimeSpan.FromSeconds(120+(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(d.Key.Domain)))%60))))
            .OrderBy(d=>d.ExpiresUtc).ToList();
        while(_pending.Count<2)
        {
            var failures=_failures.Where(p=>now-p.Value.At<TimeSpan.FromMinutes(2) && !_pending.ContainsKey(p.Key) && !Manual(p.Key.Domain) && (!_nextProbe.TryGetValue(p.Key,out var next) || next<=now)).OrderBy(p=>p.Value.At).ToArray();
            bool background=refresh.Count>0 && (failures.Length==0 || _foregroundSinceRefresh>=4);
            if(!background && failures.Length==0)break;
            PolicyKey key=background?refresh[0].Key:failures[0].Key;
            if(background){refresh.RemoveAt(0);_foregroundSinceRefresh=0;}else{_failures.Remove(key);_foregroundSinceRefresh++;refresh.RemoveAll(d=>d.Key==key);}
            _queuedAt.Remove(key,out var queued);
            var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation);_pendingStops[key]=linked;
            _jobMetadata[key]=(queued==default?now:queued,now,background?"refresh":"connection-hint");
            _pendingGeneration[key]=_generation;
            // Keep the active binding while a replacement is being measured.
            _pending[key]=_calibration?.Invoke(key,linked.Token)??Calibrate(key,linked.Token,!background);
        }
        foreach(var key in _failures.Where(p=>now-p.Value.At>=TimeSpan.FromMinutes(2)).Select(p=>p.Key).ToArray()){_failures.Remove(key);_queuedAt.Remove(key);}
    }
    async Task<CalibrationResult> Calibrate(PolicyKey key,CancellationToken stop,bool failureTriggered=false)
    {
        var timer=System.Diagnostics.Stopwatch.StartNew();
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(stop);budget.CancelAfter(TimeSpan.FromSeconds(11));
        long dnsMs=0;
        try
        {
            _decisions.TryGetValue(key,out var current);
            var resolved=await PolicyTransport.SelectResolved(key.Domain,_ports,budget.Token,failureTriggered,observeRoute:current?.Route);dnsMs=resolved.DnsMilliseconds;
            var chosen=resolved.Selection;
            if(chosen.Route==null)return new(chosen.Outcome,null,dnsMs,timer.ElapsedMilliseconds,chosen.Candidates);
            var now=DateTime.UtcNow;
            bool temporary=PolicyTransport.UnstablePreferred(chosen)||chosen.Candidates.TakeWhile(c=>c.Route!=chosen.Route).Any(c=>c.Outcome==ProbeOutcome.DnsUnavailable);
            var reason=chosen.Route=="direct"?"Две прямые TLS-пробы успешны.":temporary?"Подтверждён временный путь; прямой доступ или его DNS требуют перепроверки.":"Две TLS-пробы выбранного пути успешны; более приоритетные пути проверены.";
            var decision=new PolicyDecision(key,chosen.Route,reason,now,now.AddSeconds(temporary?60:900),chosen.Milliseconds);
            return new(ProbeOutcome.Reachable,decision,dnsMs,timer.ElapsedMilliseconds,chosen.Candidates);
        }
        catch(OperationCanceledException){return new(ProbeOutcome.Cancelled,null,dnsMs,timer.ElapsedMilliseconds,Array.Empty<CandidateTiming>());}
    }
    public static async Task<(string Route,long Milliseconds)?> SelectRoute(Func<int,CancellationToken,Task<CandidateResult>> probe,CancellationToken stop)
    {
        var selected=await PolicyTransport.Select(probe,new(),stop);
        return selected.Route==null?null:(selected.Route,selected.Milliseconds);
    }
    public static async Task<CandidateResult> Probe(string domain,int port,CancellationToken stop)
    {
        if(PolicySafety.Domain(domain)!=domain || port is not (20808 or 20818 or 20828 or 20829))throw new ArgumentException("Invalid policy probe.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop);timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var handler=new SocketsHttpHandler{UseProxy=true,Proxy=new WebProxy("socks5://127.0.0.1:"+port),AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(2)};
        int rejectedCertificate=0;
        handler.SslOptions.RemoteCertificateValidationCallback=(_,_,_,errors)=>{if(errors!=SslPolicyErrors.None)Interlocked.Exchange(ref rejectedCertificate,1);return errors==SslPolicyErrors.None;};
        using var client=new HttpClient(handler);using var request=new HttpRequestMessage(HttpMethod.Head,"https://"+domain+"/"){Version=HttpVersion.Version11,VersionPolicy=HttpVersionPolicy.RequestVersionExact};
        var watch=System.Diagnostics.Stopwatch.StartNew();
        try
        {using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);return new(true,true,"TLS established; HTTP "+(int)response.StatusCode,watch.ElapsedMilliseconds);}
        catch(HttpRequestException)when(Volatile.Read(ref rejectedCertificate)!=0)
        {return new(false,true,"TLS certificate/authentication error; adaptation refused.",watch.ElapsedMilliseconds);}
        catch(Exception ex)when(ex is HttpRequestException or OperationCanceledException or IOException)
        {stop.ThrowIfCancellationRequested();return new(false,false,ex.GetBaseException().Message,watch.ElapsedMilliseconds);}
    }
    internal void StoreDecision(PolicyDecision decision)
    {
        if(!_decisions.ContainsKey(decision.Key) && _decisions.Count>=DecisionLimit)
        {
            var evicted=_decisions.MinBy(p=>p.Value.CheckedUtc).Key;
            _decisions.Remove(evicted);_bindingStates.Remove(evicted);
            _stability.Remove(evicted);
        }
        _decisions[decision.Key]=decision;
    }
    internal void LoadCache()
    {
        string path=Path.Combine(_root,"data","policy-cache.json");
        if(_options.Mode!="Auto" || !File.Exists(path))return;
        try
        {
            if(new FileInfo(path).Length>1048576)return;
            var cache=Storage.Read<PolicyCache>(path);
            if(cache.Fingerprint!=_fingerprint || cache.Mode!=_options.Mode || cache.AllowListedDpi!=_options.AllowListedDpi || cache.ClearRequest!=_clearRequest || cache.Decisions.Length>DecisionLimit)return;
            foreach(var old in cache.Decisions)
            {
                var decision=old with{Key=old.Key with{Epoch=_epoch}};
                if(decision.Route is "direct" or "dpi" or "vpn" && decision.Key.Strategy=="tls-record-ipv4-v2" && PolicySafety.Live(decision,_epoch,DateTime.UtcNow) && !Manual(decision.Key.Domain))StoreDecision(decision);
            }
            if(_decisions.Count>0)Event("Восстановлены проверенные маршруты: "+_decisions.Count);
        }
        catch(Exception ex)when(ex is IOException or JsonException or InvalidOperationException or NullReferenceException){Event("Кэш маршрутов не загружен; начата новая проверка.");}
    }
    internal void LoadActiveBindings()
    {
        string path=Runtime("policy-active.json");
        if(_options.Mode!="Auto" || !File.Exists(path))return;
        try
        {
            if(new FileInfo(path).Length>1048576)return;
            var snapshot=Storage.Read<PolicyActiveSnapshot>(path);
            if(snapshot.Epoch!=_epoch || snapshot.Mode!=_options.Mode || snapshot.AllowListedDpi!=_options.AllowListedDpi || snapshot.ClearRequest!=_clearRequest || snapshot.Decisions.Length>DecisionLimit)return;
            foreach(var decision in snapshot.Decisions)
                if(!_decisions.ContainsKey(decision.Key) && decision.Key.Epoch==_epoch && decision.Key.Strategy=="tls-record-ipv4-v2" && decision.Route is "vpn" or "dpi" && PolicySafety.Valid(decision.Key) && decision.CheckedUtc<=DateTime.UtcNow && decision.ExpiresUtc>decision.CheckedUtc && decision.ExpiresUtc-decision.CheckedUtc<=TimeSpan.FromMinutes(16))StoreDecision(decision);
        }
        catch(Exception ex)when(ex is IOException or JsonException or InvalidOperationException or NullReferenceException){Event("Направления активной сессии не загружены.");}
    }
    internal void Publish(bool persist=true)
    {
        if(Storage.Read<PolicySession>(Runtime("session.json")).Id!=_epoch.Split(':')[0])return;
        if(_publisher!=null)throw new InvalidOperationException("Live publication must await engine acknowledgement.");
        // Offline fixtures/preparation only. Run() always uses PublishAsync.
        foreach(var pair in Desired())Storage.Write(Runtime("rules/auto-"+pair.Key+".json"),new{version=3,rules=pair.Value});
        if(persist)PersistCache();
    }
    Dictionary<string,System.Text.Json.Nodes.JsonObject[]> Desired()
    {
        var now=DateTime.UtcNow;
        return PolicyPublisher.Routes.ToDictionary(route=>route,route=>_decisions.Values.Where(d=>PolicySafety.Valid(d.Key) && Binding(d,now)==route).Select(d=>PolicyPublisher.Rule(d.Key)).ToArray());
    }
    void PersistCache()
    {
        if(_options.Mode=="Auto")Storage.Write(Path.Combine(_root,"data","policy-cache.json"),new PolicyCache(_fingerprint,_options.Mode,_options.AllowListedDpi,_clearRequest,_decisions.Values.Where(d=>PolicySafety.Live(d,_epoch,DateTime.UtcNow) && !Manual(d.Key.Domain)).ToArray()));
    }
    async Task PublishAsync(CancellationToken stop)
    {
        _progress?.Mark("Publishing",true);
        await _publisher!.Apply(Desired(),stop);
        if(!Current())throw new OperationCanceledException();
        Storage.Write(Runtime("policy-active.json"),new PolicyActiveSnapshot(_epoch,_options.Mode,_options.AllowListedDpi,_clearRequest,_decisions.Values.ToArray()));
        PersistCache();_dirty=false;
        var applied=DateTime.UtcNow;
        foreach(var measurement in _measurements.Where(m=>m.AppliedUtc==null && m.DecisionCheckedUtc!=null))
            if(_decisions.TryGetValue(measurement.Key,out var decision) && decision.CheckedUtc==measurement.DecisionCheckedUtc){measurement.AppliedUtc=applied;measurement.AppliedVersion=_publisher.Version;}
        if(_measurements.Count>0)Storage.Write(Path.Combine(_root,"data","policy-metrics.json"),new{epoch=_epoch,updatedUtc=applied,measurements=_measurements});
        Storage.Write(Runtime("policy-publication.json"),new{epoch=_epoch,phase=_publisher.Phase,version=_publisher.Version,appliedUtc=applied,milliseconds=_publisher.Milliseconds});
    }
    internal void Event(string message){lock(_events){_events.Insert(0,message);if(_events.Count>20)_events.RemoveAt(_events.Count-1);}}
    internal static async Task WaitForWork(IEnumerable<Task> pending,CancellationToken stop,TimeSpan? idle=null)
    {
        using var timer=CancellationTokenSource.CreateLinkedTokenSource(stop);
        try{await Task.WhenAny(pending.Append(Task.Delay(idle??TimeSpan.FromMilliseconds(500),timer.Token)));stop.ThrowIfCancellationRequested();}
        finally{timer.Cancel();}
    }
    internal void MaybeStatus(string phase)
    {
        long now=Environment.TickCount64;
        if(phase==_statusPhase && now<_nextStatusTick)return;
        Status(phase);_statusPhase=phase;_nextStatusTick=now+2000;
    }
    internal void Status(string phase)
    {
        string[] events;lock(_events)events=_events.ToArray();
        Storage.Write(Runtime("policy-state.json"),new{phase,version=typeof(PolicyWorker).Assembly.GetName().Version!.ToString(3),epoch=_epoch,updatedUtc=DateTime.UtcNow,strategy="TLS record / TCP IPv4; bounded parallel probes",pending=_pending.Count,queued=_failures.Count,switches=_switches,bytes=_bytes,decisions=_decisions.Values.ToArray(),stale=_decisions.Values.Count(d=>!PolicySafety.Live(d,_epoch,DateTime.UtcNow)),blocked=_decisions.Values.Count(d=>Binding(d,DateTime.UtcNow)=="blocked"),publication=new{phase=_publisher?.Phase??"Offline",version=_publisher?.Version??0,milliseconds=_publisher?.Milliseconds??0},measurements=_measurements.Take(5).ToArray(),events,limits="IPv4 TCP/443; TLS reachability, not account/region access; no UDP or request replay; sampled active-flow byte counters"});
    }
}
