using System.Text;

namespace RoutePilot.Agent;

internal static class NativeForcedIntentTests
{
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    internal static void Run()
    {
        var rule=new NativeForcedIntentRule("Example.COM.",@"C:\Browser\App.exe","tcp",443,"zapret");
        var input=new NativeForcedIntentDocument(1,7,[rule]);byte[] bytes=NativeForcedIntent.Encode(input);
        var restored=NativeForcedIntent.Parse(bytes);
        Check(restored.Revision==7&&restored.Rules.Length==1&&restored.Rules[0].Domain=="example.com"&&restored.Rules[0].Application==@"c:\browser\app.exe","Forced intent did not survive canonical round-trip.");
        string json=Encoding.UTF8.GetString(bytes);
        Check(!json.Contains("Address",StringComparison.Ordinal)&&!json.Contains("Expires",StringComparison.Ordinal),"Ephemeral address or TTL persisted as intent.");
        void Reject(NativeForcedIntentDocument document){bool failed=false;try{NativeForcedIntent.Encode(document);}catch(InvalidDataException){failed=true;}Check(failed,"Unsafe forced intent accepted.");}
        Reject(input with{Revision=0});Reject(input with{Rules=[rule,rule with{Domain="example.com",Application=@"c:\browser\app.exe"}]});
        Reject(input with{Rules=[rule with{Network="udp"}]});Reject(input with{Rules=[rule with{Application=""}]});
        Reject(input with{Rules=[rule with{Domain="127.0.0.1"}]});Reject(input with{Rules=[rule with{Application="relative.exe"}]});
        Reject(input with{Rules=[rule with{Domain="example.com.."}]});Reject(input with{Rules=[rule with{Application=@"C:\Browser\..\App.exe"}]});
        bool duplicate=false;try{NativeForcedIntent.Parse(Encoding.UTF8.GetBytes(json.Replace("\"Schema\":1","\"Schema\":1,\"schema\":1")));}catch(InvalidDataException){duplicate=true;}Check(duplicate,"Duplicate intent field accepted.");
        duplicate=false;try{NativeForcedIntent.Parse(Encoding.UTF8.GetBytes(json.Replace("\"Route\":\"zapret\"","\"Route\":\"zapret\",\"Route\":\"vpn\"")));}catch(InvalidDataException){duplicate=true;}Check(duplicate,"Duplicate rule field accepted.");
        bool unknown=false;try{NativeForcedIntent.Parse(Encoding.UTF8.GetBytes(json.Replace("\"Schema\":1","\"Schema\":1,\"Unknown\":true")));}catch(System.Text.Json.JsonException){unknown=true;}Check(unknown,"Unrecognized intent field accepted.");
        bool truncated=false;try{NativeForcedIntent.Parse(bytes.AsSpan(0,bytes.Length-1));}catch(System.Text.Json.JsonException){truncated=true;}Check(truncated,"Truncated intent accepted.");
        Console.WriteLine("PASS: bounded forced user intent, canonical scopes, no ephemeral DNS/TTL, duplicate and malformed document refusal.");
    }
}
