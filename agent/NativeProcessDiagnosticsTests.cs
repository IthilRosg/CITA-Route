using System.Diagnostics;

namespace RoutePilot.Agent;

internal static class NativeProcessDiagnosticsTests
{
    internal static async Task Run()
    {
        using var job=new NativeProcessJob();var diagnostics=new NativeProcessDiagnostics();Process? process=null;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            process=job.StartOwned(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe"),
                ["/d","/c","for /L %i in (1,1,2000) do @echo CITA-DIAG-LINE"],Path.GetTempPath(),diagnostics.Writer);
            diagnostics.CloseParentWriter();await process.WaitForExitAsync(deadline.Token);
            // Process signalling alone is not a job-empty acknowledgement.
            // Match the production retirement protocol, including descendants.
            await job.TerminateAsync(deadline.Token);
            string output=diagnostics.Complete();
            uint active=job.ReadUsage().ActiveProcesses;
            if(process.ExitCode!=0||output.Length!=8192||!output.StartsWith("CITA-DIAG-LINE",StringComparison.Ordinal)||active!=0)
                throw new Exception($"Owned diagnostic writer failed: exit={process.ExitCode}, bytes={output.Length}, active={active}, sample={output[..Math.Min(80,output.Length)]}");
        }
        finally
        {
            using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await job.TerminateAsync(cleanup.Token);
            diagnostics.Complete();process?.Dispose();
        }
        using(var failedJob=new NativeProcessJob())
        {
            var failedCapture=new NativeProcessDiagnostics();bool refused=false;
            try{using var unexpected=failedJob.StartOwned(Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"),"missing.exe"),[],Path.GetTempPath(),failedCapture.Writer);}
            catch(System.ComponentModel.Win32Exception){refused=true;}
            finally{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await failedJob.TerminateAsync(cleanup.Token);}
            string output=failedCapture.Complete();
            if(!refused||output.Length!=0||failedJob.ReadUsage().ActiveProcesses!=0)throw new Exception("Failed CreateProcess must close its diagnostic writer and leave no child/job ownership.");
        }
        Console.WriteLine("PASS: explicit inherited diagnostic writer, 8 KiB capture budget, continuous drain and atomic child-job cleanup.");
    }
}
