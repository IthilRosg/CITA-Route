namespace RoutePilot.Agent;

internal static class NativeConnectorAdmissionTests
{
    internal static async Task Run()
    {
        var admission=new NativeConnectorAdmission();
        if(admission.TryEnter()!=null)throw new IOException("Initial admission must be closed.");
        admission.Open();using var held=admission.TryEnter()??throw new IOException("Open admission refused.");
        using(var deadline=new CancellationTokenSource(TimeSpan.FromMilliseconds(40)))
        {
            bool timedOut=false;try{await admission.CloseAndDrainAsync(deadline.Token);}catch(OperationCanceledException){timedOut=true;}
            if(!timedOut||!held.Cancellation.IsCancellationRequested||admission.TryEnter()!=null)throw new IOException("Timed-out drain must cancel setup and remain closed.");
        }
        bool premature=false;try{admission.Open();}catch(InvalidOperationException){premature=true;}
        if(!premature)throw new IOException("Admission reopened before pending setup retired.");
        held.Dispose();
        bool unconfirmed=false;try{admission.Open();}catch(InvalidOperationException){unconfirmed=true;}
        if(!unconfirmed)throw new IOException("Timed-out drain was not explicitly confirmed before reopen.");
        await admission.CloseAndDrainAsync(CancellationToken.None);
        admission.Open();using var established=admission.TryEnter()??throw new IOException("Admission did not reopen after retirement.");
        var prior=established.Cancellation;established.Dispose();
        await admission.CloseAndDrainAsync(CancellationToken.None);
        if(!prior.IsCancellationRequested||admission.TryEnter()!=null)throw new IOException("Close did not revoke the admission generation.");
        admission.Open();using var next=admission.TryEnter()??throw new IOException("Next generation unavailable.");
        if(next.Cancellation.IsCancellationRequested)throw new IOException("Previous close cancelled the next generation.");
    }
}
