using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace RoutePilot.Agent;

// VM-only public-certificate complement to the owned partial-body fixture.
// One public IPv4 /32, explicitly bound guest WAN; no WireSock/profile changes.
internal static class PublicBrowserTunLab
{
    const string BrowserPath=@"C:\CitaLab\Browser\chrome-win64\chrome.exe";
    sealed class Target
    {
        public string Domain{get;set;}="";
        public string Address{get;set;}="";
        public string BindInterface{get;set;}="";
    }
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    internal static async Task Run(string directory)
    {
        string root=Path.GetFullPath(directory),manifest=Path.Combine(root,"public-target.json");
        using var bios=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        Assert((bios?.GetValue("SystemManufacturer") as string)?.Contains("VMware")==true,"VMware guest required.");
        Assert(root.StartsWith(@"C:\CitaLab\PublicWebTun\",StringComparison.OrdinalIgnoreCase)&&Guid.TryParseExact(Path.GetFileName(root),"N",out _),"Owned public guest fixture path required.");
        Assert(Directory.EnumerateFileSystemEntries(root).All(p=>Path.GetFileName(p)=="public-target.json")&&File.Exists(manifest),"Fresh target manifest required.");
        Assert(new FileInfo(manifest).Length<=4096,"Oversized public target manifest.");
        var target=Storage.Read<Target>(manifest);
        Assert(target.Domain is "cloudflare-quic.com" or "www.cloudflare.com","Only fixed public test origins are permitted.");
        Assert(IPAddress.TryParse(target.Address,out var ip)&&ip.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(ip),"Public IPv4 target required; helper validates reserved ranges too.");
        Assert(target.BindInterface.Length is >0 and <=128&&target.BindInterface!="Cita-PublicWeb","Physical guest interface required.");
        using(var browserImage=File.OpenRead(BrowserPath))Assert(Convert.ToHexString(SHA256.HashData(browserImage))=="5AF44A250A2139D4E0864C07227BF0687A6D12655BAB9682A5BE784B41BC82E1","Pinned browser differs.");
        string manifestHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest)));
        bool Current()=>!File.Exists(Path.Combine(root,"stop.request"))&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest)))==manifestHash;
        // The public test belongs on the clean second VM, away from a live pilot.
        Assert(Process.GetProcessesByName("RoutePilot").Length==0&&Process.GetProcessesByName("smart-router").Length==0&&Process.GetProcessesByName("vpn-bridge").Length==0,"Use a clean guest without an installed active routing session.");
        foreach(string part in new[]{"runtime/rules","logs","profiles"})Directory.CreateDirectory(Path.Combine(root,part));
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(210));
        using var local=new HttpClient(new SocketsHttpHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(8)};
        var reserved=new HashSet<int>();int Port(){while(true){var l=new TcpListener(IPAddress.Loopback,0);l.Start();int n=((IPEndPoint)l.LocalEndpoint).Port;l.Stop();if(reserved.Add(n))return n;}}
        var ports=new PolicyPorts{Ack=Port(),Proof=Port()};
        var desired=PolicyPublisher.Routes.ToDictionary(r=>r,_=>Array.Empty<JsonObject>());
        foreach(var pair in desired)Storage.Write(Path.Combine(root,"runtime/rules/auto-"+pair.Key+".json"),new{version=3,rules=pair.Value});
        var rules=new JsonArray();
        foreach(string route in PolicyPublisher.Routes)rules.Add(JsonSerializer.SerializeToNode(new{type="logical",mode="and",rules=new object[]{new{inbound=new[]{"policy-ack"}},new{rule_set=new[]{"auto-"+route}}},action="route",outbound="proof",override_address="127.0.0.1",override_port=ports.Proof}));
        rules.Add(new JsonObject{["inbound"]=new JsonArray("policy-ack"),["action"]="reject"});
        rules.Add(new JsonObject{["action"]="sniff",["timeout"]="300ms"});
        rules.Add(new JsonObject{["process_name"]=new JsonArray("nonexistent-public-browser-lab.exe"),["action"]="reject"});
        int blockedIndex=rules.Count;
        rules.Add(JsonNode.Parse("""{"type":"logical","mode":"and","rules":[{"rule_set":["auto-blocked"]},{"ip_version":6,"invert":true}],"action":"reject"}"""));
        int quicIndex=rules.Count;
        rules.Add(new JsonObject{["protocol"]="quic",["action"]="route",["outbound"]="wan"});
        rules.Add(new JsonObject{["action"]="route",["outbound"]="wan"});
        var config=JsonSerializer.SerializeToNode(new{log=new{level="debug",timestamp=true,output=Path.Combine(root,"logs/router.log")},
            inbounds=new object[]{new{type="mixed",tag="policy-ack",listen="127.0.0.1",listen_port=ports.Ack},new{type="tun",tag="public-tun",interface_name="Cita-PublicWeb",address=new[]{"172.30.125.1/30","fd7a:125::1/126"},route_address=new[]{target.Address+"/32"},auto_route=true,strict_route=false,dns_mode="disabled",stack="mixed",mtu=1400}},
            outbounds=new object[]{new{type="direct",tag="wan",bind_interface=target.BindInterface},new{type="direct",tag="proof"}},
            dns=new{servers=new[]{new{type="hosts",tag="owned",predefined=new Dictionary<string,string[]>{{target.Domain,new[]{target.Address}}}}}},
            route=new{rules,rule_set=PolicyPublisher.Routes.Select(r=>new{type="local",format="source",tag="auto-"+r,path=Path.Combine(root,"runtime/rules/auto-"+r+".json")}),default_domain_resolver="owned",final="wan"}})!;
        PolicyWebRouting.Configure(config);Storage.Write(Path.Combine(root,"router.json"),config);
        void Progress(string phase)=>Storage.Write(Path.Combine(root,"progress.json"),new{phase,updatedUtc=DateTime.UtcNow});
        var start=new ProcessStartInfo(@"C:\CitaLab\Adaptive\browser\Cita.BrowserLab.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};start.ArgumentList.Add(root);
        using var helper=Process.Start(start)??throw new IOException("Scoped public helper did not start.");var helperError=helper.StandardError.ReadToEndAsync();var helperOutput=helper.StandardOutput.ReadToEndAsync();
        string? control=null;BrowserTunLab.Browser? browser=null;var observations=new List<object>();var browserEvents=new List<object>();bool success=false;string? failure=null;
        string routerLog=Path.Combine(root,"logs/router.log");
        async Task<string> ReadLog(long offset=0){using var stream=new FileStream(routerLog,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);Assert(stream.Length<=4*1024*1024,"Public fixture log budget exceeded.");stream.Position=offset;using var reader=new StreamReader(stream);return await reader.ReadToEndAsync(stop.Token);}
        async Task<BrowserTunLab.Browser> Open(string phase){Progress(phase);return await BrowserTunLab.Browser.Open(BrowserPath,Path.Combine(root,"profiles",phase),null,local,stop.Token,target.Domain,target.Address);}
        async Task<JsonElement> Fetch(BrowserTunLab.Browser page)=>await page.Evaluate("""
            (async()=>{const u='/cdn-cgi/trace';const started=performance.now();try{const r=await fetch(u,{cache:'no-store',signal:AbortSignal.timeout(7000)});const reader=r.body.getReader();let n=0,text='';const decoder=new TextDecoder();while(true){const part=await reader.read();if(part.done)break;n+=part.value.byteLength;if(n>8192){await reader.cancel();return {ok:false,error:'BodyLimit'}}text+=decoder.decode(part.value,{stream:true})}const p=performance.getEntriesByName(new URL(u,location.href).href).at(-1);return {ok:r.ok,status:r.status,bytes:n,protocol:p?.nextHopProtocol??'',serverProtocol:text.match(/^http=(.+)$/m)?.[1]??'',elapsedMs:Math.round(performance.now()-started)}}catch(e){return {ok:false,error:e.name,elapsedMs:Math.round(performance.now()-started)}}})()
            """);
        bool Complete(JsonElement r)=>r.TryGetProperty("ok",out var ok)&&ok.GetBoolean()&&r.GetProperty("status").GetInt32()==200&&r.GetProperty("bytes").GetInt32() is >0 and <=8192;
        async Task<JsonElement> Warm(BrowserTunLab.Browser page,bool quic){JsonElement result=default;for(int i=0;i<12;i++){result=await Fetch(page);if(Complete(result)&&(quic?result.GetProperty("protocol").GetString()=="h3"&&result.GetProperty("serverProtocol").GetString()=="http/3":result.GetProperty("protocol").GetString() is "h2" or "http/1.1"))return result;await Task.Delay(750,stop.Token);}observations.Add(new{phase="required-protocol-unavailable",quic,last=result});throw new IOException(quic?"HealthyPublicHttp3Unavailable":"VerifiedPublicTcpFallbackUnavailable");}
        try
        {
            Progress("StartingPublicTun");string ready=Path.Combine(root,"ready.json");
            for(int i=0;!File.Exists(ready);i++){if(helper.HasExited)throw new IOException("Public helper startup: "+await helperError);Assert(i<200,"Public helper startup deadline.");await Task.Delay(100,stop.Token);}
            control=Storage.Read<JsonElement>(ready).GetProperty("control").GetString()!;var controlUri=new Uri(control);Assert(controlUri.Scheme=="http"&&controlUri.Host=="127.0.0.1","Loopback control required.");
            await using var publisher=new PolicyPublisher(root,ports,Current);await publisher.Apply(desired,stop.Token);
            browser=await Open("PublicBaseline");var baseline=await Warm(browser,true);observations.Add(new{phase="baseline",response=baseline});
            desired["blocked"]=new[]{PolicyWebRouting.TcpFallback(new("public-web",target.Domain,BrowserPath,443))};Progress("PublishingPublicCompatibility");await publisher.Apply(desired,stop.Token);
            var pinned=await Fetch(browser);Assert(Complete(pinned),"Existing browser flow failed while publishing compatibility.");observations.Add(new{phase="existing-flow-after-publication",response=pinned});browserEvents.AddRange(browser.Events);await browser.DisposeAsync();browser=null;
            long offset=new FileInfo(routerLog).Length;var fallbackTimer=Stopwatch.StartNew();browser=await Open("PublicCompatibility");var fallback=await Warm(browser,false);await Task.Delay(2000,stop.Token);fallback=await Fetch(browser);Assert(Complete(fallback)&&fallback.GetProperty("protocol").GetString() is "h2" or "http/1.1","Complete public TCP body required.");
            string compatibility=await ReadLog(offset);
            int rejections=compatibility.Split('\n').Count(l=>l.Contains("match["+blockedIndex+"]")&&l.Contains("=> reject"));Assert(rejections>0,"No actual browser QUIC rejection observed.");Assert(!compatibility.Split('\n').Any(l=>l.Contains("match["+quicIndex+"]")&&l.Contains("=> route")),"Retransmitted QUIC bypassed the scoped rule.");
            observations.Add(new{phase="automatic-tcp-compatibility",response=fallback,ruleRejections=rejections,includingBrowserStartupMs=fallbackTimer.ElapsedMilliseconds});browserEvents.AddRange(browser.Events);await browser.DisposeAsync();browser=null;
            desired["blocked"]=Array.Empty<JsonObject>();await publisher.Apply(desired,stop.Token);
            browser=await Open("PublicRestored");var restored=await Warm(browser,true);observations.Add(new{phase="restored",response=restored});browserEvents.AddRange(browser.Events);await browser.DisposeAsync();browser=null;
            Assert(Current(),"Public fixture ownership changed.");success=true;Progress("Passed");
        }
        catch(Exception ex){if(browser!=null)browserEvents.AddRange(browser.Events);failure=ex.GetType().Name+": "+ex.Message;Progress("Failed");}
        finally
        {
            if(browser!=null)await browser.DisposeAsync();
            if(control!=null)try{await local.GetStringAsync(control+"/shutdown");}catch{}
            using var close=new CancellationTokenSource(12000);try{await helper.WaitForExitAsync(close.Token);}catch(OperationCanceledException){helper.Kill(true);await helper.WaitForExitAsync();}
            await Task.WhenAll(helperError,helperOutput);
        }
        Storage.Write(Path.Combine(root,"result.json"),new{success,failure,finishedUtc=DateTime.UtcNow,domain=target.Domain,automaticPublicCertificateFallbackVerified=success,strictBrowserTls=true,forcedQuic=false,observations,browserEvents,scope="Fixed public Cloudflare trace, IPv4/SNI, ordinary clean Chromium profiles with identical flags and strict certificates, one scoped TUN /32. Explicit rule publication; no VPN/DPI, browser observation channel, partial-body recovery or migration of a live HTTP/3 stream is claimed."});
        Console.WriteLine(JsonSerializer.Serialize(new{success,failure,root}));if(!success)throw new IOException(failure);
    }
}
