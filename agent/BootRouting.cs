using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Security.Cryptography;
using Cita.Startup;

namespace RoutePilot.Agent;

internal sealed class BootSettings
{
    public int Schema{get;set;}=1;
    public bool Enabled{get;set;}
    public string SourceRoot{get;set;}="";
    public string Version{get;set;}="";
    public string ConnectionSignature{get;set;}="";
    public Dictionary<string,string> CodeHashes{get;set;}=new();
}

// A fixed, administrator-owned snapshot is the only startup execution root.
// Never execute scripts or engines from the user's mutable installation as SYSTEM.
internal sealed class BootRouting
{
    static string SettingsFile=>Path.Combine(StartupPaths.AgentDirectory,"startup-settings.json");
    internal static DateTime BootUtc=>DateTime.UtcNow-TimeSpan.FromMilliseconds(Environment.TickCount64);
    readonly AgentConfig _config;
    readonly SemaphoreSlim _serial=new(1,1);
    readonly CancellationTokenSource _cancel=new();
    Task? _stopTask;
    bool _routeAttempted;
    public BootRouting(AgentConfig config)=>_config=config;
    internal static BootSettings? Settings()
    {try{return ComponentLiveness.Read<BootSettings>(SettingsFile,131072);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException){return null;}}
    internal static bool OwnsSnapshot(string source)=>Settings() is {Schema:1} settings&&Same(settings.SourceRoot,source);
    static bool Same(string first,string second)=>Path.GetFullPath(first).TrimEnd('\\').Equals(Path.GetFullPath(second).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);
    static void NoLinks(string path)
    {
        for(string? current=Path.GetFullPath(path);current!=null;current=Path.GetDirectoryName(current))
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("Startup paths cannot contain links.");
    }
    static string Child(string root,string relative)
    {
        string path=Path.GetFullPath(Path.Combine(root,relative));
        if(!path.StartsWith(Path.GetFullPath(root).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))throw new IOException("Invalid startup snapshot path.");
        NoLinks(path);return path;
    }
    static string Hash(string path){using var input=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(input));}
    internal static string ConnectionSignature(AgentConfig config)=>Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new{config.WireSockCliPath,config.WireSockSourceProfile,config.SourceEndpoint,config.ExpectedEgressIp}))));
    static void State(BootSettings settings,string phase,string message,DateTime? boot=null)=>Storage.Write(StartupPaths.StateFile,new StartupState{
        SourceRoot=settings.SourceRoot,Enabled=settings.Enabled,Phase=phase,Message=message,UpdatedUtc=DateTime.UtcNow,BootUtc=boot??BootUtc});
    internal static void Configure(string root,bool enabled)
    {
        using var identity=WindowsIdentity.GetCurrent();
        if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)||!Same(AppContext.BaseDirectory,StartupPaths.AgentDirectory))throw new UnauthorizedAccessException("Configure startup using the protected installed agent and Windows confirmation.");
        var config=Storage.Read<AgentConfig>(Path.Combine(StartupPaths.AgentDirectory,"agent-config.json"));
        if(!Same(root,config.AppRoot))throw new InvalidOperationException("This client does not own the protected installation.");
        var prior=StartupPaths.Read();
        if(prior.Active)throw new InvalidOperationException("Disconnect the startup route before changing startup settings.");
        var settings=new BootSettings{Enabled=enabled,SourceRoot=config.AppRoot,Version=typeof(BootRouting).Assembly.GetName().Version!.ToString(3),ConnectionSignature=ConnectionSignature(config)};
        if(!enabled){Storage.Write(SettingsFile,settings);State(settings,"Disabled","Подключение до входа выключено.");return;}
        NoLinks(root);NoLinks(StartupPaths.Client);
        var locks=new List<FileStream>();
        string stage=Path.Combine(StartupPaths.AgentDirectory,"startup-stage-"+Guid.NewGuid().ToString("N"));
        string backup=Path.Combine(StartupPaths.AgentDirectory,"startup-backup-"+Guid.NewGuid().ToString("N"));
        try
        {
            foreach(string name in new[]{"setup.lock","connection-operation.lock","network-control.lock","installation-maintenance.lock","update-preparation.lock"})
                locks.Add(new FileStream(Child(root,Path.Combine("runtime",name)),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None));
            string session=Child(root,"runtime\\session.json");
            if(File.Exists(session)&&ComponentLiveness.Read<OrdinarySession>(session).Running)throw new InvalidOperationException("Disconnect CITA Route before configuring startup.");
            var run=Path.Combine(config.DataDirectory,"run.json");
            if(File.Exists(run)&&Storage.Read<RunState>(run) is var diagnostic&&(diagnostic.Active||diagnostic.Phase==RunPhase.RecoveryFailed))throw new InvalidOperationException("Independent diagnostics own the installation.");
            Directory.CreateDirectory(stage);
            void Copy(string relative,string? source=null,bool code=false)
            {
                string from=source??Child(root,relative),to=Child(stage,relative);NoLinks(from);
                if(new FileInfo(from).Length>256L*1024*1024)throw new IOException("Oversized startup snapshot input.");
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);File.Copy(from,to);
                if(code)settings.CodeHashes[relative]=Hash(to);
            }
            foreach(string script in new[]{"SmartRouting.ps1","FileStorage.ps1","ConnectionEnvironment.ps1","RuleFeeds.ps1","WireSockCli.ps1","WireSockProfiles.ps1","WireSockRecovery.ps1","AgentLease.ps1","SessionRecovery.ps1"})Copy(script,code:true);
            Copy("runtime\\smart-router.exe",Path.Combine(StartupPaths.AgentDirectory,"smart-router.exe"),true);
            foreach(string directory in new[]{"rules","runtime\\rules"})
            {
                string source=Child(root,directory);
                foreach(string file in Directory.EnumerateFiles(source).Where(p=>Path.GetExtension(p) is ".json" or ".srs"))Copy(Path.Combine(directory,Path.GetFileName(file)));
            }
            foreach(string file in new[]{"force-direct.txt","force-vpn.txt","product.json","private\\controller-token.txt"})Copy(file);
            Copy("rules\\telegram-cidr.txt");
            Directory.CreateDirectory(Path.Combine(stage,"data"));Directory.CreateDirectory(Path.Combine(stage,"logs"));
            Storage.Write(Path.Combine(stage,"data","connection.json"),new{SourceProfile=config.WireSockSourceProfile,CliPath=config.WireSockCliPath,ExpectedEgressIp=config.ExpectedEgressIp,UserSid="S-1-5-18",UserProfile=config.UserProfile});
            var policy=Storage.Read<PolicyOptions>(Child(root,"data\\routing-policy.json"));
            if(policy.Mode is not ("Auto" or "Lists"))throw new IOException("Invalid startup routing mode.");
            Storage.Write(Path.Combine(stage,"data","routing-policy.json"),policy);
            // The whole tree inherits the protected agent ACL: user read only.
            // Do not carry source ACLs, mutable binaries, journals or WG secrets.
            if(Directory.Exists(StartupPaths.Client)){NoLinks(StartupPaths.Client);Directory.Move(StartupPaths.Client,backup);}
            try
            {
                Directory.Move(stage,StartupPaths.Client);
                Storage.Write(SettingsFile,settings);
            }
            catch
            {
                if(Directory.Exists(StartupPaths.Client)){NoLinks(StartupPaths.Client);Directory.Delete(StartupPaths.Client,true);}
                if(Directory.Exists(backup)){NoLinks(backup);Directory.Move(backup,StartupPaths.Client);}
                throw;
            }
            if(Directory.Exists(backup)){NoLinks(backup);Directory.Delete(backup,true);}
            // Enabling changes the next boot only, never the current connection.
            State(settings,"ReadyForBoot","Готово к следующему запуску Windows. Профиль и правила сохранены защищённой копией.");
        }
        finally
        {
            foreach(var held in locks.AsEnumerable().Reverse())held.Dispose();
            if(Directory.Exists(stage)){NoLinks(stage);Directory.Delete(stage,true);}
        }
    }
    async Task<int> Script(string action,CancellationToken cancellation)
    {
        string executable=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe");
        var result=await Tools.Run(executable,new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",Path.Combine(StartupPaths.Client,"SmartRouting.ps1"),"-Action",action},125,cancellation);
        // Native command text can contain profile details. Publish fixed UI states only.
        return result.Code;
    }
    public async Task Run(CancellationToken serviceStop)
    {
        var settings=Settings();if(settings is not {Schema:1,Enabled:true}||!Same(settings.SourceRoot,_config.AppRoot))return;
        var prior=StartupPaths.Read();
        if(StartupPaths.SameBoot(prior.BootUtc,BootUtc))return;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(serviceStop,_cancel.Token);
        await _serial.WaitAsync(linked.Token);
        try
        {
            if(!File.Exists(Path.Combine(_config.AppRoot,"product.json")))throw new IOException("Source installation was removed.");
            using var product=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_config.AppRoot,"product.json")));
            if(product.RootElement.GetProperty("version").GetString()!=settings.Version||settings.ConnectionSignature!=ConnectionSignature(_config))throw new IOException("Refresh startup after updating CITA Route or its profile.");
            foreach(var entry in settings.CodeHashes)if(Hash(Child(StartupPaths.Client,entry.Key))!=entry.Value)throw new IOException("Startup code changed.");
            if(settings.CodeHashes.Count!=10)throw new IOException("Incomplete startup code snapshot.");
            string sourceSession=Path.Combine(_config.AppRoot,"runtime","session.json");
            if(File.Exists(sourceSession))
            {
                var ordinary=ComponentLiveness.Read<OrdinarySession>(sourceSession);
                // Journals survive Windows reboot; only a matching live process
                // can make a previous ordinary session block startup.
                if(ordinary.Running&&((ordinary.RouterPid>0&&new ProcessIdentity(ordinary.RouterPid,ordinary.RouterStartedUtc,Path.Combine(_config.AppRoot,"runtime","smart-router.exe")).Matches())||
                    (ordinary.BridgePid>0&&new ProcessIdentity(ordinary.BridgePid,ordinary.BridgeStartedUtc,Path.Combine(StartupPaths.AgentDirectory,"vpn-bridge.exe")).Matches())))throw new IOException("An existing ordinary session requires attention.");
            }
            State(settings,"WaitingForNetwork","Ожидание сети и службы WireSock…");
            var clock=Stopwatch.StartNew();
            while(clock.Elapsed<TimeSpan.FromMinutes(3))
            {
                linked.Token.ThrowIfCancellationRequested();
                string run=Path.Combine(_config.DataDirectory,"run.json");
                bool diagnostic=File.Exists(run)&&Storage.Read<RunState>(run) is var state&&(state.Active||state.Phase==RunPhase.RecoveryFailed);
                bool network=NetworkInterface.GetAllNetworkInterfaces().Any(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211&&n.GetIPProperties().GatewayAddresses.Count>0);
                if(network&&!diagnostic)
                {
                    try
                    {
                        var observation=await Tools.Run(_config.WireSockCliPath,new[]{"status"},4,linked.Token);
                        if(observation.Code==0&&System.Text.RegularExpressions.Regex.IsMatch(observation.Text,@"(?i)Not connected|Disconnected|Connected to|Не\s+подключено|Отключено|Подключено к серверу"))break;
                    }
                    catch(OperationCanceledException)when(linked.IsCancellationRequested){throw;}
                    catch(Exception ex)when(ex is IOException or OperationCanceledException or System.ComponentModel.Win32Exception){}
                }
                await Task.Delay(2000,linked.Token);
            }
            linked.Token.ThrowIfCancellationRequested();
            if(clock.Elapsed>=TimeSpan.FromMinutes(3))throw new TimeoutException("Startup network readiness timed out.");
            State(settings,"Starting","Подключение WireSock, применение маршрутов и проверка HTTPS…");
            _routeAttempted=true;
            if(await Script("Start",linked.Token)!=0)throw new IOException("Startup connection did not pass its checks.");
            var connected=ComponentLiveness.Read<OrdinarySession>(Path.Combine(StartupPaths.Client,"runtime","session.json"));
            using(var health=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(StartupPaths.Client,"runtime","session-health.json"))))
            {
                var proof=health.RootElement;
                if(!connected.Running||!connected.BridgeReady||proof.GetProperty("sessionId").GetString()!=connected.Id||proof.GetProperty("bridgeGeneration").GetInt32()!=connected.BridgeGeneration||!proof.GetProperty("healthy").GetBoolean()||(DateTime.UtcNow-proof.GetProperty("updatedUtc").GetDateTime().ToUniversalTime()).TotalSeconds is <0 or >90)throw new IOException("Startup readiness was not independently confirmed.");
            }
            State(settings,"Connected","Маршрут подключён до входа в Windows.");
        }
        catch(OperationCanceledException)when(linked.IsCancellationRequested){}
        catch(Exception)
        {
            bool restored=!_routeAttempted;
            if(_routeAttempted)
                try{restored=await Script("Stop",CancellationToken.None)==0;}catch{}
            State(settings,restored?"NeedsAttention":"StopFailed",restored?"Подключение при запуске не завершено. Открой CITA Route и проверь подключение.":"Запуск не завершён, отключение не подтверждено. Проверь WireSock и CITA Route.");
        }
        finally{_serial.Release();}
    }
    public void RequestStop()
    {
        if(_stopTask is {IsCompleted:false})return;
        _cancel.Cancel();
        _stopTask=Task.Run(async()=>
        {
            var settings=Settings();if(settings==null)return;
            await _serial.WaitAsync();
            try
            {
                State(settings,"Stopping","Отключение маршрута, запущенного до входа…");
                string session=Path.Combine(StartupPaths.Client,"runtime","session.json");
                bool running=File.Exists(session)&&ComponentLiveness.Read<OrdinarySession>(session).Running;
                if((_routeAttempted||running)&&await Script("Stop",CancellationToken.None)!=0)throw new IOException("Startup disconnect unconfirmed.");
                State(settings,"StoppedForBoot","Отключено вручную. Следующий автозапуск — при новом запуске Windows.");
            }
            catch(Exception){State(settings,"StopFailed","Отключение не подтверждено. Проверь подключение в WireSock.");}
            finally{_serial.Release();}
        });
    }
    internal bool Disable(string source)
    {
        var settings=Settings();
        if(settings==null||!Same(settings.SourceRoot,source)||StartupPaths.Read().Active)return false;
        settings.Enabled=false;Storage.Write(SettingsFile,settings);State(settings,"Disabled","Подключение до входа выключено.");return true;
    }
}
