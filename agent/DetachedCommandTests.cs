using System.Diagnostics;

namespace RoutePilot.Agent;

internal static class DetachedCommandTests
{
    sealed record Child(int Pid,DateTime StartedUtc);
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    internal static async Task Run()
    {
        string image=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe");
        string root=Path.Combine(Path.GetTempPath(),"cita-detached-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);string receipt=Path.Combine(root,"child.json");
        Process? child=null;DateTime started=default;
        try
        {
            string script="$p=Start-Process -FilePath '"+image.Replace("'","''")+"' -ArgumentList @('-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 20') -NoNewWindow -PassThru;"+
                "@{Pid=$p.Id;StartedUtc=$p.StartTime.ToUniversalTime().ToString('o')}|ConvertTo-Json|Set-Content -Encoding UTF8 -LiteralPath '"+receipt.Replace("'","''")+"';exit 7";
            var timer=Stopwatch.StartNew();
            var result=await Tools.Run(image,new[]{"-NoProfile","-NonInteractive","-Command",script},10,captureOutput:false);
            var identity=Storage.Read<Child>(receipt);started=identity.StartedUtc.ToUniversalTime();child=Process.GetProcessById(identity.Pid);
            Assert(result.Code==7&&result.Text.Length==0,"Discarded command output must retain the primary exit code.");
            Assert(timer.Elapsed<TimeSpan.FromSeconds(10)&&!child.HasExited,"A background worker must not hold primary command completion open.");
            using var stop=new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            bool cancelled=false;
            try{await Tools.Run(image,new[]{"-NoProfile","-NonInteractive","-Command","Start-Sleep -Seconds 20"},10,stop.Token,captureOutput:false);}
            catch(OperationCanceledException){cancelled=true;}
            Assert(cancelled,"Discarding output must preserve cancellation of the owned command.");
            Console.WriteLine("PASS: detached startup command completes with a live child, preserves exit status and obeys cancellation.");
        }
        finally
        {
            if(child==null&&File.Exists(receipt))
            {
                var identity=Storage.Read<Child>(receipt);started=identity.StartedUtc.ToUniversalTime();
                try{child=Process.GetProcessById(identity.Pid);}catch(ArgumentException){}
            }
            if(child!=null)
            {
                try{if(!child.HasExited&&Math.Abs((child.StartTime.ToUniversalTime()-started).TotalMilliseconds)<100&&string.Equals(child.MainModule?.FileName,image,StringComparison.OrdinalIgnoreCase)){child.Kill();await child.WaitForExitAsync();}}
                finally{child.Dispose();}
            }
            if(!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(),"cita-detached-")),StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe fixture cleanup.");
            Directory.Delete(root,true);
        }
    }
}
