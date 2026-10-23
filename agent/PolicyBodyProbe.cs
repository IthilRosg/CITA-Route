namespace RoutePilot.Agent;

// Bounded evidence of an OWN public root GET. Never certifies another URL.
internal static class PolicyBodyProbe
{
    internal const int Limit=65536;
    internal sealed record Result(bool Success,string Evidence,long Bytes);
    internal static async Task<Result> Read(Stream stream,long? expected,CancellationToken budget,CancellationToken stop,TimeSpan? idle=null)
    {
        byte[] buffer=new byte[4096];long bytes=0;
        try
        {
            while(bytes<Limit)
            {
                using var progress=CancellationTokenSource.CreateLinkedTokenSource(budget);
                progress.CancelAfter(idle??TimeSpan.FromMilliseconds(1200));
                int count=await stream.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,Limit-bytes)),progress.Token);
                if(count==0)return new(expected==null||expected==bytes,expected==null||expected==bytes?"BodyComplete":"BodyIncomplete",bytes);
                bytes+=count;
                if(expected==bytes)return new(true,"BodyComplete",bytes);
            }
            return new(true,"BodySample",bytes);
        }
        catch(Exception ex)when(ex is IOException or OperationCanceledException)
        {
            stop.ThrowIfCancellationRequested();
            return new(false,budget.IsCancellationRequested?"BodyBudgetExceeded":bytes>0?"BodyStalled":"BodyUnknown",bytes);
        }
    }
}
