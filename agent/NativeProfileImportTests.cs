namespace RoutePilot.Agent;

internal static class NativeProfileImportTests
{
    internal static async Task Run()
    {
        void Check(bool value,string message){if(!value)throw new Exception(message);}
        foreach(string code in new[]{"NativeHttpsUnavailable","NativeEgressMismatch","NativeUdpUnavailable"})Check(NativeCandidateValidationError.Code(NativeCandidateValidationError.Create(code))==code,"Safe validation diagnostic code must be retained.");
        try{_ = NativeCandidateValidationError.Create("untrusted");throw new Exception("Untrusted validation code accepted.");}catch(ArgumentException){}
        var malformed=new InvalidDataException();malformed.Data["NativeValidationCode"]="untrusted";Check(NativeCandidateValidationError.Code(malformed)=="InvalidDataException","Untrusted diagnostic code must not escape.");
        var original=new NativeProfileImport.Input("[Interface]\r\nAddress = 10.0.0.2/32\r\n[Peer]\r\nAllowedIPs = 0.0.0.0/0\r\n","fixture","192.0.2.2:1234",new[]{"0.0.0.0/0"},1400);
        var lower=NativeProfileImport.LowerMtu(original);
        Check(lower.Mtu==1280&&lower.Config.Contains("[Interface]\r\nMTU = 1280\n")&&!original.Config.Contains("MTU"),"MTU fallback must change only a separate candidate.");
        var explicitMtu=original with{Config=original.Config.Replace("Address =","MTU = 1380\r\nAddress ="),Mtu=1380};
        lower=NativeProfileImport.LowerMtu(explicitMtu);
        Check(lower.Config.Contains("MTU = 1280\n")&&!lower.Config.Contains("MTU = 1380"),"An existing MTU must be replaced once.");
        int calls=0;var first=await NativeProfileImport.WithMtuFallback(original,_=>{calls++;return Task.FromResult("verified");},default);
        Check(calls==1&&ReferenceEquals(first.Input,original),"Healthy profiles must retain their MTU.");
        calls=0;var fallback=await NativeProfileImport.WithMtuFallback(original,p=>{calls++;return p.Mtu==1400?Task.FromException<string>(new NativeProfileImport.HttpsProbeFailure()):Task.FromResult("verified");},default);
        Check(calls==2&&fallback.Input.Mtu==1280&&fallback.Exit=="verified","Failed HTTPS must retry once with a lower MTU.");
        calls=0;try{await NativeProfileImport.WithMtuFallback(original with{Mtu=1200},_=>{calls++;return Task.FromException<string>(new NativeProfileImport.HttpsProbeFailure());},default);throw new Exception("A lower MTU must not be increased.");}catch(NativeProfileImport.HttpsProbeFailure){}Check(calls==1,"Do not retry a profile with an already lower MTU.");
        calls=0;try{await NativeProfileImport.WithMtuFallback(original,_=>{calls++;return Task.FromException<string>(new IOException("UDP fixture"));},default);throw new Exception("Other errors must not change MTU.");}catch(IOException){}Check(calls==1,"UDP or setup errors must not trigger MTU fallback.");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();calls=0;try{await NativeProfileImport.WithMtuFallback(original,_=>{calls++;return Task.FromException<string>(new NativeProfileImport.HttpsProbeFailure());},cancelled.Token);throw new Exception("Cancelled checks must not retry.");}catch(NativeProfileImport.HttpsProbeFailure){}Check(calls==1,"Cancellation must prevent a retry.");
    }
}
