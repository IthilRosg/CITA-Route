namespace RoutePilot.Agent;

internal static class PolicyStartupTests
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    internal static async Task Run()
    {
        int attempts=0;
        await PolicyWorker.InitializePublication(_=>++attempts switch{
            1=>Task.FromException(new TaskCanceledException("Engine warming up")),
            2=>Task.FromException(new IOException("Acknowledgement unavailable")),
            _=>Task.CompletedTask},()=>true,CancellationToken.None);
        Assert(attempts==3,"Transient startup failures require a later real acknowledgement.");
        bool current=true,cancelled=false;attempts=0;
        try{await PolicyWorker.InitializePublication(_=>{attempts++;current=false;throw new IOException("Old generation");},()=>current,CancellationToken.None);}
        catch(OperationCanceledException){cancelled=true;}
        Assert(cancelled&&attempts==1,"A replaced generation must never retry publication.");
        using(var stop=new CancellationTokenSource())
        {
            attempts=0;cancelled=false;
            try{await PolicyWorker.InitializePublication(_=>{attempts++;stop.Cancel();throw new IOException("Stop");},()=>true,stop.Token);}
            catch(OperationCanceledException){cancelled=true;}
            Assert(cancelled&&attempts==1,"Stop must cancel initial retry immediately.");
        }
        attempts=0;cancelled=false;
        try{await PolicyWorker.InitializePublication(async token=>{attempts++;await Task.Delay(Timeout.Infinite,token);},()=>true,CancellationToken.None,TimeSpan.FromMilliseconds(150));}
        catch(OperationCanceledException){cancelled=true;}
        Assert(cancelled&&attempts==1,"A stalled acknowledgement must obey the shared startup deadline.");
        bool refused=false;
        try{await PolicyWorker.InitializePublication(_=>throw new UnauthorizedAccessException("Protected input denied"),()=>true,CancellationToken.None);}
        catch(UnauthorizedAccessException){refused=true;}
        Assert(refused,"A permissions failure is not engine warmup.");
        current=true;cancelled=false;
        try{await PolicyWorker.InitializePublication(_=>{current=false;return Task.CompletedTask;},()=>current,CancellationToken.None);}
        catch(OperationCanceledException){cancelled=true;}
        Assert(cancelled,"An acknowledgement from a retired generation must not become ready.");
        Console.WriteLine("PASS: initial policy acknowledgement retry, Stop priority, generation changes, bounded stall and protected-input refusal.");
    }
}
