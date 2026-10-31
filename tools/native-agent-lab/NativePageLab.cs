using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RoutePilot.Agent;

internal static class NativePageLab
{
    static readonly string[] Pages=["https://windows64.net/windows-11-x64-skachat-torrent/ne-oficialnye-sborki/","https://www.ryuugames.com/eng-the-blood-of-yidhra-uncensored/","https://srnky.com/"];
    internal static async Task<object> Measure(NativeGuardedSession? runtime,string root,CancellationToken stop,bool media=false)
    {
        string image=@"C:\CitaLab\Browser\chrome-win64\chrome.exe";
        using(var stream=File.OpenRead(image))if(Convert.ToHexString(SHA256.HashData(stream))!="5AF44A250A2139D4E0864C07227BF0687A6D12655BAB9682A5BE784B41BC82E1")throw new IOException("Pinned browser differs.");
        var results=new List<object>();int pid=runtime?.Core.ProcessId??0;int auditsFailed=0;bool youtubePlaybackAdvanced=false;
        var pages=media?new[]{"https://www.youtube.com/watch?v=dQw4w9WgXcQ","https://discord.com/app"}:Pages;
        foreach(string url in pages)
        {
            string auditStage="launch";var auditClock=Stopwatch.StartNew();
            string profile=Path.Combine(root,"page-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(profile);
            var info=new ProcessStartInfo(image){UseShellExecute=false,CreateNoWindow=true};
            foreach(string arg in new[]{"--headless=new","--no-first-run","--no-default-browser-check","--disable-background-networking","--no-proxy-server","--remote-debugging-port=0","--user-data-dir="+profile,"about:blank"})info.ArgumentList.Add(arg);
            if(media)info.ArgumentList.Insert(info.ArgumentList.Count-1,"--autoplay-policy=no-user-gesture-required");
            using var browser=Process.Start(info)??throw new IOException("Browser did not start.");
            try
            {
                using var budget=CancellationTokenSource.CreateLinkedTokenSource(stop);budget.CancelAfter(TimeSpan.FromSeconds(18));var ct=budget.Token;
                auditStage="devtools-start";string active=Path.Combine(profile,"DevToolsActivePort");while(!File.Exists(active))await Task.Delay(100,ct);
                int port=int.Parse((await File.ReadAllLinesAsync(active,ct))[0]);using var http=new HttpClient(new SocketsHttpHandler{UseProxy=false});
                using var tabs=JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:"+port+"/json/list",ct));string socket=tabs.RootElement.EnumerateArray().First(t=>t.GetProperty("type").GetString()=="page").GetProperty("webSocketDebuggerUrl").GetString()!;
                using var ws=new ClientWebSocket();await ws.ConnectAsync(new Uri(socket),ct);int sequence=0;var hosts=new Dictionary<string,string>();var failures=new List<object>();var responses=new List<object>();
                async Task<JsonElement> Receive()
                {
                    using var bytes=new MemoryStream();byte[] buffer=new byte[8192];WebSocketReceiveResult read;
                    do{read=await ws.ReceiveAsync(buffer,ct);if(read.MessageType==WebSocketMessageType.Close)throw new IOException("Browser debugger closed.");bytes.Write(buffer,0,read.Count);if(bytes.Length>2097152)throw new IOException("Browser event too large.");}while(!read.EndOfMessage);
                    using var json=JsonDocument.Parse(bytes.ToArray());return json.RootElement.Clone();
                }
                void Event(JsonElement message)
                {
                    if(!message.TryGetProperty("method",out var method)||!message.TryGetProperty("params",out var p))return;
                    if(method.GetString()=="Network.requestWillBeSent"&&Uri.TryCreate(p.GetProperty("request").GetProperty("url").GetString(),UriKind.Absolute,out var target)&&hosts.Count<1024)hosts[p.GetProperty("requestId").GetString()!]=target.Host;
                    if(method.GetString()=="Network.loadingFailed"&&failures.Count<128)failures.Add(new{host=hosts.GetValueOrDefault(p.GetProperty("requestId").GetString()!),type=p.GetProperty("type").GetString(),error=p.GetProperty("errorText").GetString(),blockedReason=p.TryGetProperty("blockedReason",out var blocked)?blocked.GetString():null,cors=p.TryGetProperty("corsErrorStatus",out var cors)?cors.GetProperty("corsError").GetString():null});
                    if(method.GetString()=="Network.responseReceived"&&responses.Count<512){var r=p.GetProperty("response");responses.Add(new{host=hosts.GetValueOrDefault(p.GetProperty("requestId").GetString()!),type=p.GetProperty("type").GetString(),status=r.GetProperty("status").GetDouble(),protocol=r.GetProperty("protocol").GetString()});}
                }
                async Task<JsonElement> Command(string method,object parameters)
                {
                    int id=++sequence;await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{id,method,@params=parameters})),WebSocketMessageType.Text,true,ct);
                    while(true){var message=await Receive();Event(message);if(message.TryGetProperty("id",out var got)&&got.GetInt32()==id){if(message.TryGetProperty("error",out _))throw new IOException("Browser command failed.");return message;}}
                }
                auditStage="devtools-connect";await Command("Network.enable",new{});await Command("Page.enable",new{});var clock=Stopwatch.StartNew();auditStage="navigate";var navigation=await Command("Page.navigate",new{url});
                // Frequent no-op evaluation drains network events while the page
                // executes normally. No link clicks, login or form submissions.
                auditStage="page-events";for(int i=0;i<30;i++){await Task.Delay(250,ct);string expression=media?(i==16?"(()=>{const v=document.querySelector('video');window.__citaVideoStart=v?v.currentTime:null;return true})()":i>=8&&i%4==0?"(()=>{const v=document.querySelector('video');if(v&&v.paused){v.muted=true;v.play().then(()=>window.__citaPlay='started').catch(e=>window.__citaPlay=e.name);}return true})()":"true"):(i%2==0?"window.scrollBy(0,Math.max(innerHeight,800));true":"true");await Command("Runtime.evaluate",new{expression,returnByValue=true});}
                var state=await Command("Runtime.evaluate",new{expression="JSON.stringify({ready:document.readyState,images:Array.from(document.images).slice(0,200).map(i=>({host:(()=>{try{return new URL(i.currentSrc||i.src,location.href).hostname}catch{return null}})(),placeholder:(i.currentSrc||i.src).startsWith(\"data:\"),lazy:i.loading===\"lazy\",complete:i.complete,loaded:i.naturalWidth>0})),resources:performance.getEntriesByType('resource').length,bodyPresent:!!document.body,video:(()=>{const v=document.querySelector('video');const p=document.querySelector('#movie_player');const s=p&&typeof p.getPlayerState==='function'?p.getPlayerState():null;return v?{readyState:v.readyState,networkState:v.networkState,startTime:window.__citaVideoStart??null,currentTime:v.currentTime,paused:v.paused,error:v.error?v.error.code:null,playAttempt:['started','NotAllowedError','NotSupportedError','AbortError'].includes(window.__citaPlay)?window.__citaPlay:null,playerState:Number.isInteger(s)?s:null,ad:!!document.querySelector('.ad-showing')}:null})()})",returnByValue=true});
                string evidence=state.GetProperty("result").GetProperty("result").GetProperty("value").GetString()!;
                var dom=JsonSerializer.Deserialize<JsonElement>(evidence);
                if(media&&new Uri(url).Host=="www.youtube.com"&&dom.TryGetProperty("video",out var video)&&video.ValueKind==JsonValueKind.Object&&video.TryGetProperty("startTime",out var start)&&start.ValueKind==JsonValueKind.Number&&video.TryGetProperty("currentTime",out var end)&&end.ValueKind==JsonValueKind.Number)youtubePlaybackAdvanced=end.GetDouble()-start.GetDouble()>=2;
                results.Add(new{page=new Uri(url).Host,elapsedMs=clock.ElapsedMilliseconds,navigationError=navigation.GetProperty("result").TryGetProperty("errorText",out var error)?error.GetString():null,dom,requests=hosts.Count,hosts=hosts.Values.Distinct().Take(64).ToArray(),failures,responses});
            }
            catch(Exception error)when(error is IOException or HttpRequestException or OperationCanceledException or WebSocketException){auditsFailed++;results.Add(new{page=new Uri(url).Host,auditFailure=error.GetType().Name,stage=auditStage,elapsedMs=auditClock.ElapsedMilliseconds});}
            finally{if(!browser.HasExited){browser.Kill(true);await browser.WaitForExitAsync(CancellationToken.None);}for(int retry=0;retry<3&&Directory.Exists(profile);retry++){try{Directory.Delete(profile,true);}catch(IOException)when(retry<2){await Task.Delay(300,CancellationToken.None);}}}
            if(runtime!=null&&(runtime.Core.ProcessId!=pid||runtime.Core.State!="Active"))throw new IOException("Page audit changed native core.");
        }
        return new{completed=true,allPagesAudited=auditsFailed==0,youtubePlaybackAdvanced=media?youtubePlaybackAdvanced:(bool?)null,sameCore=runtime!=null,browser="Pinned Chrome, headless, fresh isolated profiles; normal TLS, system DNS, no proxy",scope=media?"Anonymous YouTube playback attempt (muted, autoplay permitted) and Discord page; no login, no claim of Discord voice or long playback quality":"Anonymous page navigation and lazy-scroll resource observation; no user data, no claim of interactive feature success",results};
    }
}
