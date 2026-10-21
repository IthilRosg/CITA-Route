namespace RoutePilot.Agent;

// Confidence in a preferred path must outlast a brief recovery burst. This
// state is scoped to one worker (network/session), never persisted as proof.
internal sealed class PolicyStability
{
    internal static readonly TimeSpan ConfirmationDelay=TimeSpan.FromSeconds(15);
    readonly Dictionary<PolicyKey,(string Route,DateTime First,DateTime Checked)> _returns=new();
    static int Rank(string route)=>route switch{"direct"=>0,"dpi"=>1,"vpn"=>2,_=>3};
    internal bool Pending(PolicyKey key)=>_returns.ContainsKey(key);
    internal int Count=>_returns.Count;
    internal void Remove(PolicyKey key)=>_returns.Remove(key);
    internal void Clear()=>_returns.Clear();
    internal bool Accept(PolicyDecision? previous,PolicyDecision candidate,CandidateTiming[] evidence,DateTime now)
    {
        if(previous==null || previous.Key!=candidate.Key || Rank(candidate.Route)>=Rank(previous.Route))
        {Remove(candidate.Key);return true;}
        // Two failed checks of the current route allow immediate recovery.
        // A cancelled or missing check is not evidence of a broken route.
        if(evidence.Any(c=>c.Route==previous.Route && c.Outcome is ProbeOutcome.Unavailable or ProbeOutcome.DnsUnavailable))
        {Remove(candidate.Key);return true;}
        if(_returns.TryGetValue(candidate.Key,out var pending) && pending.Route==candidate.Route &&
            now>=pending.First && now-pending.First<=TimeSpan.FromMinutes(2) && candidate.CheckedUtc>pending.Checked)
        {
            if(candidate.CheckedUtc-pending.First>=ConfirmationDelay){Remove(candidate.Key);return true;}
            return false;
        }
        if(_returns.Count>=PolicyWorker.DecisionLimit)Remove(_returns.Keys.First());
        _returns[candidate.Key]=(candidate.Route,candidate.CheckedUtc,candidate.CheckedUtc);
        return false;
    }
}
