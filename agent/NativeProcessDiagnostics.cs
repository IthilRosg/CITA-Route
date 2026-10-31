using System.IO.Pipes;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RoutePilot.Agent;

// Drain even after the capture budget is exhausted: a noisy child must not
// block on a full stdout pipe. Keep at most 8 KiB, never create a disk log.
internal sealed class NativeProcessDiagnostics
{
    readonly AnonymousPipeServerStream pipe=new(PipeDirection.In,HandleInheritability.Inheritable);
    readonly Task<string> capture;
    int writerClosed;
    internal SafePipeHandle Writer=>pipe.ClientSafePipeHandle;
    internal NativeProcessDiagnostics(){capture=Task.Run(Read);}
    internal void CloseParentWriter(){if(Interlocked.Exchange(ref writerClosed,1)==0)pipe.DisposeLocalCopyOfClientHandle();}
    async Task<string> Read()
    {
        byte[] kept=new byte[8192],buffer=new byte[1024];int length=0;
        try
        {
            int count;while((count=await pipe.ReadAsync(buffer))>0){int take=Math.Min(count,kept.Length-length);buffer.AsSpan(0,take).CopyTo(kept.AsSpan(length));length+=take;}
        }
        catch(Exception error)when(error is IOException or ObjectDisposedException){}
        return Encoding.UTF8.GetString(kept,0,length);
    }
    // Called only after the owned job has no processes left; all child writer
    // handles are then closed. A capture error cannot pretend a live job died.
    internal string Complete()
    {
        CloseParentWriter();
        try{return capture.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();}
        catch(TimeoutException){return "Startup diagnostics did not finish.";}
        finally{pipe.Dispose();}
    }
}
