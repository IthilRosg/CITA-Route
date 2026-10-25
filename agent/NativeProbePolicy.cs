using System.Text.Json;

namespace RoutePilot.Agent;

internal static class NativeProbePolicy
{
    internal static bool RetryAnonymousHttps(JsonElement probe,JsonElement owner,int attempts)
    {
        if(attempts!=1||probe.GetProperty("success").GetBoolean()||owner.GetProperty("state").GetString()!="RunningUnverified")return false;
        string? code=probe.TryGetProperty("failureCode",out var value)?value.GetString():null;
        if(code is "Closed" or "Truncated" or "Timeout" or "ConnectionReset" or "ConnectionRefused" or "NetworkUnreachable")return true;
        return code=="HttpStatus"&&probe.TryGetProperty("httpStatus",out var status)&&status.TryGetInt32(out int http)&&http is 500 or 502 or 503 or 504;
    }
}

internal sealed record NativeValidationEvidence(int HttpsAttempts,bool ExitMatched,string? FailureCode,bool UdpVerified,int Epoch,string? ObservedExitHash=null,string? FailurePhase=null);
