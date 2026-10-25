using System.Diagnostics;
using System.IO.Pipes;
using System.Net.NetworkInformation;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace RoutePilot.Agent;

public sealed class AgentHost
{
    readonly AgentConfig _config;
    readonly RunStore _store;
    readonly SemaphoreSlim _gate=new(1);
    Stopwatch? _workTimer;
    int _workSeconds;
    bool _crashDryRun;
    volatile string? _recoveryRequested;
    readonly string _proof;
    ProcessIdentity? _operation;
    BootRouting? _boot;
    NativeServiceRoute? _native;
    string? _nativeFailure;
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetNamedPipeClientProcessId(IntPtr handle,out uint pid);
    public AgentHost(AgentConfig config)
    {
        _config=config;
        string expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RoutePilot");
        if(!Path.GetFullPath(config.DataDirectory).Equals(Path.GetFullPath(expected),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unexpected service data directory.");
        _store=new RunStore(config.DataDirectory);_proof=Path.Combine(config.DataDirectory,"guard-proof.json");
        string operation=Path.Combine(config.DataDirectory,"operation.json");
        if(File.Exists(operation))_operation=Storage.Read<ProcessIdentity>(operation);
    }
    public void RequestRecovery(string reason)=>_recoveryRequested=reason;
    public void PrepareForShutdown()=>_boot?.PrepareForShutdown();
    public async Task Run(CancellationToken stop)
    {
        SaveVpnusProof();
        if(_store.State.Active || _store.State.Phase==RunPhase.RecoveryFailed)
        {
            if(_store.State.Kind==TestKind.DryRun && _store.State.Active)_store.State.Results.Add(new("IndependentGuard",true,"The restarted service detected and recovered an unfinished dry run."));
            await Recover("Service restarted with an unfinished run.");
        }
        var listener=Listen(stop);
        Task native=Task.CompletedTask;
        try{_native=NativeServiceRoute.TryLoad();if(_native!=null)native=_native.Run(stop);}catch(Exception e){_nativeFailure=e.GetType().Name;}
        Task startup=Task.CompletedTask;
        if(_native==null&&_nativeFailure==null){_boot=new BootRouting(_config);startup=_boot.Run(stop);}
        using var resourceStop=CancellationTokenSource.CreateLinkedTokenSource(stop);
        var resources=Task.Run(()=>ResourceMonitor.Run(_config.DataDirectory,_config.AppRoot,resourceStop.Token));
        try
        {
            while(!stop.IsCancellationRequested)
            {
                await Task.Delay(300,stop);
                await _gate.WaitAsync(stop);
                try
                {
                    var state=_store.State;
                    string? forced=_recoveryRequested;_recoveryRequested=null;
                    if(forced!=null && (state.Active || state.Phase==RunPhase.RecoveryFailed)){await Recover(forced);continue;}
                    if(state.Phase!=RunPhase.Testing)continue;
                    if(state.SystemIsolated && NetworkProbe.Vpnus!=null)
                    {state.Results.Add(new("CompetingVpn",false,"VPNUS reappeared during isolated measurements."));await Recover("Competing VPN detected.");continue;}
                    if(state.Route!=null)
                    {
                        bool same;try{same=state.NetworkSignature==Tools.NetworkSignature(state.PhysicalInterfaceIndex);}catch{same=false;}
                        if(!same){state.Results.Add(new("NetworkChanged",false,"The physical interface, address, gateway or DNS changed."));await Recover("Network changed.");continue;}
                    }
                    if(_crashDryRun && state.Kind==TestKind.DryRun && _workTimer?.Elapsed.TotalSeconds>=1 && File.Exists(Path.Combine(Tools.RunFolder(_config,state.Id),"worker-held.ready")))
                    {Log("Intentional dry-run service restart test.");Environment.Exit(73);}
                    bool expired=_workTimer==null || _workTimer.Elapsed.TotalSeconds>=_workSeconds;
                    var worker=state.Processes.FirstOrDefault();
                    if(expired)
                    {
                        if(state.Kind==TestKind.DryRun)state.Results.Add(new("IndependentGuard",true,"The timed-out worker was stopped independently."));
                        else state.Results.Add(new("Deadline",false,"The test exceeded its fixed work budget."));
                        await Recover("Work deadline reached.");continue;
                    }
                    if(worker!=null && !worker.Matches())
                    {
                        string result=Path.Combine(Tools.RunFolder(_config,state.Id),"result.json");
                        try
                        {
                            if(new FileInfo(result).Length>131072)throw new InvalidDataException("Oversized worker result.");
                            var report=Storage.Read<WorkerReport>(result);
                            if(report.RunId!=state.Id || report.Results.Count>32)throw new InvalidDataException("Mismatched worker result.");
                            state.Results.AddRange(report.Results.Select(r=>new ProbeResult(r.Stage[..Math.Min(r.Stage.Length,80)],r.Success,r.Detail[..Math.Min(r.Detail.Length,2000)],r.ElapsedMs,r.Informational)));
                        }
                        catch(Exception ex){state.Results.Add(new("Worker",false,"Worker exited without a valid report: "+ex.Message));}
                        await Recover("Test completed.");
                    }
                }
                finally{_gate.Release();}
            }
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        finally
        {
            resourceStop.Cancel();
            await _gate.WaitAsync();
            try{if(_store.State.Active)await Recover("Service stopping.");}finally{_gate.Release();}
            try{await listener;}catch(OperationCanceledException){}
            try{await resources;}catch(OperationCanceledException){}catch{}
            try{await startup;}catch(OperationCanceledException){}
            try{await native;}catch(OperationCanceledException){}
        }
    }
    async Task Listen(CancellationToken stop)
    {
        while(!stop.IsCancellationRequested)
        {
            var security=new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(_config.UserSid),PipeAccessRights.ReadWrite|PipeAccessRights.Synchronize,AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),PipeAccessRights.FullControl,AccessControlType.Allow));
            using var pipe=NamedPipeServerStreamAcl.Create(Program.PipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,security);
            await pipe.WaitForConnectionAsync(stop);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop);timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                string sid=PipeIdentity.Read(pipe.SafePipeHandle.DangerousGetHandle());
                if(sid!=_config.UserSid && sid!="S-1-5-18")throw new UnauthorizedAccessException("Wrong caller SID.");
                if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out uint pid))throw new UnauthorizedAccessException("Missing client PID.");
                using var process=Process.GetProcessById((int)pid);
                var request=JsonSerializer.Deserialize<AgentRequest>(await PipeMessages.Read(pipe,timeout.Token),Storage.Json)??throw new InvalidDataException("Empty request.");
                AgentReply reply;
                await _gate.WaitAsync(timeout.Token);
                try{reply=await Dispatch(request,process);}catch(Exception ex){reply=Reply(false,ex.Message);}finally{_gate.Release();}
                byte[] bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply,Storage.Wire)+"\n");
                await pipe.WriteAsync(bytes,timeout.Token);await pipe.FlushAsync(timeout.Token);
            }
            catch(Exception ex)when(ex is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException or ArgumentException or System.ComponentModel.Win32Exception){Log("IPC: "+ex.Message);}
        }
    }
    AgentReply Reply(bool success,string message)=>new(success,message,JsonSerializer.Deserialize<RunState>(JsonSerializer.Serialize(_store.State,Storage.Json),Storage.Json),_native?.Snapshot??(_nativeFailure!=null?JsonSerializer.SerializeToElement(new{phase="Unavailable",errorCode=_nativeFailure}):null));
    async Task<AgentReply> Dispatch(AgentRequest request,Process caller)
    {
        if(request.Command=="native-status")return Reply(_native!=null,"Native backend status.");
        if(request.Command is "native-start" or "native-stop")
        {
            if(_native==null)return Reply(false,"Protected native backend is not configured.");
            if(_store.State.Active||_operation?.Matches()==true)return Reply(false,"Another network operation owns the coordinator.");
            if(request.Command=="native-start"&&(Process.GetProcessesByName("smart-router").Length>0||Process.GetProcessesByName("vpn-bridge").Length>0))return Reply(false,"Disconnect the current legacy router before selecting the native backend.");
            _native.Request(request.Command=="native-start",request.RunId);return Reply(true,"Native operation accepted.");
        }
        if(request.Command=="status")return Reply(true,"Local rescue service available.");
        if(request.Command is "boot-stop" or "boot-disable")
        {
            if(request.RunId==null||!Path.GetFullPath(request.RunId).TrimEnd('\\').Equals(Path.GetFullPath(_config.AppRoot).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))return Reply(false,"This client does not own the startup route.");
            if(_boot==null)return Reply(false,"Startup controller is not available yet.");
            if(request.Command=="boot-disable")return Reply(_boot.Disable(_config.AppRoot),"Startup disable requested; an active startup route must be stopped first.");
            _boot.RequestStop();return Reply(true,"Startup disconnect requested.");
        }
        if(request.Command=="begin-operation")
        {
            if(_store.State.Active || _store.State.Phase==RunPhase.RecoveryFailed || _operation?.Matches()==true||_native?.Busy==true||_nativeFailure!=null)return Reply(false,"Another network operation owns the coordinator.");
            _operation=ProcessIdentity.From(caller);Storage.Write(Path.Combine(_config.DataDirectory,"operation.json"),_operation);return Reply(true,"Network operation reserved until release or caller exit.");
        }
        if(request.Command=="end-operation")
        {
            if(_operation!=null && _operation.Pid==caller.Id && _operation.Matches()){_operation=null;File.Delete(Path.Combine(_config.DataDirectory,"operation.json"));}
            return Reply(true,"Operation lease released.");
        }
        if(request.Command=="worker-hello")
        {
            var state=_store.State;
            bool valid=state.Phase==RunPhase.Testing && state.Id==request.RunId && state.Processes.FirstOrDefault()?.Pid==caller.Id;
            return Reply(valid,valid?"Worker lease verified.":"The worker does not own an active lease.");
        }
        if(request.Command=="vpnus-intent")
        {
            var state=_store.State;
            if(state.Phase!=RunPhase.Testing || state.Kind is not (TestKind.VpnusRestart or TestKind.SystemRouting) || state.Id!=request.RunId || state.Processes.FirstOrDefault()?.Pid!=caller.Id)return Reply(false,"No VPNUS restart lease.");
            state.VpnusTouched=true;_store.Save();return Reply(true,"VPNUS restart intent is recorded for independent recovery.");
        }
        if(request.Command=="stop-vpnus-service")
        {
            var state=_store.State;
            if(state.Phase!=RunPhase.Testing || state.Kind!=TestKind.SystemRouting || state.Id!=request.RunId || state.Processes.FirstOrDefault()?.Pid!=caller.Id || !state.VpnusTouched)return Reply(false,"No recorded system test lease.");
            bool down=VpnusServiceControl.StopWithDownProof();
            state.SystemIsolated=down;
            state.Results.Add(new("VpnusDownObserved",down,"The competing VPNUS tunnel must stay down during the system test."));_store.Save();
            return Reply(down,down?"VPNUS stopped for the bounded test.":"VPNUS did not stop.");
        }
        if(request.Command=="restart-vpnus-service")
        {
            var state=_store.State;
            if(state.Phase!=RunPhase.Testing || state.Kind!=TestKind.VpnusRestart || state.Id!=request.RunId || state.Processes.FirstOrDefault()?.Pid!=caller.Id || !state.VpnusTouched)return Reply(false,"No recorded VPNUS recovery lease.");
            bool down=VpnusServiceControl.RestartWithDownProof();
            state.Results.Add(new("VpnusDownObserved",down,down?"VPNUS adapter was observed down while its service was stopped.":"No real tunnel-down interval was proved."));_store.Save();
            return Reply(down,down?"VPNUS service restarted after a confirmed tunnel-down interval.":"A real disconnection was not confirmed; restart verification is incomplete.");
        }
        if(request.Command=="recover")
        {
            if(request.RunId!=_store.State.Id)return Reply(false,"A recovery command must name the current run.");
            if(!_store.State.Active && _store.State.Phase!=RunPhase.RecoveryFailed)return Reply(true,"No unfinished run.");
            _recoveryRequested="Local recovery requested.";
            return Reply(true,"Recovery requested.");
        }
        if(request.Command is "endpoint" or "bridge" or "router" or "stop-router" or "record-egress-proof")
        {
            var state=_store.State;
            if(state.Phase!=RunPhase.Testing || state.Kind is not (TestKind.WireSockProxy or TestKind.SystemRouting) || state.Id!=request.RunId || state.Processes.FirstOrDefault()?.Pid!=caller.Id)return Reply(false,"This command belongs to the current diagnostic worker only.");
            double remaining=_workSeconds-(_workTimer?.Elapsed.TotalSeconds??_workSeconds);
            if(remaining<=0){_recoveryRequested="Work deadline reached during an IPC operation.";return Reply(false,"Work budget expired.");}
            using var commandBudget=new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(15,remaining)));
            if(request.Command=="endpoint")
            {
                if(state.Route!=null)return Reply(false,"The endpoint route is already reserved.");
                state.Route=await Tools.PrepareEndpoint(_config,1000+(int)(state.Generation%20000),commandBudget.Token);
                state.PhysicalInterfaceIndex=state.Route.InterfaceIndex;state.NetworkSignature=Tools.NetworkSignature(state.PhysicalInterfaceIndex);_store.Save();
                await Tools.AddRoute(state.Route,commandBudget.Token);return Reply(true,"The endpoint-only route is active.");
            }
            if(state.Route==null)return Reply(false,"Endpoint route is not prepared.");
            if(request.Command=="record-egress-proof")
            {
                if(state.Processes.Count<2 || !state.Processes[1].Matches())return Reply(false,"No owned VPN bridge.");
                var trace=await NetworkProbe.Trace("socks5://127.0.0.1:20818",cancellation:commandBudget.Token);
                var physical=await NetworkProbe.Trace(bind:Tools.Physical(state.PhysicalInterfaceIndex),cancellation:commandBudget.Token);
                bool matched=trace.Ip.ToString()!=physical.Ip.ToString() && (string.IsNullOrEmpty(_config.ExpectedEgressIp) || trace.Ip.ToString()==_config.ExpectedEgressIp);
                if(matched)Storage.Write(Path.Combine(_config.DataDirectory,"egress-proof.json"),new{verifiedIp=trace.Ip.ToString(),endpoint=_config.EndpointIp,checkedUtc=DateTime.UtcNow,runId=state.Id});
                return Reply(matched,matched?"HTTPS through the owned WireSock bridge differs from the physical baseline.":"Expected VPN egress was not proved.");
            }
            if(request.Command=="stop-router")
            {
                if(state.Kind!=TestKind.SystemRouting)return Reply(false,"Not a system test.");
                foreach(var identity in state.Processes.Where(p=>p.Path.Equals(Tools.Router,StringComparison.OrdinalIgnoreCase)))Tools.Kill(identity);
                return Reply(true,"The test TUN is stopped.");
            }
            if(request.Command=="router")
            {
                if(state.Kind!=TestKind.SystemRouting || !state.VpnusTouched || NetworkProbe.Vpnus!=null || state.Processes.Count!=2 || !state.Processes[1].Matches())return Reply(false,"System test prerequisites are not satisfied.");
                string path=SystemRouter.Prepare(_config,state);
                var check=await Tools.Run(Tools.Router,new[]{"check","-c",path},cancellation:commandBudget.Token);
                if(check.Code!=0)throw new IOException("Diagnostic TUN configuration was rejected: "+check.Text);
                UserLaunch.StartSystemRouter(path,p=>{state.Processes.Add(p);_store.Save();});
                await Task.Delay(1500);
                await Tools.PowerShell("Clear-DnsClientCache",cancellation:commandBudget.Token);
                return Reply(state.Processes[^1].Matches(),"System test router started.");
            }
            if(state.Processes.Count!=1)return Reply(false,"Bridge already started.");
            string runFolder=Tools.RunFolder(_config,state.Id);
            string protectedConfig=Path.Combine(_config.DataDirectory,"bridge-"+state.Id+".json");
            string nic=Tools.Physical(state.PhysicalInterfaceIndex).Name;
            Storage.Write(protectedConfig,new{log=new{level="warn",output=Path.Combine(runFolder,"bridge.log")},
                dns=new{servers=new[]{new{type="https",tag="dns",server="1.1.1.1",detour="egress"}},final="dns",strategy="ipv4_only"},
                inbounds=new[]{new{type="mixed",tag="probe",listen="127.0.0.1",listen_port=20818}},
                outbounds=new[]{new{type="direct",tag="egress",bind_interface=nic}},route=new{final="egress",default_domain_resolver="dns"}});
            UserLaunch.Start(Tools.Bridge,new[]{"run","-c",protectedConfig,"--disable-color"},state.UserSessionId,_config.UserSid,runFolder,beforeResume:p=>{state.Processes.Add(p);_store.Save();});
            await Task.Delay(600);
            string owner=await Tools.PowerShell("@(Get-NetTCPConnection -State Listen -LocalPort 20818 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess) -join ','",cancellation:commandBudget.Token);
            if(owner.Trim()!=state.Processes[1].Pid.ToString())throw new IOException("The diagnostic listener belongs to a different process or failed to start.");
            return Reply(true,"Diagnostic bridge started.");
        }
        TestKind kind=request.Command switch{"dry-run" or "test-guard-restart"=>TestKind.DryRun,"test-vpnus"=>TestKind.VpnusRestart,"test-wiresock"=>TestKind.WireSockProxy,"test-system"=>TestKind.SystemRouting,_=>throw new InvalidOperationException("Unknown command.")};
        if(_native!=null||_nativeFailure!=null)return Reply(false,"Native backend owns this service configuration.");
        if(Cita.Startup.StartupPaths.Read() is {Active:true} boot&&boot.Owns(_config.AppRoot))return Reply(false,"Disconnect the startup route before independent diagnostics.");
        if(_store.State.Active || _store.State.Phase==RunPhase.RecoveryFailed)return Reply(false,"Another run is active or needs recovery.");
        if(_operation?.Matches()==true)return Reply(false,"A normal RoutePilot network operation is still running.");
        if(kind!=TestKind.DryRun && (!File.Exists(_proof) || (DateTime.UtcNow-File.GetLastWriteTimeUtc(_proof)).TotalHours>24))return Reply(false,"Run and pass the independent dry-run guard first.");
        if(kind!=TestKind.DryRun && NetworkProbe.Vpnus==null)return Reply(false,"VPNUS must be connected before diagnostics.");
        if(kind==TestKind.VpnusRestart && !NetworkProbe.AutoConnectEnabled(_config.UserProfile))return Reply(false,"VPNUS Connect on launch is disabled.");
        string vpnusProof=Path.Combine(_config.DataDirectory,"vpnus-proof.json");
        if(kind==TestKind.SystemRouting && (!File.Exists(vpnusProof) || (DateTime.UtcNow-File.GetLastWriteTimeUtc(vpnusProof)).TotalHours>24))return Reply(false,"First pass an actual VPNUS restart test.");
        if(caller.SessionId<=0)return Reply(false,"An interactive user session is required.");
        if(Process.GetProcessesByName("smart-router").Length>0 || Process.GetProcessesByName("vpn-bridge").Length>0)return Reply(false,"Stop the current RoutePilot engine before diagnostics.");
        if(kind is TestKind.WireSockProxy or TestKind.SystemRouting && System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e=>e.Port is 20818 or 20819 or 20820 or 20828 or 20829))return Reply(false,"A diagnostic port is occupied.");
        _store.Begin(kind,caller.SessionId,kind==TestKind.DryRun?5:90,60);
        var current=_store.State;current.VpnusInitiallyUp=NetworkProbe.Vpnus!=null;current.VpnusTouched=false;
        string folder=Tools.RunFolder(_config,current.Id);Directory.CreateDirectory(folder);
        var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(_config.UserSid),FileSystemRights.Modify,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(acl);
        _store.Save();
        try
        {
            UserLaunch.Start(Tools.Self,new[]{"--probe-worker",current.Id,kind.ToString()},caller.SessionId,_config.UserSid,folder,beforeResume:p=>{current.Processes.Add(p);_store.Save();});
            _store.Transition(current.Id,RunPhase.Testing,"Diagnostic worker running with a fixed deadline.");
            _workSeconds=kind==TestKind.DryRun?5:90;_workTimer=Stopwatch.StartNew();
            _crashDryRun=request.Command=="test-guard-restart";
            return Reply(true,"Run started. The rescue service remains independent of this client.");
        }
        catch{await Recover("Worker could not start.");throw;}
    }
    async Task Recover(string reason)
    {
        var state=_store.State;
        if(!_store.Transition(state.Id,RunPhase.Restoring,reason) && state.Phase!=RunPhase.Restoring)return;
        var failures=new List<string>();
        using var restoreBudget=new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var restoreTimer=Stopwatch.StartNew();
        // TUN must disappear before the bridge, and both before restarting VPNUS.
        foreach(var identity in state.Processes.AsEnumerable().Reverse())
        {try{Tools.Kill(identity);}catch(Exception ex){failures.Add("Process: "+ex.Message);}}
        if(state.Kind==TestKind.DryRun)
        {
            try{using var released=File.Open(Path.Combine(Tools.RunFolder(_config,state.Id),"worker-held.lock"),FileMode.Open,FileAccess.Read,FileShare.None);}
            catch(Exception ex){failures.Add("Worker lock was not released: "+ex.Message);}
        }
        if(state.Kind is TestKind.WireSockProxy or TestKind.SystemRouting)
        {
            try
            {
                var cleanup=UserLaunch.Start(Tools.Self,new[]{"--cleanup-worker",state.Id},state.UserSessionId,_config.UserSid,Tools.RunFolder(_config,state.Id));
                await WaitProcess(cleanup,12,restoreBudget.Token);
                string cleanupReport=Path.Combine(Tools.RunFolder(_config,state.Id),"cleanup.json");
                if(new FileInfo(cleanupReport).Length>131072)throw new InvalidDataException("Oversized cleanup report.");
                var result=Storage.Read<WorkerReport>(cleanupReport);
                if(result.RunId!=state.Id || result.Results.Any(r=>!r.Success))throw new IOException("WireSock cleanup was not confirmed.");
            }
            catch(Exception ex){failures.Add("WireSock cleanup: "+ex.Message);}
        }
        if(state.Route!=null){try{await Tools.RemoveRoute(state.Route,restoreBudget.Token);}catch(Exception ex){failures.Add("Route: "+ex.Message);}}
        if(state.Kind==TestKind.SystemRouting){try{await Tools.PowerShell("Clear-DnsClientCache",5,restoreBudget.Token);}catch(Exception ex){failures.Add("DNS cache: "+ex.Message);}}
        if(state.VpnusTouched)
        {
            try{int remaining=(int)(55-restoreTimer.Elapsed.TotalSeconds);if(remaining<1)throw new TimeoutException("Recovery budget expired.");VpnusServiceControl.EnsureRunning(Math.Min(8,remaining));}catch(Exception ex){failures.Add("VPNUS service: "+ex.Message);}
            bool online=await VpnusOnline(restoreBudget.Token);
            if(!online && !restoreBudget.IsCancellationRequested)
            {
                try
                {
                    if(!NetworkProbe.AutoConnectEnabled(_config.UserProfile))throw new InvalidOperationException("Connect on launch is no longer enabled.");
                    Tools.StopVpnusGui(state.UserSessionId);
                    UserLaunch.Start(Tools.VpnusExe,Array.Empty<string>(),state.UserSessionId,_config.UserSid,Path.GetDirectoryName(Tools.VpnusExe)!);
                    var deadline=Stopwatch.StartNew();
                    while(!restoreBudget.IsCancellationRequested && deadline.Elapsed.TotalSeconds<35 && !(online=await VpnusOnline(restoreBudget.Token)))await Task.Delay(1000,restoreBudget.Token);
                }
                catch(Exception ex){failures.Add("VPNUS restart: "+ex.Message);}
            }
            state.Results.Add(new("VpnusRecovery",online,online?"VPNUS IPv4 HTTPS access confirmed.":"VPNUS recovery requires local attention."));
            if(!online)failures.Add("No confirmed VPNUS access.");
        }
        bool restored=failures.Count==0;
        _store.Transition(state.Id,restored?RunPhase.Restored:RunPhase.RecoveryFailed,restored?"Cleanup and requested recovery completed.":string.Join("; ",failures));
        if(restored && state.Kind==TestKind.DryRun && state.Results.Any(r=>r.Stage=="IndependentGuard" && r.Success))Storage.Write(_proof,new{passedUtc=DateTime.UtcNow,version="0.2.2"});
        SaveVpnusProof();
        Log(state.Id+" "+state.Phase+" "+state.LastMessage);
    }
    void SaveVpnusProof()
    {
        var state=_store.State;
        if(state.Kind==TestKind.VpnusRestart && state.Phase==RunPhase.Restored && (DateTime.UtcNow-state.StartedUtc).TotalHours<24 && state.Results.Count>=4 && state.Results.All(r=>r.Success) && new[]{"VpnusDownObserved","VpnusRestart","VpnusRecovery"}.All(s=>state.Results.Any(r=>r.Stage==s && r.Success)))
            Storage.Write(Path.Combine(_config.DataDirectory,"vpnus-proof.json"),new{passedUtc=state.StartedUtc,runId=state.Id});
    }
    static async Task WaitProcess(ProcessIdentity identity,int seconds,CancellationToken cancellation=default)
    {
        using var process=Process.GetProcessById(identity.Pid);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try{await process.WaitForExitAsync(timeout.Token);}catch{Tools.Kill(identity);throw;}
    }
    static async Task<bool> VpnusOnline(CancellationToken cancellation)
    {try{var nic=NetworkProbe.Vpnus;if(nic==null)return false;await NetworkProbe.Trace(bind:nic,cancellation:cancellation);return true;}catch{return false;}}
    void Log(string text)=>File.AppendAllText(Path.Combine(_config.DataDirectory,"service.log"),DateTime.UtcNow.ToString("O")+" "+text+Environment.NewLine);
}
