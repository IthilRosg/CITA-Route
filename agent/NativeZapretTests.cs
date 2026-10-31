using System.Security.Cryptography;

namespace RoutePilot.Agent;

internal static class NativeZapretTests
{
    internal static async Task Run()
    {
        static void Check(bool value,string error){if(!value)throw new Exception(error);}
        string epoch=Guid.NewGuid().ToString("N");
        var scope=new NativeZapretScope(epoch,@"C:\Apps\browser.exe",25,"192.168.1.124",53123,"8.8.8.8",443,"tcp");
        var excluded=new HashSet<string>{"1.1.1.1"};
        NativeZapretPlan Plan(NativeZapretScope candidate)=>NativeZapretPlan.ForTlsTcp(candidate,epoch,25,"192.168.1.124",excluded);
        var plan=Plan(scope);
        var reflected=new NativeWinDivertReflectionEvent(123,0,0,101,8,"canonical-filter");
        Check(NativeWinDivertReflection.Matches(reflected,123,100,"canonical-filter"),"A matching kernel reflection must be accepted.");
        foreach(var bad in new[]{reflected with{Pid=124},reflected with{Layer=1},reflected with{Flags=1},reflected with{Flags=2},
            reflected with{Flags=4},reflected with{Flags=8},reflected with{OpenedTimestamp=99},reflected with{Event=9},reflected with{Filter="broader-filter"}})
            Check(!NativeWinDivertReflection.Matches(bad,123,100,"canonical-filter"),"A stale, foreign or observation-only filter cannot prove readiness.");
        Check(plan.Filter.Contains("ifIdx == 25")&&plan.Filter.Contains("tcp.SrcPort == 53123")&&
            plan.Filter.Contains("tcp.DstPort == 53123")&&plan.Filter.Contains("ip.SrcAddr == 8.8.8.8")&&
            plan.Filter.Contains("!loopback and !impostor"),"Zapret filter must bind both directions to the exact physical socket.");
        Check(plan.Arguments(@"C:\CITA Route\zapret").Count(x=>x.StartsWith("--wf-"))==1&&
            plan.Arguments(@"C:\CITA Route\zapret")[0]=="--wf-raw="+plan.Filter,
            "Zapret must not broaden the filter with port-wide interception.");
        foreach(var bad in new[]{scope with{Epoch=Guid.NewGuid().ToString("N")},scope with{Application=""},
            scope with{PhysicalIndex=26},scope with{LocalAddress="192.168.1.125"},scope with{LocalPort=0},
            scope with{RemotePort=80},scope with{Network="udp"},scope with{RemoteAddress="1.1.1.1"},
            scope with{RemoteAddress="192.168.1.1"},scope with{RemoteAddress="26.68.227.253"},
            scope with{RemoteAddress="100.64.0.1"},scope with{RemoteAddress="127.0.0.1"},
            scope with{RemoteAddress="198.18.0.1"},scope with{RemoteAddress="::1"},scope with{RemoteAddress="8.8.8.8 or true"}})
        {
            bool refused=false;try{Plan(bad);}catch(InvalidDataException){refused=true;}
            Check(refused,"Zapret accepted stale, unidentified, broad or excluded scope.");
        }
        string root=Path.Combine(Path.GetTempPath(),"CITA-zapret-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var hashes=new Dictionary<string,string>();
            foreach(string name in NativeZapretBundle.RequiredFiles)
            {
                string path=Path.Combine(root,name.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                byte[] bytes=System.Text.Encoding.UTF8.GetBytes("identity-fixture-"+name);File.WriteAllBytes(path,bytes);hashes[name]=Convert.ToHexString(SHA256.HashData(bytes));
            }
            var bundle=new NativeZapretBundle(root,hashes);bundle.Verify();
            string original=hashes["winws2.exe"];hashes["winws2.exe"]=new string('0',64);
            bundle.Verify();hashes["winws2.exe"]=original;
            bool writableRefused=false;
            try{using var invalidTrust=NativeZapretTrust.Open(bundle);}catch(UnauthorizedAccessException){writableRefused=true;}
            Check(writableRefused,"An ordinary user-writable fixture must not become an executable Zapret trust root.");
            File.AppendAllText(Path.Combine(root,"lua","zapret-antidpi.lua"),"changed");
            bool changed=false;try{bundle.Verify();}catch(InvalidDataException){changed=true;}
            Check(changed,"Changed Zapret Lua must be rejected just like a changed executable.");
            File.WriteAllText(Path.Combine(root,"lua","zapret-antidpi.lua"),"identity-fixture-lua/zapret-antidpi.lua");
            bool noLease=false;try{NativeZapretWorker.Start(bundle,plan,()=>true,CancellationToken.None,CancellationToken.None);}
            catch(InvalidDataException){noLease=true;}
            Check(noLease,"A worker without cancellable socket ownership must not start.");
            using(var cancelled=new CancellationTokenSource())
            {
                cancelled.Cancel();bool stopped=false;
                try{NativeZapretWorker.Start(bundle,plan,()=>true,cancelled.Token,CancellationToken.None);}
                catch(OperationCanceledException){stopped=true;}
                Check(stopped,"Cancelled socket lease must be rejected before component loading.");
            }
            foreach(int cancelOn in new[]{1,2})
            {
                using var lease=new CancellationTokenSource();int checks=0;bool cancelled=false;
                bool Current(){if(++checks==cancelOn)lease.Cancel();return true;}
                try{using var unused=NativeZapretWorker.Start(bundle,plan,Current,lease.Token,CancellationToken.None);}
                catch(OperationCanceledException){cancelled=true;}
                Check(cancelled&&NativeZapretWorker.SlotsInUse==0,"Reentrant lease cancellation must prevent process creation and release its slot.");
            }
            // Real Windows job-creation regression: the child writes a fixture
            // and sleeps. Closing the job must kill it well before its 30s sleep.
            string evidence=Path.Combine(root,"argument evidence.txt"),payload="quoted \"value\" and trailing \\";
            string command="[IO.File]::WriteAllText('"+evidence.Replace("'","''")+"','"+payload+"');Start-Sleep -Seconds 30";
            using var job=new NativeProcessJob(128UL*1024*1024);
            using var child=job.StartOwned(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe"),
                ["-NoProfile","-NonInteractive","-Command",command],root);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while(!File.Exists(evidence)&&!child.HasExited)await Task.Delay(20,deadline.Token);
            Check(File.Exists(evidence)&&File.ReadAllText(evidence)==payload,"Owned process argument quoting did not round-trip.");
            Check(job.ReadUsage().ActiveProcesses>0,"Child did not enter its job at creation.");
            job.Dispose();
            await child.WaitForExitAsync(deadline.Token);
            Check(child.HasExited,"A controller-owned child survived job closure.");
        }
        finally
        {
            // Windows may release a killed child's working-directory handle
            // just after process exit. Keep cleanup bounded and preserve the
            // original assertion instead of masking it with that transient IO.
            for(int attempt=0;;attempt++)
            {
                try{Directory.Delete(root,true);break;}
                catch(IOException)when(attempt<40){await Task.Delay(50);}
            }
        }
        Console.WriteLine("PASS: exact Zapret TLS socket filters, epoch/interface/application exclusions, component tamper refusal, cancellable leases and atomic Windows job cleanup.");
    }
}
