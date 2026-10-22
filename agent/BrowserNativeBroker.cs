using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;

namespace RoutePilot.Agent;

internal sealed record BrowserObserverState(string SessionId,string Pipe,ProcessIdentity Owner);
internal sealed class BrowserNativeBroker:IAsyncDisposable
{
    sealed class Link{internal string? Ticket;internal BrowserFeedbackWatch? Watch;internal long Started;}
    sealed class Client(string browser){internal readonly string Browser=browser;internal readonly Dictionary<string,Link> Links=new();}
    sealed record Work(Client Client,BrowserNativeBatch? Batch,TaskCompletionSource<BrowserNativeReply>? Completion);
    readonly PolicyWorker _worker;readonly string _root,_session,_pipe;readonly CancellationTokenSource _stop=new();
    readonly Channel<Work> _queue=Channel.CreateBounded<Work>(new BoundedChannelOptions(16){SingleReader=true,SingleWriter=false,FullMode=BoundedChannelFullMode.Wait});
    readonly Task[] _servers;Task? _signal;
    internal int AcceptedErrors{get;private set;}
    internal int ReadyResults{get;private set;}
    int _connections;
    internal int TrustedConnections=>Volatile.Read(ref _connections);
    internal int TrustedResources{get;private set;}
    internal Task Signal=>_signal??=_queue.Reader.WaitToReadAsync(_stop.Token).AsTask();
    internal BrowserNativeBroker(string root,string session,PolicyWorker worker)
    {
        _worker=worker;_root=root;_session=session;_pipe="Cita.Browser.v1."+session+"."+Environment.ProcessId;
        Storage.Write(Path.Combine(root,"runtime/browser-observer.json"),new BrowserObserverState(session,_pipe,ProcessIdentity.From(Process.GetCurrentProcess())));
        _servers=Enumerable.Range(0,4).Select(_=>Serve()).ToArray();
    }
    async Task Serve()
    {
        while(!_stop.IsCancellationRequested)
        {
            Client? client=null;
            try
            {
                using var pipe=new NamedPipeServerStream(_pipe,PipeDirection.InOut,4,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly,32768,32768);
                await pipe.WaitForConnectionAsync(_stop.Token);
                if(!BrowserNativeIdentity.GetNamedPipeClientProcessId(pipe.SafePipeHandle,out uint pid)||BrowserNativeIdentity.Browser((int)pid) is not {} browser){await Task.Delay(250,_stop.Token);continue;}
                client=new(browser);Interlocked.Increment(ref _connections);
                while(!_stop.IsCancellationRequested)
                {
                    var data=await BrowserNativeWire.Read(pipe,_stop.Token);if(data==null)break;
                    var batch=BrowserNativeWire.Parse(data);var done=new TaskCompletionSource<BrowserNativeReply>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if(!_queue.Writer.TryWrite(new(client,batch,done)))throw new IOException("Browser observer is busy.");
                    var reply=await done.Task.WaitAsync(TimeSpan.FromSeconds(12),_stop.Token);
                    await BrowserNativeWire.Write(pipe,JsonSerializer.SerializeToUtf8Bytes(reply,BrowserNativeWire.Json),_stop.Token);
                }
            }
            catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or JsonException or ArgumentException){try{await Task.Delay(500,_stop.Token);}catch(OperationCanceledException){}}
            finally{if(client!=null)_queue.Writer.TryWrite(new(client,null,null));}
        }
    }
    internal void Pump()
    {
        while(_queue.Reader.TryRead(out var work))
        {
            if(work.Batch==null){foreach(var link in work.Client.Links.Values)_worker.FinishBrowserResource(link.Ticket);work.Client.Links.Clear();continue;}
            var entries=new List<BrowserNativeEntry>();var client=work.Client;
            foreach(var old in client.Links.Where(p=>Environment.TickCount64<p.Value.Started||Environment.TickCount64-p.Value.Started>90000).Select(p=>p.Key).ToArray())
            {_worker.FinishBrowserResource(client.Links[old].Ticket);client.Links.Remove(old);}
            foreach(var item in work.Batch.Events)
            {
                if(item.Kind=="begin")
                {
                    if(client.Links.Remove(item.Id,out var old))_worker.FinishBrowserResource(old.Ticket);
                    string? ticket=client.Links.Count<256?_worker.BeginBrowserResource(item.Domain,client.Browser,item.Method,item.Resource,DateTime.UtcNow):null;
                    if(ticket!=null){client.Links[item.Id]=new(){Ticket=ticket,Started=Environment.TickCount64};TrustedResources++;}
                    entries.Add(new(item.Id,ticket==null?"ignored":"watching"));continue;
                }
                if(!client.Links.TryGetValue(item.Id,out var link)){entries.Add(new(item.Id,"expired"));continue;}
                if(item.Kind=="finish"){_worker.FinishBrowserResource(link.Ticket);client.Links.Remove(item.Id);entries.Add(new(item.Id,"finished"));continue;}
                if(item.Kind=="fail")
                {
                    if(link.Watch!=null){entries.Add(new(item.Id,"checking"));continue;}
                    link.Watch=link.Ticket==null?null:_worker.WatchBrowserFailure(link.Ticket,item.Error,item.Cancelled,DateTime.UtcNow);link.Ticket=null;
                    if(link.Watch!=null)AcceptedErrors++;else client.Links.Remove(item.Id);
                    entries.Add(new(item.Id,link.Watch==null?"ignored":"checking"));continue;
                }
                bool ready=link.Watch!=null&&_worker.BrowserRepairReady(link.Watch,DateTime.UtcNow);
                entries.Add(new(item.Id,ready?"ready":"checking",ready?link.Watch!.Key.Domain:null));
                if(ready){ReadyResults++;client.Links.Remove(item.Id);}
            }
            work.Completion!.TrySetResult(new(work.Batch.Id,entries.ToArray()));
        }
        if(_signal?.IsCompleted==true)_signal=null;
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();_queue.Writer.TryComplete();await Task.WhenAll(_servers);
        while(_queue.Reader.TryRead(out var pending)){foreach(var link in pending.Client.Links.Values)_worker.FinishBrowserResource(link.Ticket);pending.Completion?.TrySetCanceled();}
        try{string path=Path.Combine(_root,"runtime/browser-observer.json");var state=ComponentLiveness.Read<BrowserObserverState>(path);if(state.SessionId==_session&&state.Owner.Pid==Environment.ProcessId)File.Delete(path);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException){}
        _stop.Dispose();
    }
}
