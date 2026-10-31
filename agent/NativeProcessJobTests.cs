using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Reflection;

namespace RoutePilot.Agent;

internal static class NativeProcessJobTests
{
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool member);
    internal static async Task Run()
    {
        if(!OperatingSystem.IsWindows())return;
        using var job=new NativeProcessJob();
        string shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
        var info=new ProcessStartInfo(shell){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.Environment["CITA_ATOMIC_TEST"]="тест строка";
        foreach(string arg in new[]{"-NoProfile","-NonInteractive","-Command","[Console]::InputEncoding=[Text.UTF8Encoding]::new(); [Console]::OutputEncoding=[Text.UTF8Encoding]::new(); $line=[Console]::In.ReadLine(); [Console]::Out.WriteLine($line); [Console]::Error.WriteLine($env:CITA_ATOMIC_TEST); Start-Sleep -Seconds 60"})info.ArgumentList.Add(arg);
        using(var child=job.StartWithPipes(info))
        {
            if(!IsProcessInJob(child.Process.Handle,new IntPtr(job.DescriptorHandleValue),out bool member)||!member||job.ReadUsage().ActiveProcesses<1)throw new IOException("Child was not created in the owned job.");
            await child.Input.WriteLineAsync("Привет 🚀");
            if(await child.Output.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))!="Привет 🚀"||await child.Error.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))!="тест строка")throw new IOException("Owned pipe, argument or Unicode environment mismatch.");
            await job.TerminateAsync(CancellationToken.None);await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if(job.ReadUsage().ActiveProcesses!=0)throw new IOException("Owned job was not empty.");
        }
        var stalled=new ProcessStartInfo(shell){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{"-NoProfile","-NonInteractive","-Command","Start-Sleep -Seconds 60"})stalled.ArgumentList.Add(arg);
        using var stalledJob=new NativeProcessJob();
        using(var child=stalledJob.StartWithPipes(stalled))
        {
            Task<string?>? read=null;Task? write=null;
            try
            {
                using var deadline=new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                read=child.Output.ReadLineAsync(deadline.Token).AsTask();
                write=child.Input.WriteAsync(new string('x',4*1024*1024).AsMemory(),deadline.Token);
                try{await Task.WhenAll(read,write).WaitAsync(TimeSpan.FromSeconds(4));}catch(OperationCanceledException){}
                if(child.Process.HasExited||!read.IsCanceled||!write.IsCanceled)throw new IOException("Stalled pipe operations did not become terminal cancellation while the child was alive.");
            }
            finally
            {
                await stalledJob.TerminateAsync(CancellationToken.None);
                if(read!=null&&write!=null)try{await Task.WhenAll(read,write).WaitAsync(TimeSpan.FromSeconds(5));}catch(OperationCanceledException){}
            }
            if(stalledJob.ReadUsage().ActiveProcesses!=0)throw new IOException("Stalled job retirement unconfirmed.");
        }
        info.Arguments="unsupported";bool rejected=false;
        try{using var ignored=job.StartWithPipes(info);}catch(ArgumentException){rejected=true;}
        if(!rejected)throw new IOException("Unsupported startup options were not rejected.");
        info.Arguments="";info.FileName=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")+".exe");rejected=false;
        try{using var ignored=job.StartWithPipes(info);}catch(Win32Exception){rejected=true;}
        if(!rejected||job.ReadUsage().ActiveProcesses!=0)throw new IOException("Failed startup left an owned child.");
        using var probeJob=new NativeProcessJob();Process? held=null;
        NativeChildProcess LaunchProbe(ProcessStartInfo start)
        {
            start.ArgumentList.Clear();foreach(string arg in new[]{"-NoProfile","-NonInteractive","-Command","Start-Sleep -Seconds 60"})start.ArgumentList.Add(arg);
            var child=probeJob.StartWithPipes(start);held=Process.GetProcessById(child.Process.Id);_=held.Handle;return child;
        }
        await using(var binding=new NativeCoreBinding(shell,shell,shell,new string('a',32),"",launchOwned:LaunchProbe))
        {
            try
            {
                using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                var method=typeof(NativeCoreBinding).GetMethod("ProbeAsync",BindingFlags.Instance|BindingFlags.NonPublic)??throw new IOException("Probe method missing.");
                var task=(Task<JsonElement>)method.Invoke(binding,new object[]{"--probe",new{payload=new string('x',4*1024*1024)},cancel.Token})!;
                bool cancelled=false;try{await task.WaitAsync(TimeSpan.FromSeconds(8));}catch(OperationCanceledException){cancelled=true;}
                for(int attempt=0;attempt<20&&probeJob.ReadUsage().ActiveProcesses!=0;attempt++)await Task.Delay(100);
                if(!cancelled||!task.IsCompleted||held==null||!held.HasExited||probeJob.ReadUsage().ActiveProcesses!=0)throw new IOException($"Cancelled real probe retirement: cancelled={cancelled}, terminal={task.IsCompleted}, hasChild={held!=null}, exited={held?.HasExited}, active={probeJob.ReadUsage().ActiveProcesses}.");
            }
            finally{held?.Dispose();}
        }
    }
}
