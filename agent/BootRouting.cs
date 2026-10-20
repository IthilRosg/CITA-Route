using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
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
    public Dictionary<string,string> InputHashes{get;set;}=new();
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
    volatile bool _shutdown;
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
    internal static bool WireSockReady(int exitCode,string status)
    {
        if(exitCode!=0)return false;
        const System.Text.RegularExpressions.RegexOptions options=System.Text.RegularExpressions.RegexOptions.IgnoreCase|System.Text.RegularExpressions.RegexOptions.CultureInvariant;
        string text=status.Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(text,@"\A(?:Not connected|NotConnected|No connection|Disconnected|Не\s+подключено|Отключено|Нет\s+подключения)\z",options)||
            System.Text.RegularExpressions.Regex.IsMatch(text,@"\A(?:Connected to|Подключено к серверу)\s+\S",options);
    }
    static void State(BootSettings settings,string phase,string message,DateTime? boot=null)=>Storage.Write(StartupPaths.StateFile,new StartupState{
        SourceRoot=settings.SourceRoot,Enabled=settings.Enabled,Phase=phase,Message=message,UpdatedUtc=DateTime.UtcNow,BootUtc=boot??BootUtc});
    // Keep the caller's exclusive setup reservation alive across the native
    // process boundary. A Boolean 'skip locks' would race other setup operations.
    static FileStream BorrowSetup(string root,int owner,long handle)
    {
        using var process=OpenProcess(0x0040,false,owner);
        if(process.IsInvalid||!DuplicateHandle(process,new IntPtr(handle),GetCurrentProcess(),out var copy,0,false,2))
            throw new IOException("The setup reservation is no longer available.");
        try
        {
            var path=new StringBuilder(32768);
            uint length=GetFinalPathNameByHandle(copy,path,(uint)path.Capacity,0);
            string actual=path.ToString();if(actual.StartsWith(@"\\?\"))actual=actual[4..];
            if(length==0||length>=path.Capacity||!Same(actual,Child(root,"runtime\\setup.lock")))throw new IOException("Wrong setup reservation.");
            bool reserved=false;
            try{using var competing=File.Open(actual,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite|FileShare.Delete);}
            catch(IOException ex)when(ex.HResult==unchecked((int)0x80070020)){reserved=true;}
            if(!reserved)throw new IOException("The setup reservation is not exclusive.");
            return new FileStream(copy,FileAccess.ReadWrite);
        }
        catch{copy.Dispose();throw;}
    }
    [DllImport("kernel32.dll",SetLastError=true)] static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int id);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool DuplicateHandle(SafeProcessHandle source,IntPtr handle,IntPtr target,out SafeFileHandle copy,uint access,bool inherit,uint options);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder path,uint size,uint flags);
    internal static void Configure(string root,bool enabled,bool refresh=false,int setupOwner=0,long setupHandle=0)
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
            if(setupOwner>0)locks.Add(BorrowSetup(root,setupOwner,setupHandle));
            else foreach(string name in new[]{"setup.lock","connection-operation.lock","network-control.lock","installation-maintenance.lock","update-preparation.lock"})
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
            foreach(string file in new[]{"force-direct.txt","force-vpn.txt","private\\controller-token.txt"})settings.InputHashes[file]=Hash(Child(stage,file));
            Copy("rules\\telegram-cidr.txt");
            Directory.CreateDirectory(Path.Combine(stage,"data"));Directory.CreateDirectory(Path.Combine(stage,"logs"));
            Storage.Write(Path.Combine(stage,"data","connection.json"),new{SourceProfile=config.WireSockSourceProfile,CliPath=config.WireSockCliPath,ExpectedEgressIp=config.ExpectedEgressIp,UserSid="S-1-5-18",UserProfile=config.UserProfile});
            byte[] policyBytes=File.ReadAllBytes(Child(root,"data\\routing-policy.json"));
            var policy=System.Text.Json.JsonSerializer.Deserialize<PolicyOptions>(policyBytes,Storage.Json)??throw new IOException("Missing routing preferences.");
            settings.InputHashes["data\\routing-policy.json"]=Convert.ToHexString(SHA256.HashData(policyBytes));
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
            bool stopped=refresh&&prior.Owns(root)&&prior.Phase=="StoppedForBoot"&&StartupPaths.SameBoot(prior.BootUtc,BootUtc);
            State(settings,stopped?"StoppedForBoot":"ReadyForBoot",stopped?
                "Профиль автоподключения обновлён. Ручное отключение сохранено до следующего запуска Windows.":
                "Готово к следующему запуску Windows. Профиль и правила сохранены защищённой копией.");
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
        // These commands intentionally leave Watch/policy children alive. Their
        // inherited output handles must not hold completion open after the
        // primary script exits. Script diagnostics already use owned log files.
        var result=await Tools.Run(executable,new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",Path.Combine(StartupPaths.Client,"SmartRouting.ps1"),"-Action",action},125,cancellation,captureOutput:false);
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
            var evidence=new StartupEvidence{BootUtc=BootUtc};
            string evidenceFile=Path.Combine(_config.DataDirectory,"startup-boot-proof.json");
            Storage.Write(evidenceFile,evidence);
            void ValidateSnapshot()
            {
            if(!File.Exists(Path.Combine(_config.AppRoot,"product.json")))throw new IOException("Source installation was removed.");
            using var product=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_config.AppRoot,"product.json")));
            if(product.RootElement.GetProperty("version").GetString()!=settings.Version||settings.ConnectionSignature!=ConnectionSignature(_config))throw new IOException("Refresh startup after updating CITA Route or its profile.");
            foreach(var entry in settings.InputHashes)if(Hash(Child(_config.AppRoot,entry.Key))!=entry.Value)throw new IOException("Refresh startup after changing routing preferences.");
            if(settings.InputHashes.Count!=4)throw new IOException("Incomplete startup input snapshot.");
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
            }
            ValidateSnapshot();
            State(settings,"WaitingForNetwork","Ожидание сети и службы WireSock…");
            var clock=Stopwatch.StartNew();
            while(true)
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
                        if(WireSockReady(observation.Code,observation.Text))break;
                    }
                    catch(OperationCanceledException)when(linked.IsCancellationRequested){throw;}
                    catch(Exception ex)when(ex is IOException or OperationCanceledException or System.ComponentModel.Win32Exception){}
                }
                if(clock.Elapsed>=TimeSpan.FromSeconds(30))
                {
                    State(settings,"WaitingForNetwork","Ожидание сети и службы WireSock… Можно отменить кнопкой отключения.");
                    clock.Restart();
                }
                await Task.Delay(2000,linked.Token);
            }
            linked.Token.ThrowIfCancellationRequested();
            // The source may have changed while startup waited for networking.
            ValidateSnapshot();
            State(settings,"Starting","Подключение WireSock, применение маршрутов и проверка HTTPS…");
            _routeAttempted=true;
            if(await Script("Start",linked.Token)!=0)throw new IOException("Startup connection did not pass its checks.");
            var connected=ComponentLiveness.Read<OrdinarySession>(Path.Combine(StartupPaths.Client,"runtime","session.json"));
            using(var health=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(StartupPaths.Client,"runtime","session-health.json"))))
            {
                var proof=health.RootElement;
                if(!connected.Running||!connected.BridgeReady||proof.GetProperty("sessionId").GetString()!=connected.Id||proof.GetProperty("bridgeGeneration").GetInt32()!=connected.BridgeGeneration||!proof.GetProperty("healthy").GetBoolean()||(DateTime.UtcNow-proof.GetProperty("updatedUtc").GetDateTime().ToUniversalTime()).TotalSeconds is <0 or >90)throw new IOException("Startup readiness was not independently confirmed.");
            }
            evidence.SessionId=connected.Id;evidence.BridgeGeneration=connected.BridgeGeneration;
            evidence.InteractiveUsersAtConnection=StartupEvidence.CountUsers();evidence.ConnectedUtc=DateTime.UtcNow;
            Storage.Write(evidenceFile,evidence);
            State(settings,"Connected","Маршрут подключён службой при запуске Windows.");
        }
        catch(OperationCanceledException)when(linked.IsCancellationRequested){}
        catch(Exception)
        {
            bool restored=!_routeAttempted;
            if(_routeAttempted)
                try{restored=await Script("Stop",CancellationToken.None)==0;}catch{}
            State(settings,restored?"NeedsAttention":"StopFailed",restored?"Подключение при запуске не завершено. Открой CITA Route и проверь подключение.":"Запуск не завершён, отключение не подтверждено. Проверь WireSock и CITA Route.");
        }
        finally{if(_shutdown)PrepareForShutdown();_serial.Release();}
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
    internal void PrepareForShutdown()
    {
        _shutdown=true;
        try
        {
            var state=StartupPaths.Read();
            if(state.Enabled&&state.Owns(_config.AppRoot))
            {
                // Hybrid shutdown can retain kernel uptime. A Windows shutdown,
                // unlike an ordinary service restart, explicitly rearms next start.
                state.BootUtc=default;Storage.Write(StartupPaths.StateFile,state);
            }
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
    }
}
