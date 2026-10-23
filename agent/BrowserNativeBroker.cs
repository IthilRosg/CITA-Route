using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
    internal int AcceptedCompletions{get;private set;}
    internal int ReadyResults{get;private set;}
    int _connections;
    internal int TrustedConnections=>Volatile.Read(ref _connections);
    internal int TrustedResources{get;private set;}
    internal string? ListenerFailure{get;private set;}
    [DllImport("advapi32.dll")]static extern uint SetSecurityInfo(SafePipeHandle handle,int type,uint flags,IntPtr owner,IntPtr group,IntPtr dacl,IntPtr sacl);
    [DllImport("advapi32.dll")]static extern uint GetSecurityInfo(SafePipeHandle handle,int type,uint flags,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
    [DllImport("kernel32.dll")]static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("advapi32.dll")]static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("advapi32.dll")]static extern uint GetSecurityInfo(SafeProcessHandle handle,int type,uint flags,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
    [DllImport("advapi32.dll")]static extern uint SetSecurityInfo(SafeProcessHandle handle,int type,uint flags,IntPtr owner,IntPtr group,IntPtr dacl,IntPtr sacl);
    static void AllowUserQuery()
    {
        // A separate logon of the same account (including VMware's test logon)
        // may not inherit access to a high process. Grant only image/start-time
        // query on THIS process, retaining every existing process permission.
        using var process=Process.GetCurrentProcess();using var caller=WindowsIdentity.GetCurrent();
        if(caller.IsSystem)throw new IOException("SYSTEM browser delegation requires separate acceptance.");
        uint error=GetSecurityInfo(process.SafeHandle,6,4,out _,out _,out _,out _,out var descriptor);if(error!=0)throw new IOException("Browser observer process query security unavailable: "+error);
        try
        {
            int size=checked((int)GetSecurityDescriptorLength(descriptor));if(size<=0||size>65536)throw new IOException("Invalid process security descriptor.");var data=new byte[size];Marshal.Copy(descriptor,data,0,size);var raw=new RawSecurityDescriptor(data,0);
            if(raw.DiscretionaryAcl==null)throw new IOException("Unexpected unrestricted observer process.");
            var acl=new DiscretionaryAcl(false,false,raw.DiscretionaryAcl);acl.AddAccess(AccessControlType.Allow,caller.User!,0x1000,InheritanceFlags.None,PropagationFlags.None);
            var bytes=new byte[acl.BinaryLength];acl.GetBinaryForm(bytes,0);var pinned=GCHandle.Alloc(bytes,GCHandleType.Pinned);
            try{error=SetSecurityInfo(process.SafeHandle,6,4,IntPtr.Zero,IntPtr.Zero,pinned.AddrOfPinnedObject(),IntPtr.Zero);if(error!=0)throw new IOException("Browser observer metadata query grant failed: "+error);}
            finally{pinned.Free();}
        }
        finally{LocalFree(descriptor);}
    }
    internal Task Signal=>_signal??=_queue.Reader.WaitToReadAsync(_stop.Token).AsTask();
    internal static PipeSecurity UserSecurity()
    {
        using var caller=WindowsIdentity.GetCurrent();var user=caller.User??throw new IOException("Browser observer user is unavailable.");
        if(caller.IsSystem)throw new IOException("SYSTEM browser delegation requires separate acceptance.");
        var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user,PipeAccessRights.FullControl,AccessControlType.Allow));
        return security;
    }
    internal static NamedPipeServerStream UserPipe(string name,int instances=4,bool first=true)
    {
        var pipe=NamedPipeServerStreamAcl.Create(name,PipeDirection.InOut,instances,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,32768,32768,UserSecurity(),HandleInheritability.None,first?PipeAccessRights.TakeOwnership:0);
        try
        {
            if(!first){if(!HasMediumLabel(pipe))throw new IOException("Unexpected browser pipe integrity label.");return pipe;}
            // Set only the mandatory label with WRITE_OWNER. Supplying a whole
            // SACL at creation would unnecessarily require SeSecurityPrivilege.
            var label=new RawSecurityDescriptor("S:(ML;;NW;;;ME)").SystemAcl!;byte[] data=new byte[label.BinaryLength];label.GetBinaryForm(data,0);var pinned=GCHandle.Alloc(data,GCHandleType.Pinned);
            try{uint error=SetSecurityInfo(pipe.SafePipeHandle,6,0x10,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,pinned.AddrOfPinnedObject());if(error!=0)throw new IOException("Browser pipe label failed: "+error);}
            finally{pinned.Free();}
            return pipe;
        }
        catch{pipe.Dispose();throw;}
    }
    internal static bool HasMediumLabel(NamedPipeServerStream pipe)
    {
        uint error=GetSecurityInfo(pipe.SafePipeHandle,6,0x10,out _,out _,out _,out var label,out var descriptor);if(error!=0)throw new IOException("Browser pipe label query failed: "+error);
        try
        {
            int size=checked((int)GetSecurityDescriptorLength(descriptor));if(size<=0||size>65536)throw new IOException("Invalid pipe label descriptor.");var data=new byte[size];Marshal.Copy(descriptor,data,0,size);var security=new RawSecurityDescriptor(data,0);var acl=security.SystemAcl;
            if(label==IntPtr.Zero||acl==null||acl.Count!=1)throw new IOException("Unexpected pipe label: "+security.GetSddlForm(AccessControlSections.Audit));
            var ace=acl[0];var bytes=new byte[ace.BinaryLength];ace.GetBinaryForm(bytes,0);
            if((int)ace.AceType!=17||BitConverter.ToUInt32(bytes,4)!=1||new SecurityIdentifier(bytes,8).Value!="S-1-16-8192")throw new IOException("Unexpected pipe label: "+security.GetSddlForm(AccessControlSections.Audit));
            return true;
        }
        finally{LocalFree(descriptor);}
    }
    internal BrowserNativeBroker(string root,string session,PolicyWorker worker)
    {
        _worker=worker;_root=root;_session=session;_pipe="Cita.Browser.v1."+session+"."+Environment.ProcessId;
        var listeners=new List<NamedPipeServerStream>();
        try
        {
            AllowUserQuery();
            for(int i=0;i<4;i++)listeners.Add(UserPipe(_pipe,4,i==0));
            Storage.Write(Path.Combine(root,"runtime/browser-observer.json"),new BrowserObserverState(session,_pipe,ProcessIdentity.From(Process.GetCurrentProcess())));
            _servers=listeners.Select(pipe=>Serve(pipe)).ToArray();
        }
        catch{foreach(var pipe in listeners)pipe.Dispose();_stop.Dispose();throw;}
    }
    async Task Serve(NamedPipeServerStream pipe)
    {
        using(pipe)
        while(!_stop.IsCancellationRequested)
        {
            Client? client=null;bool connected=false;
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);connected=true;
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
            catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or JsonException or ArgumentException){if(client==null&&!_stop.IsCancellationRequested)ListenerFailure=ex.GetType().Name+":"+ex.HResult+":"+ex.Message;try{await Task.Delay(500,_stop.Token);}catch(OperationCanceledException){}}
            finally{if(client!=null)_queue.Writer.TryWrite(new(client,null,null));if(connected)try{pipe.Disconnect();}catch(Exception ex)when(ex is IOException or InvalidOperationException){}}
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
                    string? ticket=client.Links.Count<256?_worker.BeginBrowserResource(item.Domain,client.Browser,item.Method,item.Resource,DateTime.UtcNow,item.ParentDomain,item.ContextId):null;
                    if(ticket!=null){client.Links[item.Id]=new(){Ticket=ticket,Started=Environment.TickCount64};TrustedResources++;}
                    entries.Add(new(item.Id,ticket==null?"ignored":"watching"));continue;
                }
                if(!client.Links.TryGetValue(item.Id,out var link)){entries.Add(new(item.Id,"expired"));continue;}
                if(item.Kind=="finish"){_worker.FinishBrowserResource(link.Ticket);client.Links.Remove(item.Id);entries.Add(new(item.Id,"finished"));continue;}
                if(item.Kind=="complete")
                {
                    if(_worker.CompleteBrowserResource(link.Ticket,DateTime.UtcNow))AcceptedCompletions++;
                    _worker.FinishBrowserResource(link.Ticket);client.Links.Remove(item.Id);entries.Add(new(item.Id,"finished"));continue;
                }
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
