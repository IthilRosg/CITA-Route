using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using RoutePilot.Agent;

internal static class Program
{
    static string ValidateRoot(string root)
    {
        string full=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string parent=@"C:\Program Files\RoutePilot\Agent";
        if(!string.Equals(Path.GetDirectoryName(full),parent,StringComparison.OrdinalIgnoreCase)||
           !Regex.IsMatch(Path.GetFileName(full),@"\Aforced-store-lab-[a-f0-9]{32}\z"))throw new IOException("Wrong lab root.");
        return full;
    }
    static NativeForcedIntentRule Rule(string domain)=>new(domain,@"C:\Browser\app.exe","tcp",443,"direct");
    static Process StartWriter(NativeProcessJob job,string root,string domain)=>job.StartOwned(Environment.ProcessPath!,["--writer",root,domain],root);
    static void CrashAt(NativeProcessJob job,string root,string mode)
    {
        string marker=Path.Combine(root,"crash-ready-"+mode);
        using var child=job.StartOwned(Environment.ProcessPath!,["--crash-"+mode,root],root);
        try{var deadline=Stopwatch.StartNew();while(!File.Exists(marker)){if(child.HasExited||deadline.Elapsed>TimeSpan.FromSeconds(15))throw new IOException("Crash checkpoint unconfirmed.");Thread.Sleep(20);}child.Kill();if(!child.WaitForExit(5000))throw new IOException("Crash writer exit unconfirmed.");}
        finally{if(!child.HasExited){child.Kill();if(!child.WaitForExit(5000))throw new IOException("Crash writer cleanup unconfirmed.");}}
    }
    static int Main(string[] args)
    {
        try
        {
            if(args.Length is not (2 or 3))throw new ArgumentException("Wrong lab invocation.");
            string root=ValidateRoot(args[1]);
            if(args[0]=="--writer"&&args.Length==3)
            {
                string ready=args[2] switch{"one.example"=>"ready-one","two.example"=>"ready-two",_=>throw new ArgumentException("Unknown writer.")};
                File.WriteAllText(Path.Combine(root,ready),"ready");var wait=Stopwatch.StartNew();
                while(!File.Exists(Path.Combine(root,"release"))){if(wait.Elapsed>TimeSpan.FromSeconds(15))throw new IOException("Writer barrier deadline.");Thread.Sleep(20);}
                try{NativeForcedIntentStore.Protected(root).Replace(1,[Rule(args[2])]);return 0;}
                catch(NativeForcedIntentCommitUnknownException){return 6;}
                catch(NativeForcedIntentConflictException){return 4;}
                catch(IOException){return 5;}
            }
            if(args.Length==2&&args[0] is "--crash-before" or "--crash-after")
            {
                bool before=args[0]=="--crash-before";int targetReads=0;
                var fixture=NativeForcedIntentStore.ForTests(root,(item,ancestor)=>{
                    NativeTrustedBundle.CheckAcl(item,ancestor);
                    if(item is FileInfo file){bool checkpoint=before?file.Name.StartsWith("forced-intent-",StringComparison.Ordinal)&&file.Name.EndsWith(".new",StringComparison.Ordinal):file.Name=="forced-intent.json"&&++targetReads==2;
                        if(checkpoint){File.WriteAllText(Path.Combine(root,"crash-ready-"+(before?"before":"after")),"ready");Thread.Sleep(Timeout.Infinite);}}
                });
                fixture.Replace(2,[Rule("crash.example")]);throw new IOException("Crash checkpoint was not reached.");
            }
            if(args[0]!="--run"||args.Length!=2)throw new ArgumentException("Wrong lab mode.");
            var directory=new DirectoryInfo(root);if(!directory.Exists||(directory.Attributes&FileAttributes.ReparsePoint)!=0||directory.EnumerateFileSystemInfos().Any())throw new IOException("Fresh empty lab root required.");
            var store=NativeForcedIntentStore.Protected(root);store.InitializeNew();
            using var job=new NativeProcessJob();
            using var one=StartWriter(job,root,"one.example");Process? second=null;
            try{second=StartWriter(job,root,"two.example");}catch{if(!one.HasExited){one.Kill();if(!one.WaitForExit(5000))throw new IOException("First writer exit unconfirmed.");}throw;}
            using var two=second;
            try{
                var deadline=Stopwatch.StartNew();while(!File.Exists(Path.Combine(root,"ready-one"))||!File.Exists(Path.Combine(root,"ready-two"))){if(one.HasExited||two.HasExited||deadline.Elapsed>TimeSpan.FromSeconds(15))throw new IOException("Both writer readiness unconfirmed.");Thread.Sleep(20);}
                File.WriteAllText(Path.Combine(root,"release"),"release");
                if(!one.WaitForExit(15000)||!two.WaitForExit(15000))throw new IOException("Writer deadline.");
            }
            finally{var errors=new List<Exception>();foreach(var child in new[]{one,two})try{if(!child.HasExited){child.Kill();if(!child.WaitForExit(5000))throw new IOException("Owned writer exit unconfirmed.");}}catch(Exception error){errors.Add(error);}if(errors.Count>0)throw new AggregateException("Writer cleanup failed.",errors);}
            int[] statuses=[one.ExitCode,two.ExitCode];if(statuses.Count(x=>x==0)!=1||statuses.Count(x=>x==4)!=1)throw new IOException("Concurrent writer results unexpected.");
            var result=store.Read(2);if(result.Revision!=2||result.Rules.Length!=1)throw new IOException("Concurrent commit unconfirmed.");
            byte[] previous=NativeForcedIntent.Encode(result);CrashAt(job,root,"before");
            if(!NativeForcedIntent.Encode(store.Read(2)).AsSpan().SequenceEqual(previous))throw new IOException("Pre-rename crash changed committed intent.");
            var leftovers=Directory.GetFiles(root,"forced-intent-*.new");if(leftovers.Length!=1||!Regex.IsMatch(Path.GetFileName(leftovers[0]),@"\Aforced-intent-[a-f0-9]{32}\.new\z")||(File.GetAttributes(leftovers[0])&FileAttributes.ReparsePoint)!=0)throw new IOException("Unexpected pre-rename temporary file.");File.Delete(leftovers[0]);
            CrashAt(job,root,"after");var recovered=store.Read(3);byte[] expected=NativeForcedIntent.Encode(new NativeForcedIntentDocument(1,3,[Rule("crash.example")]));if(!NativeForcedIntent.Encode(recovered).AsSpan().SequenceEqual(expected))throw new IOException("Post-rename crash state not recovered.");
            string weak=Path.Combine(root,"weak");if(Directory.Exists(weak)||File.Exists(weak))throw new IOException("Unexpected weak ACL fixture.");Directory.CreateDirectory(weak);if((File.GetAttributes(weak)&FileAttributes.ReparsePoint)!=0)throw new IOException("Reparse fixture refused.");var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
            var admin=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);var users=new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null);
            acl.SetOwner(admin);foreach(var sid in new[]{admin,system})acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(users,FileSystemRights.Modify,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));new DirectoryInfo(weak).SetAccessControl(acl);
            bool refused=false;try{NativeForcedIntentStore.Protected(weak).InitializeNew();}catch(UnauthorizedAccessException){refused=true;}
            if(!refused)throw new IOException("Untrusted writable directory accepted.");
            Console.WriteLine(JsonSerializer.Serialize(new{success=true,barrierConfirmed=true,concurrentWinner=1,concurrentStaleRefused=1,secondStatus=statuses.Single(x=>x!=0),revision=recovered.Revision,crashBeforePreserved=true,crashAfterRecovered=true,weakAclRefused=true,scope="physical filesystem only; process kill, not power failure; no routing"}));return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e.GetType().Name+": "+e.Message);return 1;}
    }
}
