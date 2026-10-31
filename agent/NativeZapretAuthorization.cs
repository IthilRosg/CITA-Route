using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace RoutePilot.Agent;

// A bounded authorization candidate for one connector epoch and policy
// revision. Construction is NOT a core policy ACK: a future owner must only
// expose it after the exact published document and guard are confirmed.
internal sealed class NativeZapretAuthorization
{
    readonly record struct Key(string Domain,string Application,string Address);
    readonly Dictionary<Key,long> scopes;
    readonly string epoch;
    readonly ulong revision;
    readonly Func<ulong,bool> current;
    readonly Func<long> ticks;
    NativeZapretAuthorization(string epoch,ulong revision,Dictionary<Key,long> scopes,Func<ulong,bool> current,Func<long> ticks)
    {this.epoch=epoch;this.revision=revision;this.scopes=scopes;this.current=current;this.ticks=ticks;}
    static string Domain(string domain)
    {
        if(domain==null||domain.Length is <1 or >254||domain.Any(c=>c>127))throw new InvalidDataException("Zapret domain missing or unsupported.");
        string normalized=(domain.EndsWith('.')?domain[..^1]:domain).ToLowerInvariant();
        if(normalized.Length is <1 or >253||IPAddress.TryParse(normalized,out _)||normalized.Split('.').Any(label=>!Regex.IsMatch(label,@"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z",RegexOptions.CultureInvariant)))throw new InvalidDataException("Zapret domain refused.");
        return normalized;
    }
    static string Application(string application)
    {
        // Go validates UTF-8 byte length and uses Unicode lowercasing. Until
        // exact cross-runtime behavior is tested, non-ASCII names fail closed.
        if(application==null||application.Length is <3 or >512||!Regex.IsMatch(application,@"\A[a-zA-Z]:\\",RegexOptions.CultureInvariant)||application.Any(c=>c<32||c>126))throw new InvalidDataException("Zapret application refused.");
        return application.ToLowerInvariant();
    }
    internal static NativeZapretAuthorization CreateCandidate(string epoch,ulong revision,IReadOnlyCollection<NativeDomainRule> rules,Func<ulong,bool> current,
        long unixMilliseconds,long monotonicMilliseconds,Func<long>? monotonicClock=null,bool allowForced=false)
    {
        if(epoch==null||!Regex.IsMatch(epoch,@"\A[a-f0-9]{32}\z",RegexOptions.CultureInvariant)||revision==0||rules==null||rules.Count>128||current==null)throw new InvalidDataException("Zapret authorization identity refused.");
        var result=new Dictionary<Key,long>();var ruleKeys=new HashSet<(string Domain,string Application)>();
        foreach(var rule in rules)
        {
            if(rule==null||rule.Route!="zapret"||rule.Forced&&!allowForced||rule.Network!="tcp"||rule.Port!=443||rule.Addresses==null||rule.Addresses.Length is <1 or >4)throw new InvalidDataException("Zapret rule refused.");
            if(rule.CalibrationId!=null&&!NativeRoutePolicyPublisher.ValidCalibrationId(rule.CalibrationId))throw new InvalidDataException("Zapret calibration identity refused.");
            string domain=Domain(rule.Domain),app=Application(rule.Application);
            // All candidates are TCP/443, so domain+app is the entire Go
            // policy key even when their destination sets are disjoint.
            if(!ruleKeys.Add((domain,app)))throw new InvalidDataException("Duplicate Zapret rule refused.");
            long remaining;try{remaining=checked(rule.ExpiresUnixMilliseconds-unixMilliseconds);}catch(OverflowException){throw new InvalidDataException("Zapret rule lifetime refused.");}
            if(remaining is <=0 or >600000)throw new InvalidDataException("Zapret rule lifetime refused.");
            foreach(string address in rule.Addresses)
            {
                if(!IPAddress.TryParse(address,out var ip)||ip.AddressFamily!=AddressFamily.InterNetwork||ip.ToString()!=address||!NativeZapretPlan.Public(ip))throw new InvalidDataException("Zapret address refused.");
                if(!result.TryAdd(new(domain,app,address),checked(monotonicMilliseconds+remaining)))throw new InvalidDataException("Duplicate Zapret scope refused.");
            }
        }
        return new(epoch,revision,result,current,monotonicClock??(()=>Environment.TickCount64));
    }
    internal bool Allows(NativeConnectorRequest request)
    {
        if(request==null||request.Epoch!=epoch||request.Network!="tcp"||request.Port!=443)return false;
        try
        {
            if(!current(revision))return false;
            var key=new Key(Domain(request.Domain),Application(request.Application),request.Address);
            return scopes.TryGetValue(key,out long until)&&ticks()<until&&current(revision);
        }
        catch(Exception){return false;}
    }
}
