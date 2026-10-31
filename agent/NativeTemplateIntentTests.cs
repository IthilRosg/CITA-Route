using System.Text.Json;

namespace RoutePilot.Agent;
internal static class NativeTemplateIntentTests
{
    internal static void Run()
    {
        string scratch=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.GetEnvironmentVariable("CITA_TEST_SCRATCH_ROOT")??Path.GetTempPath()));
        string root=Path.Combine(scratch,"cita-template-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string path=Path.Combine(root,"intent.json");int boots=0;
            string Read(string file)=>File.ReadAllText(file);
            string Boot(){boots++;return "fixture-boot";}
            var first=NativeServiceRoute.LoadOrCreateTemplateIntent(path,true,Read,Boot);string saved=File.ReadAllText(path);
            if(first.Desired||first.Generation!=0||first.ConnectionId!=""||first.Run.Length!=32||first.Operation.Length!=32||boots!=1)throw new IOException("Template intent initial state differs.");
            var second=NativeServiceRoute.LoadOrCreateTemplateIntent(path,true,Read,Boot);
            if(first!=second||File.ReadAllText(path)!=saved||boots!=1)throw new IOException("Template reload changed its persisted intent.");
            string existing=JsonSerializer.Serialize(new{Run=new string('a',32),Generation=8,Desired=false,Operation=new string('b',32),Boot="existing"});
            File.WriteAllText(path,existing);
            var loaded=NativeServiceRoute.LoadOrCreateTemplateIntent(path,true,Read,Boot);
            if(loaded.Desired||loaded.Run!=new string('a',32)||loaded.Operation!=new string('b',32)||File.ReadAllText(path)!=existing||boots!=1)throw new IOException("Existing false intent was rewritten.");
            string absent=Path.Combine(root,"configured-missing.json");bool refused=false;
            try{NativeServiceRoute.LoadOrCreateTemplateIntent(absent,false,Read,Boot);}catch(InvalidDataException){refused=true;}
            if(!refused||File.Exists(absent))throw new IOException("Configured missing intent was silently created.");
            string race=Path.Combine(root,"race.json");using var gate=new Barrier(2);
            string RaceBoot(){if(!gate.SignalAndWait(TimeSpan.FromSeconds(5)))throw new TimeoutException("Intent race did not synchronize.");return "fixture-boot";}
            var a=Task.Run(()=>NativeServiceRoute.LoadOrCreateTemplateIntent(race,true,Read,RaceBoot));
            var b=Task.Run(()=>NativeServiceRoute.LoadOrCreateTemplateIntent(race,true,Read,RaceBoot));
            Task.WaitAll(a,b);
            if(a.Result!=b.Result||a.Result.Desired||Directory.EnumerateFiles(root,"race.json.*.new").Any())throw new IOException("Concurrent template writers diverged or left temporary files.");
        }
        finally
        {
            if(!string.Equals(Path.GetDirectoryName(Path.GetFullPath(root)),scratch,StringComparison.OrdinalIgnoreCase)||!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(root),@"\Acita-template-[a-f0-9]{32}\z"))throw new IOException("Template test cleanup target refused.");
            Directory.Delete(root,true);
        }
    }
}
