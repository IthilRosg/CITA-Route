using System.Text.Json;
using System.Text.RegularExpressions;
using RoutePilot.Agent;

internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            if(args.Length==2&&args[0] is "--probe-parent" or "--probe-child")
            {
                string root=Path.GetFullPath(args[1]);
                if(!string.Equals(Path.GetDirectoryName(root),@"C:\Program Files\RoutePilot\Agent",StringComparison.OrdinalIgnoreCase)||!Regex.IsMatch(Path.GetFileName(root),@"\Asupervisor-probe-[a-f0-9]{32}\z"))throw new IOException("Probe root refused.");
                if(args[0]=="--probe-child"){Thread.Sleep(Timeout.Infinite);return 1;}
                using var probeJob=new NativeProcessJob();using var probe=probeJob.StartOwned(Environment.ProcessPath!,["--probe-child",root],root);
                string marker=Path.Combine(root,"ready.json");
                File.WriteAllText(marker+".new",JsonSerializer.Serialize(new{pid=probe.Id,started=probe.StartTime.ToUniversalTime().Ticks}));
                File.Move(marker+".new",marker);
                Thread.Sleep(Timeout.Infinite);return 1;
            }
            if(args.Length!=3)throw new ArgumentException("Fixture paths required.");
            string image=Path.GetFullPath(args[0]),target=Path.GetFullPath(args[1]),log=Path.GetFullPath(args[2]);
            string folder=@"C:\CITA-Lab";string leaf=Path.GetFileName(target);
            if(!string.Equals(Path.GetDirectoryName(target),folder,StringComparison.OrdinalIgnoreCase)||!Regex.IsMatch(leaf,@"\Ainstaller-fixture-[a-f0-9]{32}\z")||
                !string.Equals(image,Path.Combine(folder,leaf+".exe"),StringComparison.OrdinalIgnoreCase)||!string.Equals(log,Path.Combine(folder,leaf+".log"),StringComparison.OrdinalIgnoreCase))throw new IOException("Fixture paths refused.");
            using var job=new NativeProcessJob();using var child=job.StartOwned(image,["/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/SP-","/DIR="+target,"/LOG="+log],folder);
            bool finished=child.WaitForExit(55000);int? exit=finished?child.ExitCode:null;
            // Kill only this job, including any guard child; do not leave
            // asynchronous fixture processes after the controller exits.
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));await job.TerminateAsync(deadline.Token);
            if(job.ReadUsage().ActiveProcesses!=0)throw new IOException("Fixture job retirement unconfirmed.");
            Console.WriteLine(JsonSerializer.Serialize(new{finished,exit,jobEmpty=true}));return finished?0:2;
        }
        catch(Exception e){Console.Error.WriteLine(e.GetType().Name+": "+e.Message);return 1;}
    }
}
