using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RoutePilot.Agent;

// Disposable VM browser collector: passive baseline or existing ordinary Auto.
// The collector never owns the ordinary connection or changes its routing.
internal static class PublicResourceAudit
{
    static readonly string[] Pages={"https://example.com/","https://www.deepl.com/","https://www.wikipedia.org/","https://www.microsoft.com/","https://www.ryuugames.com/eng-the-blood-of-yidhra-uncensored/","https://srnky.com/OeXSnt","https://windows64.net/windows-11-x64-skachat-torrent/ne-oficialnye-sborki/","https://www.ryuugames.com/sister-amelias-no-kami-naki-sentaku-rj01545200/","https://windows64.net/","https://commons.wikimedia.org/wiki/Main_Page"};
    internal static bool Allowed(string url)=>Pages.Contains(url,StringComparer.Ordinal);
    internal static async Task Run(string directory,string? connectionRoot=null,bool focus=false)
    {
        string root=Path.GetFullPath(directory),browser=@"C:\CitaLab\Browser\chrome-win64\chrome.exe";
        using var bios=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        if((bios?.GetValue("SystemManufacturer") as string)?.Contains("VMware")!=true||
            !root.StartsWith(@"C:\CitaLab\PublicResources\",StringComparison.OrdinalIgnoreCase)||!Guid.TryParseExact(Path.GetFileName(root),"N",out _)||Directory.Exists(root))throw new IOException("Fresh owned VMware audit root required.");
        using(var file=File.OpenRead(browser))if(Convert.ToHexString(SHA256.HashData(file))!="5AF44A250A2139D4E0864C07227BF0687A6D12655BAB9682A5BE784B41BC82E1")throw new IOException("Pinned browser differs.");
        bool routed=connectionRoot!=null;
        PolicySession? connected=null;
        if(routed)
        {
            connectionRoot=Path.GetFullPath(connectionRoot!);
            string staged=Storage.Read<JsonElement>(@"C:\CitaLab\batch-stage.json").GetProperty("root").GetString()!;
            if(!connectionRoot.StartsWith(@"C:\CitaLab\BatchNext\",StringComparison.OrdinalIgnoreCase)||!connectionRoot.Equals(Path.GetFullPath(staged),StringComparison.OrdinalIgnoreCase))throw new IOException("Owned staged connection required.");
            connected=Storage.Read<PolicySession>(Path.Combine(connectionRoot,"runtime/session.json"));
            var options=Storage.Read<PolicyOptions>(Path.Combine(connectionRoot,"data/routing-policy.json"));
            if(!connected.Running||connected.BridgeGeneration<1||options.Mode!="Auto"||!options.ExperimentalBrowserFeedback)throw new IOException("Active Auto browser-feedback session required.");
            using var owner=Process.GetProcessById(connected.RouterPid);
            string image=owner.MainModule?.FileName??"";
            if(!string.Equals(Path.GetFileName(image),"smart-router.exe",StringComparison.OrdinalIgnoreCase)||!(image.StartsWith(connectionRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||image.Equals(@"C:\Program Files\RoutePilot\Agent\smart-router.exe",StringComparison.OrdinalIgnoreCase)))throw new IOException("Router identity differs.");
        }
        else if(Process.GetProcessesByName("RoutePilot").Length!=0||Process.GetProcessesByName("smart-router").Length!=0||Process.GetProcessesByName("vpn-bridge").Length!=0)throw new IOException("Clean guest required.");
        bool Current()
        {
            if(!routed)return true;
            var value=Storage.Read<PolicySession>(Path.Combine(connectionRoot!,"runtime/session.json"));
            return value.Running&&value.Id==connected!.Id&&value.RouterPid==connected.RouterPid&&value.BridgeGeneration==connected.BridgeGeneration&&!File.Exists(Path.Combine(connectionRoot!,"runtime/stop.request"));
        }
        object? PolicySnapshot()
        {
            if(!routed)return null;
            if(!Current())throw new IOException("Auto session generation changed.");
            var state=Storage.Read<JsonElement>(Path.Combine(connectionRoot!,"runtime/policy-state.json"));
            if(!state.GetProperty("epoch").GetString()!.StartsWith(connected!.Id+":",StringComparison.Ordinal))throw new IOException("Policy generation differs.");
            return new{updatedUtc=state.GetProperty("updatedUtc").Clone(),publication=state.GetProperty("publication").Clone(),decisions=state.GetProperty("decisions").GetArrayLength(),browserDependencies=state.GetProperty("browserDependencies").GetArrayLength(),browserDeliveries=state.GetProperty("browserDeliveries").GetArrayLength(),firstUse=state.TryGetProperty("firstUse",out var gate)?gate.Clone():(JsonElement?)null};
        }
        foreach(string part in new[]{"runtime","data","profiles"})Directory.CreateDirectory(Path.Combine(root,part));
        foreach(string name in new[]{"force-direct.txt","force-vpn.txt"})File.WriteAllText(Path.Combine(root,name),"");
        var session=new PolicySession{Id=Guid.NewGuid().ToString("N"),Running=true,RouterPid=Environment.ProcessId};Storage.Write(Path.Combine(root,"runtime/session.json"),session);
        var nic=NetworkInterface.GetAllNetworkInterfaces().First(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback);
        Storage.Write(Path.Combine(root,"runtime/bridge.json"),new{outbounds=new[]{new{bind_interface=nic.Name}}});
        var worker=new PolicyWorker(root,session,new(){Mode="Auto",AllowListedDpi=true,ExperimentalBrowserFeedback=true});
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(600));
        using var local=new HttpClient(new SocketsHttpHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(8)};
        var results=new List<object>();int measured=0;bool registered=false;BrowserNativeBroker? broker=null;
        try
        {
            if(!routed){BrowserNativeHost.Register(root,false);registered=true;broker=new(root,session.Id,worker);}
            // Repeat the originally failing document in the SAME session after
            // discovery, to distinguish first-use streams from later routing.
            foreach(string url in focus?Pages.Skip(6).Append(Pages[6]):Pages)
            {
                if(!Current())throw new IOException("Auto session generation changed.");
                string phase="open-browser-and-document";
                bool warmRepeat=focus&&url==Pages[6]&&results.Count>0;
                var clock=Stopwatch.StartNew();
                using var pageBudget=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                pageBudget.CancelAfter(TimeSpan.FromSeconds(50));
                try
                {
                    var policyBefore=PolicySnapshot();
                    await using var page=await BrowserTunLab.Browser.Open(browser,Path.Combine(root,"profiles",results.Count.ToString()),null,local,pageBudget.Token,nativeExtension:!warmRepeat,pump:broker==null?null:broker.Pump,mediumBrowser:routed,auditUrl:url);
                    // Scroll to trigger ordinary lazy images, without clicking links.
                    phase="scroll-and-observe";
                    await page.Evaluate("window.scrollTo(0,Math.min(document.body?.scrollHeight||0,1800)); true");
                    for(int i=0;i<50;i++){if(!Current())throw new IOException("Auto session generation changed.");broker?.Pump();await page.Evaluate(i%4==0?"window.scrollBy(0,1200);true":"true");await Task.Delay(200,pageBudget.Token);}
                    // Transport errors can arrive after the first observation;
                    // leave bounded time for the ordinary proof/retry cycle.
                    if(routed&&page.PendingAudit().Length>0&&clock.ElapsedMilliseconds<30000)
                        for(int i=0;i<40;i++){if(!Current())throw new IOException("Auto session generation changed.");await page.Evaluate("true");await Task.Delay(200,pageBudget.Token);}
                    phase="document-snapshot";
                    var timing=await page.Evaluate("""
                        (()=>{const n=performance.getEntriesByType('navigation')[0];const ms=(a,b)=>a>0&&b>=a?Math.round(b-a):null;const r=performance.getEntriesByType('resource');return {navigation:n?{dnsMs:ms(n.domainLookupStart,n.domainLookupEnd),connectMs:ms(n.connectStart,n.connectEnd),tlsMs:ms(n.secureConnectionStart,n.connectEnd),ttfbMs:ms(n.requestStart,n.responseStart),bodyMs:ms(n.responseStart,n.responseEnd),responseEndMs:Math.round(n.responseEnd),domInteractiveMs:Math.round(n.domInteractive),loadEventMs:Math.round(n.loadEventEnd)}:null,slowResources:r.toSorted((a,b)=>b.duration-a.duration).slice(0,24).map(x=>({domain:new URL(x.name,location.href).hostname,type:x.initiatorType,protocol:x.nextHopProtocol,durationMs:Math.round(x.duration),bytes:x.transferSize,crossOriginTiming:x.responseStart===0})),failedImages:Array.from(document.images).filter(i=>i.complete&&!i.naturalWidth).slice(0,24).map(i=>({domain:new URL(i.currentSrc||i.src,location.href).hostname,lazy:i.loading==='lazy',visible:i.getBoundingClientRect().width>0&&i.getBoundingClientRect().height>0}))}})()
                        """);
                    var observed=await page.Evaluate("""
                        (()=>{const host=u=>{try{return new URL(u,location.href).hostname}catch{return ''}};const images=Array.from(document.images).slice(0,200);const groups=Object.create(null);for(const img of images){const h=host(img.currentSrc||img.src);const g=groups[h]??={seen:0,loaded:0,broken:0,pending:0};g.seen++;if(!img.complete)g.pending++;else if(img.naturalWidth>0)g.loaded++;else g.broken++}const text=(document.title+' '+(document.body?.innerText||'').slice(0,4000)).toLowerCase();return {host:location.hostname,ready:document.readyState,challenge:/just a moment|verify you are human|checking your browser|attention required/.test(text),browserError:location.protocol==='chrome-error:',images:Object.entries(groups).map(([domain,counts])=>({domain,...counts})),frames:Array.from(document.querySelectorAll('iframe')).slice(0,32).map(f=>host(f.src)),resources:performance.getEntriesByType('resource').slice(0,300).map(r=>({domain:host(r.name),type:r.initiatorType,protocol:r.nextHopProtocol||'',bytes:r.transferSize||0,duration:Math.round(r.duration)}))}})()
                        """);
                    phase="image-decode";
                    var decoded=await page.Evaluate("""
                        (async()=>await Promise.all(Array.from(document.images).filter(i=>i.complete&&i.naturalWidth>0).slice(0,32).map(async img=>{let result;try{result=await Promise.race([img.decode().then(()=> 'decoded',()=> 'decode-error'),new Promise(r=>setTimeout(()=>r('decode-timeout'),2000))])}catch{result='decode-error'}return {domain:new URL(img.currentSrc||img.src,location.href).hostname,result}})))()
                        """);
                    broker?.Pump();var events=page.Events.Select(e=>JsonSerializer.SerializeToElement(e)).ToArray();
                    var statuses=events.Where(e=>e.TryGetProperty("status",out _)).Select(e=>new{status=e.GetProperty("status").GetDouble(),protocol=e.GetProperty("protocol").GetString()}).ToArray();
                    var errors=events.Where(e=>e.TryGetProperty("error",out _)).Select(e=>e.GetProperty("error").GetString()??"").Select(e=>Regex.IsMatch(e,@"^net::ERR_[A-Z0-9_]+$")?e:"Other").ToArray();
                    var resourceFailures=events.Where(e=>e.TryGetProperty("error",out _)&&e.TryGetProperty("domain",out var d)&&d.ValueKind==JsonValueKind.String).Select(e=>new{domain=e.GetProperty("domain").GetString(),error=Regex.IsMatch(e.GetProperty("error").GetString()??"",@"^net::ERR_[A-Z0-9_]+$")?e.GetProperty("error").GetString():"Other"}).ToArray();
                    phase="extension-snapshot";
                    var pendingNetwork=page.PendingAudit();
                    object? adapter=warmRepeat?null:await page.ExtensionState();
                    results.Add(new{caseId=Array.IndexOf(Pages,url),page=new Uri(url).Host,elapsedMs=clock.ElapsedMilliseconds,timing,observed,decoded,pendingNetwork,adapter,adapterSkipped=warmRepeat,statuses,errors,resourceFailures,policyBefore,policyAfter=PolicySnapshot(),dependencies=routed?null:worker.BrowserDependencySnapshot(DateTime.UtcNow)});measured++;Storage.Write(Path.Combine(root,"progress.json"),new{updatedUtc=DateTime.UtcNow,measured,attempts=results.Count,results});
                }
                catch(Exception ex)when(ex is IOException or OperationCanceledException){results.Add(new{page=new Uri(url).Host,phase,error=ex.GetType().Name,guard=Regex.IsMatch(ex.Message,@"^[A-Za-z .-]{1,120}$")?ex.Message:new[]{"Auto session generation changed.","Policy generation differs.","Pinned browser did not start.","Owned browser debugger closed.","Browser debugging startup deadline."}.Contains(ex.Message)?ex.Message:null,errorMethod=ex.StackTrace?.Split('\n')[0],errorCategory=ex.Message.StartsWith("Pinned browser startup: ",StringComparison.Ordinal)?"BrowserProcessExited":ex.Message.StartsWith("Owned browser command: ",StringComparison.Ordinal)?ex.Message:null,commandDeadline=ex.Message.StartsWith("Owned browser command deadline: ",StringComparison.Ordinal)?ex.Message:null,overallDeadline=stop.IsCancellationRequested});if(ex.StackTrace?.Contains("BrowserIntegrityLab.Start(",StringComparison.Ordinal)==true)break;}
            }
        }
        finally
        {
            if(broker!=null)await broker.DisposeAsync();
            if(registered)BrowserNativeHost.Unregister(root);
            Storage.Write(Path.Combine(root,"runtime/session.json"),new{running=false});
        }
        Storage.Write(Path.Combine(root,"result.json"),new{finishedUtc=DateTime.UtcNow,routed,completed=measured==(focus?Pages.Length-5:Pages.Length),attempts=results.Count,results,dependencyScope="Bounded cumulative controller snapshot; not per-page counts or per-flow routing proof.",scope=routed?"Existing ordinary Auto session. Collector does not change routes, registration or connection lifecycle; outer fixture owns cleanup. No downloads, authentication or TLS exceptions. Completion does not mean availability.":"Guest default network baseline with passive native document observation; no CITA routing acceptance."});
        Console.WriteLine("Audit completed: "+root);
    }
}
