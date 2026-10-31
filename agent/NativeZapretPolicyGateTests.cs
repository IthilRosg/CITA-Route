using System.Text.Json;

namespace RoutePilot.Agent;

internal static class NativeZapretPolicyGateTests
{
    internal static async Task Run()
    {
        const string epoch="0123456789abcdef0123456789abcdef",app=@"C:\apps\browser.exe";
        var rule=new NativeDomainRule("example.org",app,"tcp","zapret",443,["8.8.8.8"],DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds());
        string document=JsonSerializer.Serialize(new{Epoch=epoch,Rules=new[]{rule}});
        var request=new NativeConnectorRequest(epoch,"","",0,"example.org",app,"8.8.8.8",443,"tcp");
        static void Check(bool value,string message){if(!value)throw new IOException(message);}
        var gate=new NativeZapretPolicyGate();gate.Bind(epoch,()=>true);bool published=false;
        await gate.PublishAsync(1,document,_=>{published=true;return Task.CompletedTask;},CancellationToken.None);
        Check(published&&gate.Allows(request),"Confirmed policy did not authorize its exact scope.");
        using var pending=gate.Admission.TryEnter()??throw new IOException("Confirmed admission did not open.");
        using(var timeout=new CancellationTokenSource(TimeSpan.FromMilliseconds(40)))
        {
            bool called=false,timedOut=false;try{await gate.PublishAsync(2,document,_=>{called=true;return Task.CompletedTask;},timeout.Token);}catch(OperationCanceledException){timedOut=true;}
            Check(timedOut&&!called&&!gate.Allows(request)&&pending.Cancellation.IsCancellationRequested,"Policy was published before pending admission retired.");
        }
        pending.Dispose();
        bool badAck=false;try{await gate.PublishAsync(2,document,_=>throw new IOException("Bad ACK"),CancellationToken.None);}catch(IOException){badAck=true;}
        Check(badAck&&!gate.Allows(request)&&gate.Admission.TryEnter()==null,"Bad ACK opened admission.");
        using(var canceled=new CancellationTokenSource())
        {
            bool canceledAfterAck=false;ulong confirmedRevision=1;
            try{await gate.PublishAsync(2,document,_=>{confirmedRevision=2;canceled.Cancel();return Task.CompletedTask;},canceled.Token);}catch(OperationCanceledException){canceledAfterAck=true;}
            Check(canceledAfterAck&&confirmedRevision==2&&!gate.Allows(request)&&gate.Admission.TryEnter()==null,"ACK after cancellation lost its floor or reopened admission.");
            await gate.PublishAsync(confirmedRevision+1,document,_=>Task.CompletedTask,CancellationToken.None);
            Check(gate.Allows(request),"The next revision could not follow a confirmed ACK after cancellation.");
        }
        bool stale=false;try{await gate.PublishAsync(2,document,_=>{gate.Invalidate();return Task.CompletedTask;},CancellationToken.None);}catch(IOException){stale=true;}
        Check(stale&&!gate.Allows(request)&&gate.Admission.TryEnter()==null,"Changed generation reopened admission.");
        // An initially unbound policy publication cannot open the connector
        // that appears while its ACK is still pending.
        var unbound=new NativeZapretPolicyGate();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishing=unbound.PublishAsync(1,document,async _=>{entered.TrySetResult();await finish.Task;},CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));unbound.Bind(epoch,()=>true);finish.TrySetResult();await publishing;
            Check(!unbound.Allows(request)&&unbound.Admission.TryEnter()==null,"Late bind bypassed policy publication fence.");
            await unbound.PublishAsync(2,document,_=>Task.CompletedTask,CancellationToken.None);Check(unbound.Allows(request),"Bound policy did not open after its own ACK.");
        }
        finally{finish.TrySetResult();}

        // Delay old-generation cancellation after it has atomically marked
        // closure, then publish and open a new generation in the meantime.
        var interleaved=new NativeZapretPolicyGate();interleaved.Bind(epoch,()=>true);
        await interleaved.PublishAsync(1,document,_=>Task.CompletedTask,CancellationToken.None);
        var old=interleaved.Admission.TryEnter()??throw new IOException("Old generation unavailable.");
        using var cancelEntered=new ManualResetEventSlim();using var cancelRelease=new ManualResetEventSlim();
        using var registration=old.Cancellation.Register(()=>{old.Dispose();cancelEntered.Set();cancelRelease.Wait(TimeSpan.FromSeconds(5));});
        string otherEpoch="abcdef0123456789abcdef0123456789";
        var rebinding=Task.Run(()=>interleaved.Bind(otherEpoch,()=>true));
        try
        {
            Check(cancelEntered.Wait(TimeSpan.FromSeconds(3)),"Old cancellation barrier did not start.");
            using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await interleaved.PublishAsync(2,document,_=>Task.CompletedTask,budget.Token);
            var newRequest=request with{Epoch=otherEpoch};Check(interleaved.Allows(newRequest),"New generation was not opened.");
            cancelRelease.Set();await rebinding.WaitAsync(budget.Token);
            using var fresh=interleaved.Admission.TryEnter();
            Check(interleaved.Allows(newRequest)&&fresh!=null&&!fresh.Cancellation.IsCancellationRequested,"Old cancellation closed a new generation.");
        }
        finally{cancelRelease.Set();old.Dispose();await rebinding;}
    }
}
