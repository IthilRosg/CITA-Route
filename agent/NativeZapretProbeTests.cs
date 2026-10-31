using System.Net;

namespace RoutePilot.Agent;

internal static class NativeZapretProbeTests
{
    sealed class CountedStream:MemoryStream
    {
        internal int ReadBytes;
        internal CountedStream():base(new byte[65536]){}
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken stop=default)
        {var result=base.ReadAsync(buffer,stop);ReadBytes+=result.Result;return result;}
    }
    static void Check(bool value,string message){if(!value)throw new IOException(message);}
    internal static async Task Run()
    {
        long generation=1;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending=NativeZapretSession.ConfirmCachedAsync(()=>Interlocked.Read(ref generation)==1,async _=>{entered.TrySetResult();return await release.Task;},CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));Interlocked.Increment(ref generation);release.TrySetResult(true);
        bool stale=false;try{await pending;}catch(NativeStaleEvidenceException){stale=true;}
        Check(stale,"Guard verification reused a connector invalidated during the wait.");
        foreach(var status in new[]{HttpStatusCode.Forbidden,HttpStatusCode.TooManyRequests,HttpStatusCode.Unauthorized,HttpStatusCode.Found,HttpStatusCode.MovedPermanently,HttpStatusCode.NoContent})
        {
            using var response=new HttpResponseMessage(status){Content=new ByteArrayContent(status==HttpStatusCode.NoContent?[]:[1,2,3])};
            Check(!(await NativeZapretProbe.InspectAsync(response,CancellationToken.None)).Reachable,"Refusal, redirect or empty body became a verified Zapret path.");
        }
        var counted=new CountedStream();
        using(var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(counted)})
            Check((await NativeZapretProbe.InspectAsync(response,CancellationToken.None)).Reachable&&counted.ReadBytes==1024,"Probe must sample a bounded nonempty body.");
        foreach(string invalid in new[]{"user@example.org","example.org/private","example.org?token=private","example.org#fragment","example.org:444","127.0.0.1","example.org\r\nHost: other","https://example.org"})
        {
            bool rejected=false;int dials=0;
            try{await NativeZapretProbe.RunAsync(invalid,_=>{dials++;throw new IOException("Unexpected dial.");},CancellationToken.None);}catch(InvalidDataException){rejected=true;}
            Check(rejected&&dials==0,"A probe accepted a user path, credentials or an invalid destination.");
        }
        using(var canceled=new CancellationTokenSource())
        {
            canceled.Cancel();int dials=0;bool stopped=false;
            try{await NativeZapretProbe.RunAsync("example.org",_=>{dials++;throw new IOException();},canceled.Token);}catch(OperationCanceledException){stopped=true;}
            Check(stopped&&dials==0,"An already canceled probe started a connection.");
        }
        Check(NativeZapretProbe.FailureReason(HttpRequestError.ConnectionError,false,false,false)=="zapret-connect-failed","Connect failure classification incorrect.");
        Check(NativeZapretProbe.FailureReason(HttpRequestError.SecureConnectionError,true,false,false)=="zapret-tls-failed","TLS failure classification incorrect.");
        Check(NativeZapretProbe.FailureReason(HttpRequestError.SecureConnectionError,true,false,true)=="zapret-tls-certificate","Certificate failure classification incorrect.");
        Check(NativeZapretProbe.FailureReason(HttpRequestError.InvalidResponse,true,false,false)=="zapret-http-headers-failed"&&NativeZapretProbe.FailureReason(HttpRequestError.ResponseEnded,true,true,false)=="zapret-http-body-failed","HTTP stage classification incorrect.");
        Check(NativeZapretProbe.TimeoutReason(false,false)=="zapret-connect-timeout"&&NativeZapretProbe.TimeoutReason(true,false)=="zapret-tls-or-headers-timeout"&&NativeZapretProbe.TimeoutReason(true,true)=="zapret-http-body-timeout","Timeout stage classification incorrect.");
        var failedDial=await NativeZapretProbe.RunAsync("example.org",_=>throw new IOException("private worker output"),CancellationToken.None);
        Check(!failedDial.Reachable&&failedDial.Reason=="zapret-connect-failed","Dial failure was misclassified or exposed private output.");
        int timeoutDials=0;
        async Task<INativeConnectorConnection> Waiting(CancellationToken token){Interlocked.Increment(ref timeoutDials);await Task.Delay(Timeout.InfiniteTimeSpan,token);throw new Exception("unreachable");}
        var timeout=await NativeZapretProbe.RunAsync("example.org",Waiting,CancellationToken.None,TimeSpan.FromMilliseconds(100));
        Check(!timeout.Reachable&&timeout.Reason=="zapret-connect-timeout"&&timeoutDials==1,"Probe deadline/retry budget failed.");
        using(var callerStop=new CancellationTokenSource())
        {
            var enteredDial=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<INativeConnectorConnection> WaitForCancel(CancellationToken token){enteredDial.TrySetResult();await Task.Delay(Timeout.InfiniteTimeSpan,token);throw new Exception("unreachable");}
            var request=NativeZapretProbe.RunAsync("example.org",WaitForCancel,callerStop.Token,TimeSpan.FromSeconds(3));
            await enteredDial.Task.WaitAsync(TimeSpan.FromSeconds(2));callerStop.Cancel();bool stopped=false;
            try{await request;}catch(OperationCanceledException){stopped=true;}
            Check(stopped,"Caller cancellation was reported as a probe timeout.");
        }
        foreach(string excluded in new[]{"tls-certificate","http-refused","http-body-empty","legacy-unclassified","reachable"})
            Check(!NativeZapretSession.EligibleFailure(new(false,excluded)),"Non-network evidence enabled a bypass probe.");
        Check(NativeZapretSession.EligibleFailure(new(false,"tls-timeout")),"A classified network failure could not request bounded Zapret calibration.");
        var zapret=new NativeDomainRule("example.org",@"C:\app.exe","tcp","zapret",443,["8.8.8.8"],DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds());
        bool publicRejected=false;try{NativeRoutePolicyPublisher.Validate([zapret]);}catch(InvalidDataException){publicRejected=true;}
        Check(publicRejected,"Uncalibrated user RPC authorized a Zapret path.");NativeRoutePolicyPublisher.Validate([zapret],allowZapret:true);
    }
}
