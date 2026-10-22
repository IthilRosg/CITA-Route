using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace RoutePilot.Agent;

// Deliberate VM-only acceptance command. One TEST-NET /32; no default routes,
// DNS changes, real browser profile, installed executable or VPN reconfiguration.
internal static class BrowserTunLab
{
    const string Domain="web.cita-route.test",Address="203.0.113.231";
    const string BrowserPath=@"C:\CitaLab\Browser\chrome-win64\chrome.exe";
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    internal static async Task Run(string directory,bool feedback=false)
    {
        string root=Path.GetFullPath(directory);
        using var bios=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        Assert((bios?.GetValue("SystemManufacturer") as string)?.Contains("VMware")==true,"VMware guest required.");
        Assert(root.StartsWith(@"C:\CitaLab\BrowserTun\",StringComparison.OrdinalIgnoreCase)&&Guid.TryParseExact(Path.GetFileName(root),"N",out _),"Only a fresh owned guest lab directory is allowed.");
        Assert(!Directory.Exists(root),"Browser test directory already exists.");
        using(var image=File.OpenRead(BrowserPath))Assert(Convert.ToHexString(SHA256.HashData(image))=="5AF44A250A2139D4E0864C07227BF0687A6D12655BAB9682A5BE784B41BC82E1","Pinned official browser binary differs.");
        string installed=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"CITA Test/Client/client");
        if(feedback)Assert(Process.GetProcessesByName("RoutePilot").Length==0&&Process.GetProcessesByName("smart-router").Length==0&&Process.GetProcessesByName("vpn-bridge").Length==0,"Use the clean second guest for feedback acceptance.");
        var session=feedback?new PolicySession{Id=Guid.NewGuid().ToString("N"),Running=true,RouterPid=Environment.ProcessId}:Storage.Read<PolicySession>(Path.Combine(installed,"runtime/session.json"));Assert(session.Running&&(feedback||session.BridgeGeneration>0),"An existing ordinary pilot is required.");
        bool Current(){if(feedback)return !File.Exists(Path.Combine(root,"stop.request"));var after=Storage.Read<PolicySession>(Path.Combine(installed,"runtime/session.json"));return after.Running&&after.Id==session.Id&&after.BridgeGeneration==session.BridgeGeneration;}
        foreach(string part in new[]{"runtime/rules","logs","profiles","private","data"})Directory.CreateDirectory(Path.Combine(root,part));
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(210));
        using var local=new HttpClient(new SocketsHttpHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(8)};
        var portNumbers=new HashSet<int>();int Port(){while(true){var l=new TcpListener(IPAddress.Loopback,0);l.Start();int n=((IPEndPoint)l.LocalEndpoint).Port;l.Stop();if(portNumbers.Add(n))return n;}}
        var ports=new PolicyPorts{Ack=Port(),Proof=Port()};
        var desired=PolicyPublisher.Routes.ToDictionary(r=>r,_=>Array.Empty<JsonObject>());
        foreach(var pair in desired)Storage.Write(Path.Combine(root,"runtime/rules/auto-"+pair.Key+".json"),new{version=3,rules=pair.Value});
        string manual=Path.Combine(root,"runtime/rules/personal-direct.json");Storage.Write(manual,new{version=3,rules=Array.Empty<JsonObject>()});
        var rules=new JsonArray();
        foreach(string route in PolicyPublisher.Routes)rules.Add(JsonSerializer.SerializeToNode(new{type="logical",mode="and",rules=new object[]{new{inbound=new[]{"policy-ack"}},new{rule_set=new[]{"auto-"+route}}},action="route",outbound="fixture",override_address="127.0.0.1",override_port=ports.Proof}));
        rules.Add(JsonSerializer.SerializeToNode(new{type="logical",mode="and",rules=new object[]{new{inbound=new[]{"policy-ack"}},new{rule_set=new[]{"personal-direct"}}},action="route",outbound="fixture",override_address="127.0.0.1",override_port=ports.Proof}));
        rules.Add(new JsonObject{["inbound"]=new JsonArray("policy-ack"),["action"]="reject"});
        rules.Add(new JsonObject{["action"]="sniff",["timeout"]="300ms"});
        rules.Add(new JsonObject{["process_name"]=new JsonArray("nonexistent-cita-browser-fixture.exe"),["action"]="reject"});
        rules.Add(new JsonObject{["rule_set"]=new JsonArray("personal-direct"),["action"]="route",["outbound"]="fixture",["override_address"]="127.0.0.1",["override_port"]=443});
        if(feedback)foreach(string route in new[]{"direct","dpi","vpn"})rules.Add(new JsonObject{["rule_set"]=new JsonArray("auto-"+route),["action"]="route",["outbound"]="fixture",["override_address"]="127.0.0.1",["override_port"]=route=="direct"?443:4443});
        int blockedIndex=rules.Count;
        rules.Add(JsonNode.Parse("""{"type":"logical","mode":"and","rules":[{"rule_set":["auto-blocked"]},{"ip_version":6,"invert":true}],"action":"reject"}"""));
        int quicIndex=rules.Count;
        rules.Add(new JsonObject{["protocol"]="quic",["action"]="route",["outbound"]="fixture",["override_address"]="127.0.0.1",["override_port"]=443});
        rules.Add(new JsonObject{["network"]="udp",["action"]="route",["outbound"]="fixture",["override_address"]="127.0.0.1",["override_port"]=4444});
        rules.Add(new JsonObject{["network"]="tcp",["action"]="route",["outbound"]="fixture",["override_address"]="127.0.0.1",["override_port"]=443});
        var sets=PolicyPublisher.Routes.Select(r=>(object)new{type="local",format="source",tag="auto-"+r,path=Path.Combine(root,"runtime/rules/auto-"+r+".json")}).Append(new{type="local",format="source",tag="personal-direct",path=manual}).ToArray();
        var config=JsonSerializer.SerializeToNode(new{log=new{level="debug",timestamp=true,output=Path.Combine(root,"logs/router.log")},
            inbounds=new object[]{new{type="mixed",tag="policy-ack",listen="127.0.0.1",listen_port=ports.Ack},new{type="tun",tag="web-tun",interface_name="Cita-WebLab",address=new[]{"172.30.124.1/30","fd7a:124::1/126"},route_address=new[]{Address+"/32"},auto_route=true,strict_route=false,dns_mode="disabled",stack="mixed",mtu=1400}},
            outbounds=new[]{new{type="direct",tag="fixture"}},dns=new{servers=new[]{new{type="hosts",tag="owned",predefined=new Dictionary<string,string[]>{{Domain,new[]{"127.0.0.1"}}}}}},
            route=new{rules,rule_set=sets,default_domain_resolver="owned",auto_detect_interface=true}})!;
        PolicyWebRouting.Configure(config);Storage.Write(Path.Combine(root,"router.json"),config);
        if(feedback){Storage.Write(Path.Combine(root,"runtime/router.json"),config);Storage.Write(Path.Combine(root,"runtime/session.json"),session);Storage.Write(Path.Combine(root,"runtime/policy-ports.json"),ports);Storage.Write(Path.Combine(root,"runtime/bridge.json"),new{outbounds=new[]{new{bind_interface=NetworkInterface.GetAllNetworkInterfaces().First(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback).Name}}});File.WriteAllText(Path.Combine(root,"force-direct.txt"),"");File.WriteAllText(Path.Combine(root,"force-vpn.txt"),"");}
        void Progress(string phase)=>Storage.Write(Path.Combine(root,"progress.json"),new{phase,updatedUtc=DateTime.UtcNow});
        var info=new ProcessStartInfo(@"C:\CitaLab\Adaptive\browser\Cita.BrowserLab.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add(root);
        using var helper=Process.Start(info)??throw new IOException("Owned helper did not start.");var helperOut=helper.StandardOutput.ReadToEndAsync();var helperErr=helper.StandardError.ReadToEndAsync();
        string? control=null;Browser? browser=null;var observations=new List<object>();var browserEvents=new List<object>();bool success=false;string? failure=null;
        try
        {
            Progress("StartingScopedTun");string readyPath=Path.Combine(root,"ready.json");
            for(int i=0;!File.Exists(readyPath);i++){if(helper.HasExited)throw new IOException("Scoped helper startup: "+await helperErr);Assert(i<200,"Scoped helper startup deadline.");await Task.Delay(100,stop.Token);}
            var ready=Storage.Read<JsonElement>(readyPath);control=ready.GetProperty("control").GetString()!;var uri=new Uri(control);Assert(uri.Host=="127.0.0.1"&&uri.Scheme=="http","Control must stay loopback.");
            string spki=ready.GetProperty("spki").GetString()!;
            await using var publisher=new PolicyPublisher(root,ports,Current);await publisher.Apply(desired,stop.Token);
            PolicyWorker? worker=null;int ownTcpProbes=0;
            if(feedback)
            {
                // Same two-probe selector as production; only the lab transport
                // is substituted to pin its generated, owned certificate.
                async Task<CalibrationResult> Calibrate(PolicyKey target,CancellationToken token)
                {
                    var selected=await PolicyTransport.Select(async(port,cancel)=>{
                        using var client=new HttpClient(new SocketsHttpHandler{UseProxy=false,ConnectCallback=async(_,ct)=>{var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);try{await socket.ConnectAsync(IPAddress.Loopback,port==ports.Direct?443:4443,ct);return new NetworkStream(socket,true);}catch{socket.Dispose();throw;}},SslOptions=new SslClientAuthenticationOptions{RemoteCertificateValidationCallback=(_,cert,_,errors)=>cert!=null&&(errors&~SslPolicyErrors.RemoteCertificateChainErrors)==SslPolicyErrors.None&&Convert.ToBase64String(SHA256.HashData(new X509Certificate2(cert).PublicKey.ExportSubjectPublicKeyInfo()))==spki}});
                        using var request=new HttpRequestMessage(HttpMethod.Head,"https://"+Domain+"/"){Version=HttpVersion.Version11,VersionPolicy=HttpVersionPolicy.RequestVersionExact};
                        using var response=await client.SendAsync(request,cancel);Interlocked.Increment(ref ownTcpProbes);return PolicyTransport.HttpResponse((int)response.StatusCode,1);
                    },ports,token,avoidRoutes:worker!.AvoidedRoutes(target,DateTime.UtcNow));
                    var at=DateTime.UtcNow;var d=selected.Route==null?null:new PolicyDecision(target,selected.Route,"Own fixture TLS probes",at,at.AddMinutes(5),selected.Milliseconds);
                    return new(selected.Outcome,d,0,selected.Milliseconds,selected.Candidates);
                }
                worker=new PolicyWorker(root,session,new(){Mode="Auto",AllowListedDpi=true,ExperimentalBrowserFeedback=true},Calibrate);
                await worker.AttachBrowserPublisher(publisher,stop.Token);
            }
            var key=new PolicyKey("owned-web-test",Domain,BrowserPath,443);
            async Task<Browser> Open(string phase)
            {
                Progress(phase);Assert(Current(),"Pilot changed; test abandoned.");
                return await Browser.Open(BrowserPath,Path.Combine(root,"profiles",phase),spki,local,stop.Token,feedback:worker);
            }
            async Task<JsonElement> Resource(Browser page,string kind)
            {
                string path=kind=="image"?"/image.png":"/file.bin";
                string js="(async()=>{const u='"+path+"?case="+Guid.NewGuid().ToString("N")+"';try{const r=await fetch(u,{cache:'no-store',signal:AbortSignal.timeout(6000)});const b=await r.arrayBuffer();const hash=Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',b))).map(x=>x.toString(16).padStart(2,'0')).join('');let width=0;if('"+kind+"'==='image'){const image=new Image();image.src=URL.createObjectURL(new Blob([b],{type:'image/png'}));await image.decode();width=image.naturalWidth;URL.revokeObjectURL(image.src)}const p=performance.getEntriesByName(new URL(u,location.href).href).at(-1);return {ok:r.ok,bytes:b.byteLength,hash,width,protocol:p?.nextHopProtocol??''}}catch(e){return {ok:false,error:e.name}}})()";
                return await page.Evaluate(js);
            }
            bool Complete(JsonElement value,string kind)=>value.TryGetProperty("ok",out var ok)&&ok.GetBoolean()&&value.GetProperty("bytes").GetInt32()==ready.GetProperty(kind+"Bytes").GetInt32()&&value.GetProperty("hash").GetString()==ready.GetProperty(kind+"Sha256").GetString()&&(kind!="image"||value.GetProperty("width").GetInt32()==256);
            async Task<JsonElement[]> FetchPair(Browser page){return new[]{await Resource(page,"image"),await Resource(page,"file")};}
            async Task<JsonElement[]> Warm(Browser page,bool requireQuic)
            {
                JsonElement[] pair=Array.Empty<JsonElement>();
                for(int i=0;i<20;i++){pair=await FetchPair(page);if(pair.All(p=>p.GetProperty("ok").GetBoolean())&&(!requireQuic||pair.Any(p=>p.GetProperty("protocol").GetString()=="h3")))return pair;await Task.Delay(750,stop.Token);}
                observations.Add(new{phase="baseline-not-reached",resources=pair});
                throw new IOException("Browser did not reach the required healthy HTTP/3 baseline.");
            }
            browser=await Open("BaselineHttp3");var baseline=await Warm(browser,true);Assert(Complete(baseline[0],"image")&&Complete(baseline[1],"file"),"Baseline resource integrity.");observations.Add(new{phase="baseline",resources=baseline});
            // Retain one independent real TCP/TLS connection across publication.
            using var tcp=new TcpClient();await tcp.ConnectAsync(IPAddress.Parse(Address),443,stop.Token);
            await using var tls=new SslStream(tcp.GetStream(),false,(_,certificate,_,errors)=>certificate!=null&&(errors&~SslPolicyErrors.RemoteCertificateChainErrors)==SslPolicyErrors.None&&Convert.ToBase64String(SHA256.HashData(new X509Certificate2(certificate).PublicKey.ExportSubjectPublicKeyInfo()))==spki);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost=Domain},stop.Token);using var reader=new StreamReader(tls,Encoding.ASCII,false,1024,true);
            async Task Ping()
            {
                await tls.WriteAsync(Encoding.ASCII.GetBytes("GET /ping HTTP/1.1\r\nHost: "+Domain+"\r\nConnection: keep-alive\r\n\r\n"),stop.Token);
                Assert((await reader.ReadLineAsync(stop.Token))?.StartsWith("HTTP/1.1 200 ")==true,"Persistent TCP response.");while(await reader.ReadLineAsync(stop.Token) is {Length:>0}){}char[] answer=new char[2];await reader.ReadBlockAsync(answer,stop.Token);Assert(new string(answer)=="OK","Persistent TCP body.");
            }
            await Ping();Progress("ControlledHttp3Fault");await local.GetStringAsync(control+"/fault",stop.Token);var fault=await FetchPair(browser);observations.Add(new{phase="controlled-http3-error",resources=fault});
            browserEvents.AddRange(browser.Events);await browser.DisposeAsync();browser=null;
            Progress("PublishingCompatibility");
            if(worker!=null)
            {
                var until=Stopwatch.StartNew();
                await worker.ApplyBrowserWork(stop.Token);
                while(Storage.Read<JsonElement>(Path.Combine(root,"runtime/rules/auto-blocked.json")).GetProperty("rules").EnumerateArray().All(r=>!r.TryGetProperty("process_path",out _)))
                {Assert(until.Elapsed<TimeSpan.FromSeconds(15),"Browser error did not produce an acknowledged compatibility rule.");await Task.Delay(50,stop.Token);await worker.ApplyBrowserWork(stop.Token);}
                Assert(ownTcpProbes>=2,"Two own TCP/TLS probes must precede compatibility.");
                observations.Add(new{phase="automatic-feedback-publication",ownTcpProbes,appliedVersion=publisher.Version,controllerProcessingMs=until.ElapsedMilliseconds});
            }
            else{desired["blocked"]=new[]{PolicyWebRouting.TcpFallback(key)};await publisher.Apply(desired,stop.Token);}
            await Ping();
            // The fixture server closes its own failed QUIC connections. The
            // client never terminates another application's live connections.
            await local.GetStringAsync(control+"/close-quic",stop.Token);await local.GetStringAsync(control+"/healthy",stop.Token);
            string routerLog=Path.Combine(root,"logs/router.log");long compatibilityOffset=new FileInfo(routerLog).Length;
            browser=await Open("CompatibilityTcp");var fallback=await Warm(browser,false);
            // Give the normal Alt-Svc QUIC attempt time to run too.
            await Task.Delay(1000,stop.Token);fallback=await FetchPair(browser);
            observations.Add(new{phase="compatibility",resources=fallback});
            Assert(Complete(fallback[0],"image")&&Complete(fallback[1],"file")&&fallback.All(p=>p.GetProperty("protocol").GetString() is "h2" or "http/1.1"),"Browser TCP fallback must receive and decode both full resources.");
            string compatibilityLog;using(var stream=new FileStream(routerLog,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){stream.Position=compatibilityOffset;using var logReader=new StreamReader(stream);compatibilityLog=await logReader.ReadToEndAsync(stop.Token);}
            Assert(compatibilityLog.Split('\n').Any(line=>line.Contains("match["+blockedIndex+"]",StringComparison.Ordinal)&&line.Contains("=> reject",StringComparison.Ordinal)),"A real browser QUIC attempt must exercise the scoped rejection.");
            Assert(!compatibilityLog.Split('\n').Any(line=>line.Contains("match["+quicIndex+"]",StringComparison.Ordinal)&&line.Contains("=> route",StringComparison.Ordinal)),"No retransmitted QUIC fragment may bypass the compatibility rule.");
            await Ping();browserEvents.AddRange(browser.Events);await browser.DisposeAsync();browser=null;
            using(var udp=new UdpClient())
            {
                byte[] message=Encoding.ASCII.GetBytes("owned-game-udp");await udp.SendAsync(message,new IPEndPoint(IPAddress.Parse(Address),443),stop.Token);using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(2000);
                Assert((await udp.ReceiveAsync(deadline.Token)).Buffer.SequenceEqual(message),"Unrelated native UDP must survive.");
            }
            Progress("ManualPriority");string marker="m"+Guid.NewGuid().ToString("N")+".routepilot.invalid";
            Storage.Write(manual,new{version=3,rules=new[]{PolicyWebRouting.TcpFallback(key),new JsonObject{["domain"]=new JsonArray(marker),["network"]="tcp",["port"]=new JsonArray(443)}}});
            for(int i=0;;i++)
            {
                try{using var attempt=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);attempt.CancelAfter(500);await using var proof=await PolicyTransport.Socks(ports.Ack,marker,443,attempt.Token);await proof.ReadExactlyAsync(new byte[16],attempt.Token);break;}
                catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException){Assert(i<15,"Manual rule never acknowledged.");await Task.Delay(75,stop.Token);}
            }
            browser=await Open("ManualQuic");var manualPair=await Warm(browser,true);Assert(Complete(manualPair[0],"image")&&Complete(manualPair[1],"file"),"Manual priority resource integrity.");observations.Add(new{phase="manual-priority",resources=manualPair});browserEvents.AddRange(browser.Events);await browser.DisposeAsync();browser=null;
            Storage.Write(manual,new{version=3,rules=Array.Empty<JsonObject>()});
            if(worker!=null){worker.Clear();await worker.ApplyBrowserWork(stop.Token);}else{desired["blocked"]=Array.Empty<JsonObject>();await publisher.Apply(desired,stop.Token);}
            browser=await Open("RestoredHttp3");var restored=await Warm(browser,true);Assert(Complete(restored[0],"image")&&Complete(restored[1],"file"),"Restored resource integrity.");await Ping();observations.Add(new{phase="restored",resources=restored});browserEvents.AddRange(browser.Events);await browser.DisposeAsync();browser=null;
            if(worker!=null)
            {
                worker.Clear();await worker.ApplyBrowserWork(stop.Token);await local.GetStringAsync(control+"/body-tcp",stop.Token);
                browser=await Open("BodyHealthyTcp");var healthy=await Warm(browser,false);Assert(Complete(healthy[0],"image")&&Complete(healthy[1],"file"),"Healthy TCP resources must complete before fault injection.");
                async Task WaitRule(string route)
                {
                    var timer=Stopwatch.StartNew();
                    while(true){await worker.ApplyBrowserWork(stop.Token);if(Storage.Read<JsonElement>(Path.Combine(root,"runtime/rules/auto-"+route+".json")).GetProperty("rules").EnumerateArray().Any(r=>r.TryGetProperty("process_path",out _)))break;Assert(timer.Elapsed<TimeSpan.FromSeconds(15),"Automatic resource rule was not acknowledged: "+route);await Task.Delay(50,stop.Token);}
                }
                await WaitRule("direct");await local.GetStringAsync(control+"/body-fault",stop.Token);
                var brokenBody=await FetchPair(browser);await browser.Evaluate("true");observations.Add(new{phase="controlled-tcp-body-error",resources=brokenBody});
                Assert(brokenBody.All(p=>!p.GetProperty("ok").GetBoolean()),"Both partial TCP resources must actually fail.");browserEvents.AddRange(browser.Events);await browser.DisposeAsync();browser=null;
                var replacement=Stopwatch.StartNew();await WaitRule("dpi");
                browser=await Open("BodyRecoveredTcp");var repaired=await Warm(browser,false);Assert(Complete(repaired[0],"image")&&Complete(repaired[1],"file"),"Alternative path must receive/decode both entire resources.");
                observations.Add(new{phase="automatic-tcp-body-recovery",resources=repaired,ownTcpProbes,appliedVersion=publisher.Version,controllerAndBrowserMs=replacement.ElapsedMilliseconds});
                browserEvents.AddRange(browser.Events);await browser.DisposeAsync();browser=null;await Ping();worker.Clear();await worker.ApplyBrowserWork(stop.Token);
            }
            var state=JsonSerializer.Deserialize<JsonElement>(await local.GetStringAsync(control+"/status",stop.Token));
            Assert(state.GetProperty("events").EnumerateArray().Any(e=>e.GetProperty("broken").GetBoolean()),"The injected HTTP/3 fault must actually occur.");
            string log;using(var stream=new FileStream(routerLog,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){Assert(stream.Length<=4*1024*1024,"Owned router log exceeded the fixture budget.");using var logReader=new StreamReader(stream);log=await logReader.ReadToEndAsync(stop.Token);}
            Assert(log.Split('\n').Any(line=>line.Contains("match["+blockedIndex+"]",StringComparison.Ordinal)&&line.Contains("=> reject",StringComparison.Ordinal)),"The browser must exercise the scoped QUIC rule, not a missing-canary rejection.");
            Assert(Current(),"Pilot changed during test.");success=true;Progress("Passed");
        }
        catch(Exception ex){if(browser!=null)browserEvents.AddRange(browser.Events);failure=ex.GetType().Name+": "+ex.Message;Progress("Failed");}
        finally
        {
            if(browser!=null)await browser.DisposeAsync();
            if(control!=null)try{observations.Add(new{phase="server-events",state=JsonSerializer.Deserialize<JsonElement>(await local.GetStringAsync(control+"/status"))});await local.GetStringAsync(control+"/shutdown");}catch{}
            using var close=new CancellationTokenSource(12000);try{await helper.WaitForExitAsync(close.Token);}catch(OperationCanceledException){helper.Kill(true);await helper.WaitForExitAsync();}
            await Task.WhenAll(helperOut,helperErr);
        }
        var report=new{success,failure,finishedUtc=DateTime.UtcNow,pilotPreserved=Current(),observations,browserEvents,browserFeedbackVerified=feedback&&success,automaticPublicCertificateFallbackVerified=false,scope="Owned VMware TUN /32 and generated HTTPS/HTTP3 resources; clean headless Chromium. Feedback mode wires actual CDP request failures to the production worker/selector/ack publisher using a pinned owned TLS probe transport; it does not provide a user-browser extension or external ingress. Healthy/manual/restored H3 use the synthetic-origin QUIC flag, TCP compatibility uses ordinary Alt-Svc attempts. Public certificate fallback is covered by the separate public lab. No default route/DNS/profile changes; server retires its own failed QUIC; no existing-stream migration claim"};
        Storage.Write(Path.Combine(root,"result.json"),report);Console.WriteLine(JsonSerializer.Serialize(new{success,root,failure}));if(!success)throw new IOException(failure);
    }

    internal sealed class Browser:IAsyncDisposable
    {
        readonly Process _process;readonly ClientWebSocket _socket;readonly CancellationToken _stop;readonly string _phase,_domain,_application;int _next;
        readonly PolicyWorker? _feedback;
        readonly Dictionary<string,string?> _requests=new();
        readonly Task<string> _error,_output;
        internal readonly List<object> Events=new();
        Browser(Process process,ClientWebSocket socket,string phase,string domain,string application,CancellationToken stop,PolicyWorker? feedback){_process=process;_socket=socket;_phase=phase;_domain=domain;_application=application;_stop=stop;_feedback=feedback;_error=process.StandardError.ReadToEndAsync();_output=process.StandardOutput.ReadToEndAsync();}
        internal static async Task<Browser> Open(string exe,string profile,string? spki,HttpClient local,CancellationToken stop,string? publicDomain=null,string? publicAddress=null,PolicyWorker? feedback=null)
        {
            bool synthetic=publicDomain==null;string domain=publicDomain??Domain,address=publicAddress??Address;
            Assert(synthetic||(domain is "cloudflare-quic.com" or "www.cloudflare.com"&&IPAddress.TryParse(address,out var ip)&&ip.AddressFamily==AddressFamily.InterNetwork),"Only fixed public lab origins are allowed.");
            Directory.CreateDirectory(profile);var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
            foreach(string arg in new[]{"--headless","--disable-gpu","--no-first-run","--no-default-browser-check","--disable-background-networking","--disable-component-update","--disable-sync","--disable-domain-reliability","--disable-features=MediaRouter,OptimizationHints,AutofillServerCommunication,UseDnsHttpsSvcbAlpn","--remote-debugging-port=0","--user-data-dir="+profile,"about:blank"})info.ArgumentList.Add(arg);
            // Force is needed to allow the synthetic QUIC root, but also
            // suppresses normal TCP fallback. Therefore do not claim this
            // fixture verifies automatic fallback with a publicly trusted cert.
            if(synthetic){info.ArgumentList.Add("--ignore-certificate-errors-spki-list="+spki);if(!Path.GetFileName(profile).EndsWith("Tcp",StringComparison.Ordinal))info.ArgumentList.Add("--origin-to-force-quic-on="+Domain+":443");}
            // Resolve only the fixture origin in this disposable profile.
            info.ArgumentList.Add("--no-proxy-server");
            info.ArgumentList.Add("--host-resolver-rules=MAP "+domain+" "+address+", MAP * ~NOTFOUND, EXCLUDE localhost");
            info.ArgumentList.Add("--log-net-log="+Path.Combine(profile,"netlog.json"));
            var process=Process.Start(info)??throw new IOException("Pinned browser did not start.");var socket=new ClientWebSocket();var page=new Browser(process,socket,Path.GetFileName(profile),domain,exe,stop,feedback);
            try
            {
                string portFile=Path.Combine(profile,"DevToolsActivePort");int port=0;
                for(int i=0;port==0;i++)
                {
                    if(process.HasExited)throw new IOException("Pinned browser startup: "+await page._error);Assert(i<150,"Browser debugging startup deadline.");
                    try{using var stream=new FileStream(portFile,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var reader=new StreamReader(stream);if(int.TryParse(await reader.ReadLineAsync(stop),out int candidate)&&candidate is >0 and <=65535)port=candidate;}
                    catch(IOException){}
                    if(port==0)await Task.Delay(100,stop);
                }
                using var request=new HttpRequestMessage(HttpMethod.Put,"http://127.0.0.1:"+port+"/json/new?about:blank");using var response=await local.SendAsync(request,stop);response.EnsureSuccessStatusCode();
                var tab=JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(stop));var endpoint=new Uri(tab.GetProperty("webSocketDebuggerUrl").GetString()!);Assert(endpoint.Host=="127.0.0.1"||endpoint.Host=="localhost","Browser debugger must stay loopback.");
                await socket.ConnectAsync(endpoint,stop);await page.Call("Page.enable",new{});await page.Call("Network.enable",new{});await page.Call("Page.navigate",new{url="https://"+domain+(synthetic?"/":"/cdn-cgi/trace")});
                for(int i=0;i<80;i++){var state=await page.Evaluate("document.readyState==='complete' && location.hostname==='"+domain+"'");if(state.ValueKind==JsonValueKind.True)return page;await Task.Delay(100,stop);}
                throw new IOException("Owned browser page did not load.");
            }
            catch{await page.DisposeAsync();throw;}
        }
        async Task<JsonElement> Call(string method,object args)
        {
            int id=++_next;using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop);deadline.CancelAfter(12000);
            await _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new{id,method,@params=args}),WebSocketMessageType.Text,true,deadline.Token);
            while(true)
            {
                using var message=new MemoryStream();byte[] buffer=new byte[8192];WebSocketReceiveResult frame;
                do{frame=await _socket.ReceiveAsync(buffer,deadline.Token);if(frame.MessageType==WebSocketMessageType.Close)throw new IOException("Owned browser debugger closed.");message.Write(buffer,0,frame.Count);Assert(message.Length<=1024*1024,"Oversized own browser message.");}while(!frame.EndOfMessage);
                using var json=JsonDocument.Parse(message.ToArray());var value=json.RootElement;
                if(value.TryGetProperty("method",out var eventName)&&value.TryGetProperty("params",out var parameters))
                {
                    string? requestId=parameters.TryGetProperty("requestId",out var rid)?rid.GetString():null;
                    if(_feedback!=null&&requestId!=null&&eventName.GetString()=="Network.requestWillBeSent")
                    {
                        if(_requests.Remove(requestId,out var previous))_feedback.FinishBrowserResource(previous);
                        var request=parameters.GetProperty("request");
                        if(_requests.Count<512&&Uri.TryCreate(request.GetProperty("url").GetString(),UriKind.Absolute,out var target)&&target.Scheme=="https"&&target.IdnHost==_domain&&target.Port==443&&target.UserInfo.Length==0)
                            _requests[requestId]=_feedback.BeginBrowserResource(_domain,_application,request.GetProperty("method").GetString()??"",parameters.GetProperty("type").GetString()??"",DateTime.UtcNow);
                    }
                    if(eventName.GetString()=="Network.responseReceived"){var r=parameters.GetProperty("response");if(Uri.TryCreate(r.GetProperty("url").GetString(),UriKind.Absolute,out var url)&&url.Host==_domain)Events.Add(new{phase=_phase,kind=url.AbsolutePath,protocol=r.GetProperty("protocol").GetString(),status=r.GetProperty("status").GetDouble(),tls=r.TryGetProperty("securityDetails",out var security)?security.GetProperty("protocol").GetString():null});}
                    if(eventName.GetString()=="Network.loadingFailed")
                    {
                        string error=parameters.GetProperty("errorText").GetString()??"";bool accepted=false;
                        if(requestId!=null&&_requests.Remove(requestId,out var ticket))accepted=_feedback?.ObserveBrowserFailure(ticket,error,parameters.TryGetProperty("canceled",out var cancelled)&&cancelled.GetBoolean(),DateTime.UtcNow)==true;
                        Events.Add(new{phase=_phase,error,feedbackAccepted=accepted});
                    }
                    if(eventName.GetString()=="Network.loadingFinished"&&requestId!=null&&_requests.Remove(requestId,out var finished))_feedback?.FinishBrowserResource(finished);
                    if(Events.Count>96)Events.RemoveAt(0);
                }
                if(value.TryGetProperty("id",out var responseId)&&responseId.GetInt32()==id){if(value.TryGetProperty("error",out var error))throw new IOException("Owned browser command: "+error.GetProperty("message").GetString());return value.GetProperty("result").Clone();}
            }
        }
        internal async Task<JsonElement> Evaluate(string expression)
        {
            var result=await Call("Runtime.evaluate",new{expression,awaitPromise=true,returnByValue=true});if(result.TryGetProperty("exceptionDetails",out _))throw new IOException("Owned browser fixture script failed.");return result.GetProperty("result").GetProperty("value").Clone();
        }
        public async ValueTask DisposeAsync()
        {
            if(!_process.HasExited){try{await Call("Browser.close",new{});}catch{}using var close=new CancellationTokenSource(2000);try{await _process.WaitForExitAsync(close.Token);}catch(OperationCanceledException){_process.Kill(true);await _process.WaitForExitAsync();}}
            foreach(var ticket in _requests.Values)_feedback?.FinishBrowserResource(ticket);_requests.Clear();
            await Task.WhenAll(_error,_output);_socket.Dispose();_process.Dispose();
        }
    }
}
