using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RoutePilot.Agent;

// Experimental lifecycle coordinator. An optional owned core adapter quarantines
// native flows before retirement and validates activation before publication.
internal sealed class NativeAwgProfiles : IAsyncDisposable
{
    readonly string image;
    readonly SemaphoreSlim gate=new(1);
    readonly object sync=new();
    readonly HashSet<string> sessions=new(),credentials=new();
    readonly Func<CancellationToken,Task>? beforeStart;
    readonly Func<CancellationToken,Task>? beforePublish;
    readonly Func<CancellationToken,Task>? beforeReply;
    readonly INativeAwgBinding? binding;
    readonly bool persistentControl;
    NativeAwgBackend? active;
    (string Session,ulong Generation)? identity,desired;
    CancellationTokenSource? operation;
    ulong highWater;
    long intent;
    string phase="Stopped";
    bool disposed,retirementFailed;
    internal NativeAwgProfiles(string image,Func<CancellationToken,Task>? beforeStart=null,Func<CancellationToken,Task>? beforePublish=null,Func<CancellationToken,Task>? beforeReply=null,INativeAwgBinding? binding=null,bool persistentControl=false){this.image=Path.GetFullPath(image);this.beforeStart=beforeStart;this.beforePublish=beforePublish;this.beforeReply=beforeReply;this.binding=binding;this.persistentControl=persistentControl;}
    internal object Snapshot(){lock(sync)return new{phase,session=identity?.Session,generation=identity?.Generation,desiredSession=desired?.Session,desiredGeneration=desired?.Generation,highWater,processId=active?.ProcessId??0,retirementFailed};}
    static JsonElement Command((string Session,ulong Generation) id,string action)=>JsonSerializer.SerializeToElement(new{Session=id.Session,Generation=id.Generation,Action=action});
    static void Cancel(CancellationTokenSource? source){try{source?.Cancel();}catch(ObjectDisposedException){}}
    void Check(long ticket,CancellationToken stop){lock(sync){if(retirementFailed)throw new InvalidOperationException("Transport retirement is unconfirmed.");if(disposed||ticket!=intent||stop.IsCancellationRequested)throw new OperationCanceledException(stop);}}
    async Task RetireAsync()
    {
        NativeAwgBackend? old; (string Session,ulong Generation)? id;
        lock(sync){old=active;id=identity;if(old!=null)phase="Retiring";}
        if(old==null)return;
        bool confirmed=false,closed=false;
        try{await old.SendAsync(Command(id!.Value,"Stop"),CancellationToken.None);confirmed=true;}catch{}
        try{await old.DisposeAsync();closed=true;}catch{}
        lock(sync){if(closed){active=null;identity=null;}if(!confirmed||!closed){retirementFailed=true;phase="RetirementFailed";}}
        if(!confirmed||!closed)throw new IOException("Old transport retirement was not confirmed.");
    }
    async Task StopResourcesAsync()
    {
        bool failed=false;
        if(binding!=null)try{await binding.CloseAsync(CancellationToken.None);}catch{failed=true;}
        try{await RetireAsync();}catch{failed=true;}
        if(failed){lock(sync){retirementFailed=true;phase="RetirementFailed";}throw new IOException("Native route retirement was not confirmed.");}
    }
    internal async Task<JsonElement> SwitchAsync(JsonElement spec,CancellationToken stop)
    {
        var next=NativeAwgBackend.ValidateStart(spec);
        string credential=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(spec.GetProperty("Token").GetString()!)));
        long ticket;CancellationTokenSource mine;CancellationTokenSource? prior;
        lock(sync)
        {
            if(disposed||retirementFailed)throw new InvalidOperationException("Profile coordinator is unavailable.");
            if(sessions.Count>=1024)throw new InvalidOperationException("Start a fresh native controller before further profile switches.");
            if(next.Generation<=highWater||sessions.Contains(next.Session)||credentials.Contains(credential))throw new InvalidDataException("Profile identity must advance and rotate credentials.");
            highWater=next.Generation;sessions.Add(next.Session);credentials.Add(credential);
            ticket=++intent;desired=next;phase="Preparing";prior=operation;operation=mine=CancellationTokenSource.CreateLinkedTokenSource(stop);
        }
        Cancel(prior);
        bool acquired=false,retired=false,bindingTouched=false;NativeAwgBackend? candidate=null;
        try
        {
            await gate.WaitAsync(mine.Token);acquired=true;Check(ticket,mine.Token);
            await NativeAwgBackend.PreflightAsync(image,spec,mine.Token);Check(ticket,mine.Token);
            if(binding!=null){bindingTouched=true;await binding.QuarantineAsync(spec,mine.Token);Check(ticket,mine.Token);}
            await RetireAsync();retired=true;Check(ticket,mine.Token);
            lock(sync)phase="Starting";
            if(beforeStart!=null)await beforeStart(mine.Token);
            Check(ticket,mine.Token);
            candidate=new NativeAwgBackend(image,persistentControl);
            var ready=await candidate.StartAsync(spec,mine.Token);
            if(beforePublish!=null)await beforePublish(mine.Token);
            Check(ticket,mine.Token);
            if(binding!=null)await binding.ActivateAsync(candidate,spec,ready,mine.Token);
            lock(sync)
            {
                if(disposed||ticket!=intent||mine.IsCancellationRequested)throw new OperationCanceledException(mine.Token);
                active=candidate;identity=next;phase="Running";candidate=null;
            }
            return ready;
        }
        catch
        {
            if(bindingTouched){await StopResourcesAsync();retired=true;}
            lock(sync)if(ticket==intent&&!retirementFailed){if(!retired&&active!=null){desired=identity;phase="Running";}else phase="Stopped";}
            throw;
        }
        finally
        {
            try{if(candidate!=null)await candidate.DisposeAsync();}
            catch{lock(sync){active=candidate;identity=next;retirementFailed=true;phase="RetirementFailed";}throw;}
            finally{if(acquired)gate.Release();lock(sync)if(ReferenceEquals(operation,mine))operation=null;mine.Dispose();}
        }
    }
    internal async Task<JsonElement> SendAsync(JsonElement command,CancellationToken stop)
    {
        await gate.WaitAsync(stop);
        try
        {
            NativeAwgBackend backend;long ticket;
            lock(sync)
            {
                if(disposed||phase!="Running"||active==null||identity==null||desired!=identity)throw new InvalidOperationException("Profile transition is active.");
                NativeAwgBackend.ValidateCommand(command,identity.Value.Session,identity.Value.Generation);
                if(command.GetProperty("Action").GetString()=="Stop")throw new InvalidOperationException("Stop must use the profile coordinator.");
                backend=active;
                ticket=intent;
            }
            var reply=await backend.SendAsync(command,stop);
            if(beforeReply!=null)await beforeReply(stop);
            lock(sync)if(disposed||intent!=ticket||active!=backend||phase!="Running")throw new InvalidDataException("Stale profile reply discarded.");
            return reply;
        }
        finally{gate.Release();}
    }
    internal async Task StopAsync(string session,ulong generation)
    {
        CancellationTokenSource? pending;long ticket;
        lock(sync)
        {
            if(desired!=(session,generation))throw new InvalidDataException("Stale profile Stop refused.");
            ticket=++intent;desired=null;phase="Stopping";pending=operation;
        }
        Cancel(pending);await gate.WaitAsync();
        try{lock(sync)if(ticket!=intent)return;await StopResourcesAsync();lock(sync)if(ticket==intent&&!retirementFailed)phase="Stopped";}
        finally{gate.Release();}
    }
    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? pending;
        lock(sync){if(disposed)return;disposed=true;++intent;desired=null;pending=operation;phase="Stopping";}
        Cancel(pending);await gate.WaitAsync();
        try{await StopResourcesAsync();lock(sync)if(!retirementFailed)phase="Stopped";}
        finally{gate.Release();}
    }
}
