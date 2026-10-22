using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoutePilot.Agent;

public static class PolicyIntegration
{
    public static async Task Run(string root)
    {
        await ProbeWorker.Request("begin-operation","");
        string folder=Path.Combine(root,"runtime","policy-integration-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder,"runtime","rules"));Directory.CreateDirectory(Path.Combine(folder,"private"));Directory.CreateDirectory(Path.Combine(folder,"logs"));Directory.CreateDirectory(Path.Combine(folder,"data"));
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(75));
        Process? router=null;Task? controller=null;var listener=new TcpListener(IPAddress.Loopback,20808);
        try
        {
            if(Process.GetProcessesByName("smart-router").Any() || Process.GetProcessesByName("vpn-bridge").Any())throw new IOException("Stop the current engine first.");
            if(NetworkProbe.Vpnus==null)throw new IOException("This non-disruptive test requires the existing VPNUS connection.");
            var data=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"runtime","router-proxy.json")))!;
            data["log"]!["level"]="info";data["log"]!["output"]=Path.Combine(folder,"logs","router.log");
            data["experimental"]!.AsObject().Remove("cache_file");
            string token=Guid.NewGuid().ToString("N");data["experimental"]!["clash_api"]!["secret"]=token;
            File.WriteAllText(Path.Combine(folder,"private","controller-token.txt"),token);
            var routes=data["route"]!["rules"]!.AsArray();
            PolicyTcpBudget.Configure(data,new(){Mode="Auto"});
            foreach(var rule in routes.Where(r=>r?["rule_set"] is JsonArray sets && sets.Any(t=>t?.GetValue<string>() is "russia-inside" or "discord")).ToArray())routes.Remove(rule);
            foreach(var set in data["route"]!["rule_set"]!.AsArray())
            {
                string source=set!["path"]!.GetValue<string>(),name=Path.GetFileName(source),target=Path.Combine(folder,"runtime","rules",name);
                File.Copy(source,target);set["path"]=target;
            }
            foreach(string file in new[]{"russia_inside.json","discord.json"})File.Copy(Path.Combine(root,"runtime","rules",file),Path.Combine(folder,"runtime","rules",file));
            foreach(string file in new[]{"force-direct.txt","force-vpn.txt"})File.WriteAllText(Path.Combine(folder,file),"");
            File.Copy(Path.Combine(root,"runtime","bridge.json"),Path.Combine(folder,"runtime","bridge.json"));
            Storage.Write(Path.Combine(folder,"data","routing-policy.json"),new PolicyOptions{Mode="Auto",AllowListedDpi=true});
            string config=Path.Combine(folder,"runtime","router-proxy.json");Storage.Write(config,data);
            // A local SOCKS alias uses the already-working VPNUS only for this policy test.
            // AWG egress is tested independently by the protected system scenario.
            listener.Start();_ = Forward(listener,stop.Token);
            var start=new ProcessStartInfo(Path.Combine(root,"runtime","smart-router.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
            foreach(string arg in new[]{"run","-c",config,"--disable-color"})start.ArgumentList.Add(arg);
            router=Process.Start(start)??throw new IOException("Test router did not launch.");
            var errors=router.StandardError.ReadToEndAsync();
            Storage.Write(Path.Combine(folder,"runtime","session.json"),new PolicySession{Id=Guid.NewGuid().ToString("N"),Running=true,RouterPid=router.Id});
            await Task.Delay(1500,stop.Token);
            if(router.HasExited)throw new IOException(await errors);
            controller=PolicyWorker.Run(folder);
            string curl=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","curl.exe");
            for(int i=0;i<2;i++)await Tools.Run(curl,new[]{"--silent","--max-time","8","--proxy","http://127.0.0.1:20809","https://discord.com/","-o","NUL","--write-out","%{http_code}"},10,stop.Token);
            bool chosen=false;var watch=Stopwatch.StartNew();
            while(watch.Elapsed.TotalSeconds<35 && !stop.IsCancellationRequested)
            {
                if(controller.IsCompleted)await controller;
                string rule=Path.Combine(folder,"runtime","rules","auto-vpn.json");
                using var json=JsonDocument.Parse(File.ReadAllText(rule));
                chosen=json.RootElement.GetProperty("rules").EnumerateArray().Any(r=>r.GetProperty("domain")[0].GetString()=="discord.com" && r.GetProperty("process_path")[0].GetString()!.EndsWith("curl.exe",StringComparison.OrdinalIgnoreCase) && r.GetProperty("port")[0].GetInt32()==443);
                if(chosen)break;await Task.Delay(500,stop.Token);
            }
            if(!chosen)throw new IOException("The controller did not publish an application-scoped VPN decision. Inspect "+folder);
            await Task.Delay(1200,stop.Token);
            var final=await Tools.Run(curl,new[]{"--silent","--max-time","8","--proxy","http://127.0.0.1:20809","https://discord.com/","-o","NUL","--write-out","%{http_code}"},10,stop.Token);
            if(final.Code!=0 || final.Text.Trim()!="200")throw new IOException("The published rule did not produce HTTP 200: "+final.Text);
            Console.WriteLine("PASS: two failed real flows → bounded probes → per-application VPN rule → live rule reload → new HTTPS request HTTP 200. VPNUS was not disconnected.");
        }
        finally
        {
            if(router!=null){try{router.Kill(true);await router.WaitForExitAsync();}catch{}router.Dispose();}
            stop.Cancel();listener.Stop();
            if(controller!=null){try{await controller.WaitAsync(TimeSpan.FromSeconds(5));}catch{}}
            await ProbeWorker.Request("end-operation","");
            Console.WriteLine("Policy test artifacts: "+folder);
        }
    }
    static async Task Forward(TcpListener listener,CancellationToken stop)
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var client=await listener.AcceptTcpClientAsync(stop);
                _=Task.Run(async()=>
                {
                    using(client)using(var remote=new TcpClient())
                    {
                        try
                        {
                            await remote.ConnectAsync(IPAddress.Loopback,10808,stop);
                            using var lease=CancellationTokenSource.CreateLinkedTokenSource(stop);
                            Task a=client.GetStream().CopyToAsync(remote.GetStream(),lease.Token),b=remote.GetStream().CopyToAsync(client.GetStream(),lease.Token);
                            await Task.WhenAny(a,b);lease.Cancel();try{await Task.WhenAll(a,b);}catch{}
                        }catch{}
                    }
                },CancellationToken.None);
            }
        }catch(OperationCanceledException){}catch(ObjectDisposedException){}
    }
}
