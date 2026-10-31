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
    internal static bool RetryAnonymousUdp(JsonElement probe,JsonElement owner,int attempts)
    {
        if(attempts!=1||probe.GetProperty("success").GetBoolean()||owner.GetProperty("state").GetString()!="RunningUnverified")return false;
        return probe.TryGetProperty("stage",out var stage)&&stage.GetString()=="UdpQueries"&&
            probe.TryGetProperty("dnsFailure",out var reason)&&reason.GetString()=="ReadTimeout";
    }
}

internal sealed record NativeValidationEvidence(int HttpsAttempts,bool ExitMatched,string? FailureCode,bool UdpVerified,int Epoch,string? ObservedExitHash=null,string? FailurePhase=null,string? UdpFailure=null,int UdpAttempts=0,string? UdpStage=null,int UdpQueries=0,bool UdpWrongSourceNoReply=false,bool UdpInvalidFramesNoReply=false,bool UdpNoReplyAfterClose=false);

// Fixed public diagnostic codes only: never propagate a probe response or address.
internal static class NativeCandidateValidationError
{
    internal static InvalidDataException Create(string code)
    {
        if(code is not ("NativeHttpsUnavailable" or "NativeEgressMismatch" or "NativeUdpUnavailable" or "NativeUdpTimeout"))throw new ArgumentException("Unknown native validation code.");
        var error=new InvalidDataException("Native candidate validation failed.");error.Data["NativeValidationCode"]=code;return error;
    }
    internal static string Code(Exception error)=>error is InvalidDataException&&error.Data["NativeValidationCode"] is string code&&code is "NativeHttpsUnavailable" or "NativeEgressMismatch" or "NativeUdpUnavailable" or "NativeUdpTimeout"?code:error.GetType().Name;
}
