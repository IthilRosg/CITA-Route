using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoutePilot.Agent;

// Two-phase make-before-break. Each exact file generation is acknowledged by a
// canary which the engine can route ONLY after loading that generation. Canary
// traffic is redirected to a loopback proof listener; it never leaves this PC.
internal sealed class PolicyPublisher : IAsyncDisposable
{
    internal static readonly string[] Routes={"direct","dpi","vpn","blocked"};
    readonly string _root;
    readonly PolicyPorts _ports;
    readonly Func<bool> _current;
    readonly Dictionary<string,JsonObject[]> _visible=new();
    readonly CancellationTokenSource _stop=new();
    readonly byte[] _proof=Guid.NewGuid().ToByteArray();
    readonly TcpListener _listener;
    readonly Task _server;
    public long Version{get;private set;}
    public long Milliseconds{get;private set;}
    public string Phase{get;private set;}="Prepared";
    public PolicyPublisher(string root,PolicyPorts ports,Func<bool> current)
    {
        _root=root;_ports=ports;_current=current;
        foreach(string route in Routes)
        {
            string path=PathFor(route);
            _visible[route]=File.Exists(path)?JsonNode.Parse(File.ReadAllText(path))!["rules"]!.AsArray().OfType<JsonObject>().Where(r=>r["process_path"]!=null).Select(r=>(JsonObject)r.DeepClone()).ToArray():Array.Empty<JsonObject>();
        }
        _listener=new(IPAddress.Loopback,ports.Proof);_listener.Start(16);_server=Serve();
    }
    string PathFor(string route)=>Path.Combine(_root,"runtime","rules","auto-"+route+".json");
    async Task Serve()
    {
        try
        {
            while(!_stop.IsCancellationRequested)
            {
                using var client=await _listener.AcceptTcpClientAsync(_stop.Token);
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);timeout.CancelAfter(400);
                try{await client.GetStream().WriteAsync(_proof,timeout.Token);}catch(Exception ex)when(ex is IOException or OperationCanceledException or SocketException){}
            }
        }
        catch(Exception ex)when(ex is OperationCanceledException or ObjectDisposedException or SocketException){}
    }
    internal static JsonObject Rule(PolicyKey key)=>new(){["domain"]=new JsonArray(key.Domain),["process_path"]=new JsonArray(key.Application),["network"]="tcp",["port"]=new JsonArray(443)};
    static string Key(JsonObject rule)=>rule.ToJsonString(Storage.Wire);
    void Check(CancellationToken stop){stop.ThrowIfCancellationRequested();if(!_current())throw new OperationCanceledException("Routing generation changed.");}
    async Task WriteAndConfirm(string route,JsonObject[] rules,CancellationToken stop)
    {
        Check(stop);
        string marker="v"+Guid.NewGuid().ToString("N")+".routepilot.invalid";
        var all=rules.Select(r=>r.DeepClone()).ToList();
        all.Add(new JsonObject{["domain"]=new JsonArray(marker),["network"]="tcp",["port"]=new JsonArray(443)});
        Storage.Write(PathFor(route),new{version=3,rules=all});_visible[route]=rules;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop,_stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(4));
        while(true)
        {
            Check(stop);deadline.Token.ThrowIfCancellationRequested();
            try
            {
                using var attempt=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);attempt.CancelAfter(350);
                await using var stream=await PolicyTransport.Socks(_ports.Ack,marker,443,attempt.Token);
                byte[] proof=new byte[_proof.Length];await stream.ReadExactlyAsync(proof,attempt.Token);
                if(proof.SequenceEqual(_proof))return;
            }
            catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException){}
            await Task.Delay(35,deadline.Token);
        }
    }
    internal async Task Apply(IReadOnlyDictionary<string,JsonObject[]> desired,CancellationToken stop)
    {
        var timer=System.Diagnostics.Stopwatch.StartNew();Phase="Publishing";
        try
        {
            // Nothing is removed until ALL target additions have been observed
            // by the running engine, regardless of its file watcher ordering.
            foreach(string route in Routes)
            {
                var union=_visible[route].Concat(desired[route]).DistinctBy(Key).ToArray();
                await WriteAndConfirm(route,union,stop);
            }
            foreach(string route in Routes)
                if(!_visible[route].Select(Key).Order().SequenceEqual(desired[route].Select(Key).Order()))
                    await WriteAndConfirm(route,desired[route],stop);
            Check(stop);Version++;Phase="Applied";
        }
        catch{Phase="ApplyFailed";throw;}
        finally{Milliseconds=timer.ElapsedMilliseconds;}
    }
    public async ValueTask DisposeAsync(){_stop.Cancel();_listener.Stop();await _server;_stop.Dispose();}
}
