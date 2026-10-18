using System.Diagnostics;
using System.Text.Json;

namespace RoutePilot.Agent;

internal static class PolicyAutoCheck
{
    internal static async Task Run(string root,string domain)
    {
        if(PolicySafety.Domain(domain)!=domain)throw new ArgumentException("Expected a public domain name.");
        var session=Storage.Read<PolicySession>(Path.Combine(root,"runtime/session.json"));
        if(!session.Running || !PolicyWorker.Eligible(root,session,domain))throw new IOException("The domain is pinned/manual or the ordinary session is inactive.");
        // A private copy gives this run a NEW application-scoped key, without
        // clearing the user's learned routes or changing any routing mode.
        string systemCurl=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","curl.exe");
        string curl=Path.Combine(root,"runtime","diagnostic-curl-"+Guid.NewGuid().ToString("N")+".exe");
        File.Copy(systemCurl,curl);
        var initial=Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json"));
        bool cached=initial.GetProperty("decisions").EnumerateArray().Any(d=>d.GetProperty("Key").GetProperty("Domain").GetString()==domain && string.Equals(d.GetProperty("Key").GetProperty("Application").GetString(),curl,StringComparison.OrdinalIgnoreCase) && d.GetProperty("ExpiresUtc").GetDateTime()>DateTime.UtcNow);
        var timer=Stopwatch.StartNew();
        var start=new ProcessStartInfo(curl){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{"--head","--silent","--show-error","--max-time","14","--proxy","http://127.0.0.1:20809","https://"+domain+"/","--output","NUL","--write-out","%{http_code}"})start.ArgumentList.Add(arg);
        using var first=Process.Start(start)??throw new IOException("Diagnostic curl did not start.");
        var firstOutput=first.StandardOutput.ReadToEndAsync();var firstError=first.StandardError.ReadToEndAsync();
        string? route=null;long appliedMs=0;JsonElement? matching=null;
        try
        {
            while(timer.Elapsed.TotalSeconds<30)
            {
                var current=Storage.Read<PolicySession>(Path.Combine(root,"runtime/session.json"));
                if(current.Id!=session.Id || !current.Running || File.Exists(Path.Combine(root,"runtime/stop.request")))throw new IOException("Session changed or Stop requested during the diagnostic.");
                var policy=Storage.Read<JsonElement>(Path.Combine(root,"runtime/policy-state.json"));
                if(policy.GetProperty("phase").GetString()=="Auto" && policy.GetProperty("epoch").GetString()!.StartsWith(session.Id+":"))
                {
                    foreach(var decision in policy.GetProperty("decisions").EnumerateArray())
                    {
                        var key=decision.GetProperty("Key");
                        if(key.GetProperty("Domain").GetString()==domain && string.Equals(key.GetProperty("Application").GetString(),curl,StringComparison.OrdinalIgnoreCase) && decision.GetProperty("ExpiresUtc").GetDateTime()>DateTime.UtcNow)
                        {route=decision.GetProperty("Route").GetString();appliedMs=timer.ElapsedMilliseconds;matching=decision.Clone();break;}
                    }
                }
                if(route!=null)break;
                await Task.Delay(100);
            }
            if(route==null)throw new IOException("No acknowledged per-application decision appeared within 30 seconds.");
            var probe=await Tools.Run(curl,new[]{"--head","--silent","--show-error","--max-time","10","--proxy","http://127.0.0.1:20809","https://"+domain+"/","--output","NUL","--write-out","%{http_code}"},12);
            if(probe.Code!=0 || !int.TryParse(probe.Text.Trim(),out int status) || status<100 || status>599)throw new IOException("The NEW diagnostic HTTPS connection did not succeed after route application.");
            var report=new{success=true,sessionId=session.Id,domain,cold=!cached,route,appliedMilliseconds=appliedMs,newHttpsMilliseconds=timer.ElapsedMilliseconds,httpStatus=probe.Text.Trim(),decision=matching,scope="single credential-free HEAD triggers ordinary learning; a separate new HEAD confirms transport; original request not replayed",finishedUtc=DateTime.UtcNow};
            Storage.Write(Path.Combine(root,"logs/optimization-auto-last.json"),report);
            Console.WriteLine(JsonSerializer.Serialize(report,Storage.Json));
        }
        finally
        {
            if(!first.HasExited){first.Kill(true);await first.WaitForExitAsync();}
            await firstOutput;await firstError;
            try{File.Delete(curl);}catch(IOException){}
        }
    }
}
