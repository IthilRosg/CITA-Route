using System.Text.Json;

namespace RoutePilot.Agent;

internal static class NativeDynamicSocketLeaseTests
{
    internal static void Run()
    {
        const string run="0123456789abcdef0123456789abcdef",lease=run+run;
        using(var valid=JsonDocument.Parse(JsonSerializer.Serialize(new{success=true,run,stage="SystemLeaseCurrent",socketLease=lease})))NativeDynamicSocketLease.ValidateAcknowledgement(valid.RootElement,run,lease,"SystemLeaseCurrent");
        foreach(string malformed in new[]{"{}","null","[]","{\"success\":true}","{\"success\":\"true\"}",JsonSerializer.Serialize(new{success=true,run,stage="SystemLeaseHeld",socketLease=lease}),JsonSerializer.Serialize(new{success=true,run,stage="SystemLeaseCurrent",socketLease=run})})
        {
            using var reply=JsonDocument.Parse(malformed);bool refused=false;
            try{NativeDynamicSocketLease.ValidateAcknowledgement(reply.RootElement,run,lease,"SystemLeaseCurrent");}catch(IOException){refused=true;}
            if(!refused)throw new Exception("Malformed/stale heartbeat did not become a controlled path failure.");
        }
        Console.WriteLine("PASS: dynamic socket heartbeat rejects missing/wrong fields, stale stage and mismatched socket identity without faulting cleanup.");
    }
}
