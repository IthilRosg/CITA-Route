using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.IO.Pipes;

namespace RoutePilot.Agent;

internal static class NativeConnectorOwnershipTests
{
    sealed class UnconfirmedConnection:INativeConnectorConnection
    {
        public Stream Stream{get;}=new MemoryStream();
        public void CompleteWrites(){}
        public ValueTask DisposeAsync(){Stream.Dispose();return ValueTask.FromException(new NativeConnectorRetirementException(new IOException("Test retirement uncertain.")));}
    }
    internal static async Task Run()
    {
        if(!OperatingSystem.IsWindows())return;
        using var process=Process.GetCurrentProcess();
        using var sessionStop=new CancellationTokenSource();
        var binding=new NativeCoreBinding("fixture","fixture","fixture","fixture","fixture");
        static NativeCoreConnector Connector(Process process,CancellationToken stop)=>new(process,Guid.NewGuid().ToString("N"),()=>true,_=>false,
            (_,_)=>throw new InvalidOperationException("No connection authorized."),stop);
        static void Attach(NativeCoreBinding binding,NativeCoreConnector connector)=>typeof(NativeCoreBinding).GetField("ownedConnector",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(binding,connector);
        var first=Connector(process,sessionStop.Token);first.Start();Attach(binding,first);
        await binding.CloseAsync(CancellationToken.None);
        if(first.Phase!="Closed")throw new IOException("Binding close did not retire its connector.");
        var connectorGate=(SemaphoreSlim)typeof(NativeCoreBinding).GetField("connectorGate",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(binding)!;
        typeof(NativeCoreBinding).GetField("connectorAdmission",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(binding,true);
        await connectorGate.WaitAsync();
        try
        {
            var closing=binding.CloseAsync(CancellationToken.None);
            var starting=binding.StartOwnedConnectorAsync(Guid.NewGuid().ToString("N"),()=>true,_=>false,(_,_)=>throw new InvalidOperationException(),sessionStop.Token,CancellationToken.None);
            if(closing.IsCompleted||starting.IsCompleted)throw new IOException("Connector admission raced an in-progress close.");
            connectorGate.Release();await closing;
            bool blocked=false;try{await starting;}catch(InvalidOperationException){blocked=true;}
            if(!blocked)throw new IOException("Connector admission survived close.");
        }
        finally{if(connectorGate.CurrentCount==0)connectorGate.Release();}
        var second=Connector(process,sessionStop.Token);second.Start();Attach(binding,second);
        bool refused=false;
        try{await binding.QuarantineAsync(JsonSerializer.SerializeToElement(new{}),CancellationToken.None);}catch(InvalidDataException){refused=true;}catch(KeyNotFoundException){refused=true;}
        if(!refused||second.Phase!="Listening")throw new IOException("Invalid quarantine disturbed the current connector.");
        var valid=JsonSerializer.SerializeToElement(new{Session=Guid.NewGuid().ToString("N"),Generation=1,Token=new string('a',64),Config="test"});
        bool unavailable=false;try{await binding.QuarantineAsync(valid,CancellationToken.None);}catch(IOException){unavailable=true;}
        if(!unavailable||second.Phase!="Closed")throw new IOException("Quarantine did not retire its previous connector before changing generation.");
        // Keep one real pipe worker in setup past retirement's deadline. No
        // network socket or packet interception is created by this fixture.
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string epoch=Guid.NewGuid().ToString("N");
        var pending=new NativeCoreConnector(process,epoch,()=>true,_=>true,async(_,token)=>
        {
            entered.TrySetResult();await release.Task;token.ThrowIfCancellationRequested();
            throw new IOException("Test setup did not produce a connection.");
        },sessionStop.Token);
        using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        using(var pipe=new NamedPipeClientStream(".",pending.Endpoint.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous))
        {
            try
            {
                pending.Start();Attach(binding,pending);await pipe.ConnectAsync(deadline.Token);
                var request=new NativeConnectorRequest(epoch,pending.Endpoint.Capability,Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),"example.org",Environment.ProcessPath!,"8.8.8.8",443,"tcp");
                await NativeCoreConnectorTests.WriteRequest(pipe,request,deadline.Token);await entered.Task.WaitAsync(deadline.Token);
                bool timedOut=false;try{await binding.StopOwnedConnectorAsync(deadline.Token);}catch(TimeoutException){timedOut=true;}
                var held=typeof(NativeCoreBinding).GetField("ownedConnector",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(binding);
                if(!timedOut||pending.Phase!="RetirementPending"||!ReferenceEquals(held,pending))throw new IOException("Timed-out retirement did not retain the same connector.");
                bool blocked=false;try{await binding.StartOwnedConnectorAsync(epoch,()=>true,_=>true,(_,_)=>throw new InvalidOperationException(),sessionStop.Token,deadline.Token);}catch(InvalidOperationException){blocked=true;}
                if(!blocked)throw new IOException("Unconfirmed retirement allowed replacement.");
                release.TrySetResult();await binding.StopOwnedConnectorAsync(deadline.Token);
                if(pending.Phase!="Closed"||typeof(NativeCoreBinding).GetField("ownedConnector",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(binding)!=null)throw new IOException("Retry did not observe retirement of the same connector.");
            }
            finally{release.TrySetResult();await binding.StopOwnedConnectorAsync(CancellationToken.None);}
        }
        string failedEpoch=Guid.NewGuid().ToString("N");
        using var failedStop=new CancellationTokenSource();
        var failed=new NativeCoreConnector(process,failedEpoch,()=>true,_=>true,(_,_)=>Task.FromResult<INativeConnectorConnection>(new UnconfirmedConnection()),failedStop.Token);
        Exception? failure=null;
        try
        {
        using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8)))
        {
            failed.Start();Attach(binding,failed);
            using(var pipe=new NamedPipeClientStream(".",failed.Endpoint.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous))
            {
                await pipe.ConnectAsync(deadline.Token);
                var request=new NativeConnectorRequest(failedEpoch,failed.Endpoint.Capability,Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),"example.org",Environment.ProcessPath!,"8.8.8.8",443,"tcp");
                await NativeCoreConnectorTests.WriteRequest(pipe,request,deadline.Token);byte[] ack=new byte[1];await pipe.ReadExactlyAsync(ack,deadline.Token);
            }
            while(!failed.CleanupUnconfirmed){deadline.Token.ThrowIfCancellationRequested();await Task.Delay(10,deadline.Token);}
            for(int attempt=0;attempt<2;attempt++)
            {
                bool quarantined=false;try{await binding.StopOwnedConnectorAsync(deadline.Token);}catch(IOException){quarantined=true;}
                if(!quarantined||failed.Phase!="SocketQuarantined"||!ReferenceEquals(typeof(NativeCoreBinding).GetField("ownedConnector",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(binding),failed))throw new IOException("Unconfirmed socket retirement was not retained.");
            }
            bool disposalRefused=false;try{await binding.DisposeAsync();}catch(IOException){disposalRefused=true;}
            if(!disposalRefused||!ReferenceEquals(typeof(NativeCoreBinding).GetField("ownedConnector",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(binding),failed))throw new IOException("Binding disposal masked an unconfirmed connector retirement.");
            // The fixture connection is in-memory only; Dispose already closed
            // all pipe handles. Detach only this explicitly synthetic object.
            Attach(binding,null!);
        }
        }
        catch(Exception error){failure=error;throw;}
        finally
        {
            failedStop.Cancel();
            try{await failed.DisposeAsync();}
            catch(Exception cleanup)
            {
                if(failure!=null)failure.Data["OwnedPipeCleanupFailure"]=cleanup.GetType().Name;
                else if(cleanup is not IOException||!failed.CleanupUnconfirmed||failed.Phase!="SocketQuarantined")throw;
            }
        }
        await binding.DisposeAsync();
        bool denied=false;
        try{await binding.StartOwnedConnectorAsync(Guid.NewGuid().ToString("N"),()=>true,_=>false,(_,_)=>throw new InvalidOperationException(),sessionStop.Token,CancellationToken.None);}catch(InvalidOperationException){denied=true;}
        if(!denied)throw new IOException("Unavailable core must not own a connector.");

        // Test the recovery entry point without installing a guard or starting
        // a connector. An active PID alone must never authorize this path.
        var guardedBinding=new NativeCoreBinding("fixture","fixture","fixture","fixture","fixture");
        var processField=typeof(NativeCoreBinding).GetField("process",BindingFlags.NonPublic|BindingFlags.Instance)!;
        processField.SetValue(guardedBinding,process);
        try
        {
            int checks=0;bool current=true;
            var recovery=new NativeCoreRecovery(()=>guardedBinding,_=>{checks++;return Task.FromResult(false);});
            typeof(NativeCoreRecovery).GetField("binding",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(recovery,guardedBinding);
            typeof(NativeCoreRecovery).GetProperty("State",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(recovery,"Active");
            bool guardRejected=false;
            try{await recovery.StartOwnedConnectorAsync(process.Id,Guid.NewGuid().ToString("N"),()=>current,_=>false,(_,_)=>throw new InvalidOperationException(),sessionStop.Token,CancellationToken.None);}catch(IOException){guardRejected=true;}
            if(!guardRejected||checks!=1)throw new IOException("Missing guard did not reject connector ownership.");

            var changing=new NativeCoreRecovery(()=>guardedBinding,_=>{current=false;return Task.FromResult(true);});
            typeof(NativeCoreRecovery).GetField("binding",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(changing,guardedBinding);
            typeof(NativeCoreRecovery).GetProperty("State",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(changing,"Active");
            bool staleRejected=false;
            try{await changing.StartOwnedConnectorAsync(process.Id,Guid.NewGuid().ToString("N"),()=>current,_=>false,(_,_)=>throw new InvalidOperationException(),sessionStop.Token,CancellationToken.None);}catch(InvalidOperationException){staleRejected=true;}
            if(!staleRejected)throw new IOException("Stale connector generation survived guard verification.");
        }
        finally{processField.SetValue(guardedBinding,null);}
    }
}
