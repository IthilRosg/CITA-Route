namespace RoutePilot.Agent;

// Builds a candidate only. Publication/recovery must still reconcile the
// acknowledged core revision and guard scopes before this can be enabled.
internal static class NativePolicyComposition
{
    internal static NativeDomainRule[] Compose(NativeDomainRule[] manual,NativeDomainRule[] automatic,long now,bool allowForced=false)
    {
        if(manual==null||automatic==null||manual.Any(r=>r==null)||automatic.Any(r=>r==null))throw new InvalidDataException("Policy input missing.");
        static NativeDomainRule Copy(NativeDomainRule r)=>r with{Addresses=r.Addresses?.ToArray()!};
        var user=manual.Where(r=>r.ExpiresUnixMilliseconds>now).Select(Copy).ToArray();
        var candidates=automatic.Where(r=>r.ExpiresUnixMilliseconds>now).Select(Copy).ToArray();
        NativeRoutePolicyPublisher.Validate(user,allowZapret:allowForced,allowForced:allowForced);NativeRoutePolicyPublisher.Validate(candidates);
        if(candidates.Any(r=>string.IsNullOrWhiteSpace(r.Application)))throw new InvalidDataException("Automatic wildcard application refused.");
        static string Domain(NativeDomainRule r)=>r.Domain.TrimEnd('.').ToLowerInvariant();
        static bool SameDestination(NativeDomainRule a,NativeDomainRule b)=>Domain(a)==Domain(b)&&a.Network==b.Network&&a.Port==b.Port;
        static bool Covers(NativeDomainRule m,NativeDomainRule a)=>SameDestination(m,a)&&(m.Application.Length==0||string.Equals(m.Application,a.Application,StringComparison.OrdinalIgnoreCase));
        // Core gives exact-app entries precedence over wildcard entries. Suppress
        // covered automatic rules so the explicit manual wildcard stays in force.
        var allowed=candidates.Where(a=>!user.Any(m=>Covers(m,a)))
            .OrderByDescending(a=>a.ExpiresUnixMilliseconds).ThenBy(Domain,StringComparer.Ordinal)
            .ThenBy(a=>a.Application,StringComparer.OrdinalIgnoreCase).ThenBy(a=>a.Network,StringComparer.Ordinal).ThenBy(a=>a.Port)
            .Take(128-user.Length);
        var result=user.Concat(allowed).ToArray();NativeRoutePolicyPublisher.Validate(result);return result;
    }
}
