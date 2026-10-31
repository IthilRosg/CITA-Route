using System.Text.Json;

namespace RoutePilot.Agent;

// Owns the publication/admission boundary. It does not discover a bundle,
// validate a guard, or start a connector; without a bound connector epoch it
// cannot authorize a Zapret request.
internal sealed class NativeZapretPolicyGate
{
    readonly object sync=new();
    readonly SemaphoreSlim serial=new(1);
    readonly NativeConnectorAdmission admission=new();
    string? connectorEpoch;
    Func<bool>? sessionCurrent;
    NativeZapretAuthorization? authorization;
    long version;
    internal NativeConnectorAdmission Admission=>admission;
    internal bool Allows(NativeConnectorRequest request)
    {
        NativeZapretAuthorization? selected;lock(sync)selected=authorization;
        return selected!=null&&selected.Allows(request);
    }
    bool Current(long expected,ulong revision)
    {
        Func<bool>? check;lock(sync){if(version!=expected||sessionCurrent==null)return false;check=sessionCurrent;}
        bool valid;try{valid=check();}catch{return false;}
        lock(sync)return valid&&version==expected&&ReferenceEquals(check,sessionCurrent);
    }
    internal void Bind(string epoch,Func<bool> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if(epoch==null||epoch.Length!=32||epoch.Any(c=>!char.IsAsciiHexDigit(c))||epoch!=epoch.ToLowerInvariant())throw new InvalidDataException("Invalid connector epoch.");
        CancellationTokenSource retired;
        lock(sync){version++;authorization=null;connectorEpoch=epoch;sessionCurrent=current;retired=admission.MarkClosed();}
        retired.Cancel();
    }
    internal void Invalidate()
    {CancellationTokenSource retired;lock(sync){version++;authorization=null;connectorEpoch=null;sessionCurrent=null;retired=admission.MarkClosed();}retired.Cancel();}
    internal async Task PublishAsync(ulong revision,string document,Func<CancellationToken,Task> publish,CancellationToken stop,bool allowForced=false)
    {
        if(revision==0||document==null||publish==null)throw new InvalidDataException("Invalid policy publication.");
        await serial.WaitAsync(stop);
        try
        {
            long attempt;string? epoch;
            lock(sync){attempt=++version;authorization=null;epoch=connectorEpoch;}
            await admission.CloseAndDrainAsync(stop);
            stop.ThrowIfCancellationRequested();
            NativeZapretAuthorization? candidate=null;
            if(epoch!=null)
            {
                using var parsed=JsonDocument.Parse(document);
                var rules=JsonSerializer.Deserialize<NativeDomainRule[]>(parsed.RootElement.GetProperty("Rules").GetRawText())??throw new InvalidDataException("Policy rules missing.");
                var zapret=rules.Where(r=>r.Route=="zapret").ToArray();
                if(zapret.Length!=0)candidate=NativeZapretAuthorization.CreateCandidate(epoch,revision,zapret,r=>Current(attempt,r),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),Environment.TickCount64,allowForced:allowForced);
            }
            // Binding validates the ACK against this revision and exact bytes.
            await publish(stop);
            stop.ThrowIfCancellationRequested();
            if(candidate!=null)
            {
                if(!Current(attempt,revision))throw new IOException("Zapret policy generation changed.");
                lock(sync)
                {
                    if(version!=attempt||connectorEpoch!=epoch||sessionCurrent==null)throw new IOException("Zapret policy generation changed.");
                    authorization=candidate;admission.Open();
                }
            }
        }
        finally{serial.Release();}
    }
}
