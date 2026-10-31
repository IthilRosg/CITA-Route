using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace RoutePilot.Agent;

internal static class NativeCoreRecoveryFailureTests
{
    static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Set(object target,string name,object? value)=>target.GetType().GetField(name,Private)!.SetValue(target,value);
    internal static async Task Run()
    {
        if(!OperatingSystem.IsWindows())return;
        await Scenario("CoreRepairFailed",false,false);
        await Scenario("CoreRecoveryUnconfirmed",true,false);
        await Scenario("Closed",false,true);
    }
    static async Task Scenario(string expected,bool badRetirement,bool manualStop)
    {
        string shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
        string session=Guid.NewGuid().ToString("N");
        string script="$line=[Console]::In.ReadLine(); if($null -eq $line){exit}; [Console]::Out.WriteLine('{\"stage\":\"Snapshot\",\"session\":\""+session+"\",\"generation\":1,\"state\":\"Running\"}'); Start-Sleep -Seconds 60";
        var info=new ProcessStartInfo(shell){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{"-NoProfile","-NonInteractive","-Command",script})info.ArgumentList.Add(arg);
        using var ownerJob=new NativeProcessJob();
        using var ownerProcess=Process.Start(info)??throw new IOException("Synthetic owner failed to start.");
        bool attached=false;NativeAwgBackend? owner=null;NativeCoreRecovery? recovery=null;NativeCoreBinding? core=null;
        try
        {
            ownerJob.AssignBlockedOnPrivateInput(ownerProcess);attached=true;
            owner=new NativeAwgBackend(shell,persistentControl:true);Set(owner,"process",ownerProcess);Set(owner,"session",session);Set(owner,"generation",1UL);
            core=new NativeCoreBinding(shell,shell,shell,new string('a',32),"");
            if(badRetirement){var invalid=Process.GetCurrentProcess();invalid.Dispose();Set(core,"process",invalid);}
            recovery=new NativeCoreRecovery(()=>throw new IOException("Synthetic factory failure."),_=>Task.FromResult(true),beforeRepair:_=>
            {
                if(manualStop)Set(recovery!,"desired",0);
                throw new IOException("Synthetic repair failure.");
            },persistentControl:true);
            Set(recovery,"binding",core);Set(recovery,"backend",owner);Set(recovery,"desired",1);
            Set(recovery,"spec",JsonSerializer.SerializeToElement(new{Session=session,Generation=1UL}));
            typeof(NativeCoreRecovery).GetProperty("State",Private)!.SetValue(recovery,"Active");
            var monitor=(Task)typeof(NativeCoreRecovery).GetMethod("Monitor",Private)!.Invoke(recovery,null)!;Set(recovery,"monitor",monitor);
            var timeout=Stopwatch.StartNew();while(recovery.State!=expected&&timeout.Elapsed<TimeSpan.FromSeconds(8))await Task.Delay(25);
            if(recovery.State!=expected)throw new IOException("Actual monitor loop did not report "+expected+"; saw "+recovery.State);
            if(!manualStop)await monitor.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            try
            {
                if(badRetirement&&core!=null)Set(core,"process",null);
                if(recovery!=null)await recovery.DisposeAsync();
            }
            finally
            {
                try
                {
                    if(!attached){ownerProcess.StandardInput.Close();try{await ownerProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}catch(TimeoutException){if(!ownerProcess.HasExited)ownerProcess.Kill(true);}}
                    await ownerJob.TerminateAsync(CancellationToken.None);
                    await ownerProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    if(ownerJob.ReadUsage().ActiveProcesses!=0)throw new IOException("Synthetic owner job retirement unconfirmed.");
                }
                finally{if(owner!=null)await owner.DisposeAsync();}
            }
        }
    }
}
