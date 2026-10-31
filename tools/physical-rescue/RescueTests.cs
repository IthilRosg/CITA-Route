using System.Diagnostics;
using RoutePilot.Agent;

internal static class RescueTests
{
    internal static async Task Run()
    {
        string testBase=Path.Combine(Environment.CurrentDirectory,"runtime");if(!Directory.Exists(testBase))testBase=Path.GetTempPath();
        string leaseRoot=Path.Combine(testBase,"cita-intent-lease-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(leaseRoot);
        string intent=Path.Combine(leaseRoot,"intent.json"),replacement=Path.Combine(leaseRoot,"replace.json");
        File.WriteAllText(intent,"{\"Run\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"Operation\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"Generation\":1,\"Desired\":false}");
        File.WriteAllText(replacement,"{}");
        try
        {
            var first=MaintenanceLease.Open(intent);var second=MaintenanceLease.Open(intent);
            try
            {
                void Refused()
                {
                    bool write=false,delete=false,replace=false;
                    try{File.WriteAllText(intent,"bad");}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){write=true;}
                    try{File.Delete(intent);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){delete=true;}
                    try{File.Move(replacement,intent,true);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){replace=true;}
                    if(MaintenanceLease.TestDeleteOpen(intent) is not (32 or 33))throw new IOException("Held intent allowed native DELETE open.");
                    if(!write||!delete||!replace)throw new IOException("Held intent allowed mutation.");
                }
                Refused();first.Dispose();Refused();second.Dispose();
                if(MaintenanceLease.TestDeleteOpen(intent)!=0)throw new IOException("Intent DELETE open did not resume.");
                File.Move(replacement,intent,true);if(File.ReadAllText(intent)!="{}")throw new IOException("Intent replacement did not resume.");
                Console.WriteLine("PASS: two maintenance leases reject write/delete/replace until both are closed.");
            }
            finally{first.Dispose();second.Dispose();}
        }
        finally{File.Delete(intent);File.Delete(replacement);Directory.Delete(leaseRoot);}
        foreach(int cleanupMode in new[]{0,1,2})
        {
            var info=new ProcessStartInfo(Environment.ProcessPath!,"--test-blocked-child") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            Process? held=null;var original=new IOException("Synthetic attach refusal.");
            try
            {
                try
                {
                    using var unexpected=GuardChild.StartWith(info,()=>{},p=>{held=Process.GetProcessById(p.Id);_=held.Handle;throw original;},cleanupMode==0?GuardChild.RetireBlockedChild:cleanupMode==1?p=>false:p=>throw new InvalidOperationException("Synthetic retirement failure."));
                    throw new IOException("Failed attachment unexpectedly succeeded.");
                }
                catch(GuardChildStartException failure)
                {
                    if(!ReferenceEquals(failure.InnerException,original)||failure.CleanupConfirmed!=(cleanupMode==0)||(cleanupMode==2&&failure.CleanupFailure is not InvalidOperationException))throw new IOException("Assignment failure or cleanup status lost.");
                    if(cleanupMode==0&&(held==null||!held.HasExited))throw new IOException("Failed-assignment child remains alive.");
                }
                Console.WriteLine(cleanupMode!=0?"PASS: failed guard retirement reports unknown with original attach error.":"PASS: failed guard attachment kills and waits for the held child.");
            }
            finally{if(held!=null){if(!held.HasExited){held.Kill();await held.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}held.Dispose();}}
        }
        using(var testJob=new NativeProcessJob())
        {
            var info=new ProcessStartInfo(Environment.ProcessPath!,"--test-request-child") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            using var child=GuardChild.Start(info,testJob,()=>{});
            if(testJob.ReadUsage().ActiveProcesses!=1)throw new IOException("Guard child not attached before request.");
            bool refused=false;try{using var unused=await GuardChild.Request(child,new{test=true},false,()=>throw new IOException("Watcher exited."));}catch(IOException){refused=true;}
            if(!refused)throw new IOException("Lost watcher did not reject request.");
            child.StandardInput.Close();await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if(await child.StandardOutput.ReadToEndAsync()!="")throw new IOException("Request reached child after admission failure.");
            using var second=GuardChild.Start(info,testJob,()=>{});
            using var response=await GuardChild.Request(second,new{test=true},false,()=>{});
            if(!response.RootElement.GetProperty("success").GetBoolean())throw new IOException("Attached helper response absent.");
            await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Console.WriteLine("PASS: production guard request gate refuses lost watcher before private-input write and admits attached helper.");
        }
        using(var controller=Process.GetCurrentProcess())
        using(var job=new NativeProcessJob())
        using(var duplicate=NativeProcessJob.DuplicateFromHeldController(controller,job.DescriptorHandleValue))
        {
            string image=Environment.ProcessPath??throw new IOException("No test image.");
            using var child=Process.Start(new ProcessStartInfo(image,"--test-blocked-child") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true})??throw new IOException("Child failed.");
            try
            {
                job.AssignBlockedOnPrivateInput(child);
                if(duplicate.ReadUsage().ActiveProcesses!=1)throw new IOException("Duplicated job does not own child.");
                await duplicate.TerminateAsync(CancellationToken.None);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                if(job.ReadUsage().ActiveProcesses!=0)throw new IOException("Job child did not retire.");
            }
            finally{if(!child.HasExited){child.Kill();await child.WaitForExitAsync();}}
            Console.WriteLine("PASS: held controller job duplicate terminates its blocked child and confirms zero processes.");
        }
        using(var controller=Process.GetCurrentProcess())
        {
            var ownerJob=new NativeProcessJob();
            var primary=NativeProcessJob.DuplicateFromHeldController(controller,ownerJob.DescriptorHandleValue);
            using var backup=NativeProcessJob.DuplicateFromHeldController(controller,ownerJob.DescriptorHandleValue);
            using var child=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"--test-blocked-child") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true})??throw new IOException("Child failed.");
            try
            {
                ownerJob.AssignBlockedOnPrivateInput(child);
                primary.Dispose();ownerJob.Dispose();
                if(backup.ReadUsage().ActiveProcesses!=1)throw new IOException("Backup lost job after primary and owner closed.");
                await backup.TerminateAsync(CancellationToken.None);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                if(backup.ReadUsage().ActiveProcesses!=0)throw new IOException("Backup job retirement unconfirmed.");
                Console.WriteLine("PASS: backup duplicate retires child after primary and controller job handles close.");
            }
            finally{primary.Dispose();ownerJob.Dispose();if(!child.HasExited){child.Kill();await child.WaitForExitAsync();}}
        }
        foreach(string phase in new[]{"before","after"})
        {
            using var owner=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"--test-owner "+phase) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true})??throw new IOException("Test owner failed.");
            Process? child=null;
            try
            {
                string? line=await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if(!int.TryParse(line,out int pid))throw new IOException("Test child identity missing.");
                child=Process.GetProcessById(pid);_=child.Handle;
                owner.Kill();await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Console.WriteLine("PASS: child exits on controller death "+phase+" job attachment.");
            }
            finally
            {
                if(!owner.HasExited){owner.Kill();await owner.WaitForExitAsync();}
                if(child!=null){if(!child.HasExited){child.Kill();await child.WaitForExitAsync();}child.Dispose();}
            }
        }
        string root=Path.Combine(Path.GetTempPath(),"cita-rescue-lock-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);CleanupLease? acquired=null;Task<CleanupLease?>? waiting=null;
        try
        {
            using var owner=CleanupLease.Acquire(root)??throw new IOException("Initial lease missing.");
            waiting=Task.Run(()=>CleanupLease.Acquire(root));
            // Covers the previous five-second early-success failure window.
            await Task.Delay(5250);
            if(waiting.IsCompleted)throw new IOException("Backup gave up while the primary still held cleanup ownership.");
            owner.Dispose();acquired=await waiting.WaitAsync(TimeSpan.FromSeconds(3));
            if(acquired==null)throw new IOException("Backup did not acquire released cleanup ownership.");
            Console.WriteLine("PASS: backup waits past former timeout and takes cleanup ownership after primary release.");
        }
        finally
        {
            acquired?.Dispose();
            // A failed test never deletes a live owner's directory.
            if(waiting is {IsCompletedSuccessfully:true})waiting.Result?.Dispose();
            if(waiting==null||waiting.IsCompleted){File.Delete(Path.Combine(root,"cleanup.lock"));Directory.Delete(root);}
        }
    }
}
