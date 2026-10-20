using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RoutePilot;

public sealed class ConnectionProgress
{
    public string Id{get;set;}="";
    public string Phase{get;set;}="";
    public DateTime UpdatedUtc{get;set;}
    public static string Label(string phase)=>phase switch
    {
        "WaitingElevation"=>"Ожидаем разрешение Windows…",
        "ChoosingProfile"=>"Выбери профиль в открывшемся окне…",
        "ReleasingConflicts"=>"Останавливаем мешающее VPN-подключение…",
        "Preparing"=>"Подготавливаем подключение…",
        "Installing"=>"Устанавливаем защищённую службу…",
        "Connecting"=>"Подключаем выбранный профиль WireSock…",
        "StartingBridge"=>"Запускаем канал VPN…",
        "StartingRouter"=>"Запускаем маршрутизацию…",
        "ApplyingPolicy"=>"Применяем правила маршрутов…",
        "Verifying"=>"Проверяем соединение через VPN…",
        "Stopping"=>"Отключаем маршрут…",
        "UpdatingRules"=>"Обновляем списки маршрутов…",
        "Cancelling"=>"Отменяем подключение и завершаем очистку…",
        _=>"Выполняется операция…"
    };
}

public sealed class ConnectionResult
{
    public string Id{get;set;}="";
    public bool Success{get;set;}
    public bool Applied{get;set;}
    public bool Cancelled{get;set;}
    public bool Pending{get;set;}
    public string Message{get;set;}="";
}
public sealed record ConnectionSelection(string Cli,string Profile,string Egress);

// A single elevation covers first-time setup and Start. Child PowerShells
// isolate script `exit` statements from the durable operation report.
public static class ConnectionOperation
{
    static string Literal(string value)=>"'"+value.Replace("'","''")+"'";
    static string Encode(string value)=>Convert.ToBase64String(Encoding.Unicode.GetBytes(value));
    public static bool Active(string root)
    {
        string path=Path.Combine(root,"runtime","connection-operation.lock");
        if(!File.Exists(path))return false;
        try{using var held=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);return false;}
        catch(IOException){return true;}catch(UnauthorizedAccessException){return true;}
    }
    internal static string Command(string root,string id,string action,ConnectionSelection? setup,string sid,string user)
    {
        if(action is not ("Start" or "Stop" or "UpdateRules"))throw new ArgumentException("Unknown operation.");
        if(!Guid.TryParseExact(id,"N",out _))throw new ArgumentException("Invalid operation id.");
        string report=Path.Combine(root,"runtime","operation-"+id+".json");
        string error=Path.Combine(root,"runtime","operation-"+id+".error.json");
        string storage=Literal(Path.Combine(root,"FileStorage.ps1"));
        string Child(string script,string parameters)=>Encode("$ErrorActionPreference='Stop'; . "+storage+"; try{ & "+Literal(Path.Combine(root,script))+" "+parameters+
            " }catch{Write-JsonFile "+Literal(error)+" @{message=$_.Exception.Message};exit 1}");
        string steps="";
        if(setup!=null)
        {
            string parameters="-Action Apply -OperationId "+Literal(id)+" -CliPath "+Literal(setup.Cli)+" -SourceProfile "+Literal(setup.Profile)+
                " -ExpectedEgressIp "+Literal(setup.Egress)+" -UserSid "+Literal(sid)+" -UserProfile "+Literal(user);
            // Values stay inside PowerShell syntax; empty strings never cross -File.
            steps+="Step "+Child("Configure-Connection.ps1",parameters)+" 'setup'; $applied=$true; Assert-ConnectionOperation; ";
        }
        // Setup can need DNS/downloads through the previous connection. Keep it
        // available until checked preparation and protected installation finish.
        if(action=="Start")steps+="Step "+Child("Configure-Connection.ps1","-Action ReleaseConflicts -OperationId "+Literal(id))+" 'handoff'; ";
        if(action=="Start")steps+="$routeAttempted=$true; ";
        steps+="Step "+Child("SmartRouting.ps1","-Action "+action+" -OperationId "+Literal(id))+" 'route'; ";
        if(action=="Start")steps+="if(Test-ConnectionCancellation){Step "+Child("SmartRouting.ps1","-Action Stop")+" 'cleanup' $true; Assert-ConnectionOperation}; ";
        if(action=="Start")steps+="Step "+Child("Configure-Connection.ps1","-Action CompleteHandoff -OperationId "+Literal(id))+" 'handoff-complete'; ";
        string restore=action=="Start"?"if(Test-Path -LiteralPath (Join-Path $runtime 'connection-handoff.json')){try{Step "+Child("Configure-Connection.ps1","-Action RestoreHandoff -OperationId "+Literal(id))+" 'handoff-restore' $true}catch{$failure+=' Previous VPN restore needs attention: '+$_.Exception.Message}}; ":"";
        string cleanup=action=="Start"?"if($routeAttempted -and (Test-Path -LiteralPath (Join-Path $runtime 'session.json'))){try{$owned=Read-JsonFile (Join-Path $runtime 'session.json');if($owned.running -and $owned.operationId -eq $OperationId){Step "+Child("SmartRouting.ps1","-Action Stop")+" 'cleanup' $true}}catch{$failure+=' Route cleanup needs attention: '+$_.Exception.Message}}; ":"";
        string startGuard=action=="Start"?"$startGate=$null;try{$startGate=[IO.File]::Open((Join-Path $runtime 'update-preparation.lock'),'OpenOrCreate','ReadWrite','None')}catch{throw 'Update preparation is in progress.'}finally{if($startGate){$startGate.Dispose()}}; ":"";
        return "$ErrorActionPreference='Stop'; $root="+Literal(root)+"; $runtime=Join-Path $root 'runtime'; $OperationId="+Literal(id)+"; . "+storage+"; $applied=$false; $routeAttempted=$false; "+
            "function Step($encoded,$name,$cleanup=$false){if(-not $cleanup){Assert-ConnectionOperation}; $p=Start-Process powershell.exe -WindowStyle Hidden -PassThru -WorkingDirectory $root "+
            "-ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded) "+
            "-RedirectStandardOutput (Join-Path $root ('logs\\operation-'+$OperationId+'-'+$name+'.log')) "+
            "-RedirectStandardError (Join-Path $root ('logs\\operation-'+$OperationId+'-'+$name+'-error.log')); $null=$p.Handle; $p.WaitForExit(); $p.Refresh(); "+
            "if($p.ExitCode -ne 0){$m='Операция не завершена. Подробности есть в журнале.';if(Test-Path -LiteralPath "+Literal(error)+"){$m=(Read-JsonFile "+Literal(error)+").message};throw $m}}; "+
            "$operationLock=$null; try{$operationLock=[IO.File]::Open((Join-Path $runtime 'connection-operation.lock'),'OpenOrCreate','ReadWrite','None'); "+startGuard+"Assert-ConnectionOperation; "+steps+"Write-JsonFile "+Literal(report)+" @{id=$OperationId;success=$true;applied=$applied;cancelled=$false;message=''}}"+
            "catch{$failure=$_.Exception.Message; "+cleanup+restore+"Write-JsonFile "+Literal(report)+" @{id=$OperationId;success=$false;applied=$applied;cancelled=(Test-ConnectionCancellation);message=$failure};exit 1}finally{if($operationLock){$operationLock.Dispose()}}";
    }
    public static async Task<ConnectionResult> Run(string root,string action,bool elevated,ConnectionSelection? setup=null,IProgress<ConnectionProgress>? progress=null,CancellationToken cancellation=default,TimeSpan? timeout=null)
    {
        var startup=Cita.Startup.StartupPaths.Read();
        if(startup.Active&&startup.Owns(root))
        {
            if(action!="Stop")return new(){Message="Подключением до входа управляет служба. Сначала отключи маршрут; затем можно сменить профиль или подключиться обычным способом."};
            var reply=await new RescueClient(root).Command("boot-stop",root);
            if(!reply.Success)return new(){Message=reply.Message};
            progress?.Report(new(){Phase="Stopping",UpdatedUtc=DateTime.UtcNow});
            using var wait=CancellationTokenSource.CreateLinkedTokenSource(cancellation);wait.CancelAfter(TimeSpan.FromSeconds(145));
            while(true)
            {
                wait.Token.ThrowIfCancellationRequested();
                var state=Cita.Startup.StartupPaths.Read();
                if(!state.Owns(root))return new(){Message="Принадлежность автоподключения изменилась. Проверь состояние службы."};
                if(state.Phase=="StopFailed")return new(){Message=state.Message};
                if(!state.Active)return new(){Success=state.Phase=="StoppedForBoot",Message=state.Message};
                await Task.Delay(200,wait.Token);
            }
        }
        Directory.CreateDirectory(Path.Combine(root,"runtime"));Directory.CreateDirectory(Path.Combine(root,"logs"));
        if(Active(root))return new(){Message="Другая операция подключения ещё выполняется. Подробности — в журнале."};
        // Keep recent operation diagnostics without accumulating files forever.
        try
        {
            foreach(var old in new DirectoryInfo(Path.Combine(root,"logs")).GetFiles("operation-*.log").OrderByDescending(f=>f.LastWriteTimeUtc).Skip(32))
                if(old.LastWriteTimeUtc<DateTime.UtcNow.AddHours(-1))old.Delete();
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
        string id=Guid.NewGuid().ToString("N"),prefix=Path.Combine(root,"runtime","operation-"+id);
        string sid=WindowsIdentity.GetCurrent().User!.Value,user=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string command=Command(root,id,action,setup,sid,user);
        using var deadline=new CancellationTokenSource(timeout??TimeSpan.FromMinutes(6));
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancellation,deadline.Token);
        using var registration=lifetime.Token.Register(()=>{try{File.WriteAllText(prefix+".cancel",id);}catch(IOException){}catch(UnauthorizedAccessException){}});
        cancellation.ThrowIfCancellationRequested();
        progress?.Report(new(){Id=id,Phase=elevated?"WaitingElevation":action=="Stop"?"Stopping":"UpdatingRules",UpdatedUtc=DateTime.UtcNow});
        // Keep the outer transaction out of the Windows command-line length limit.
        // Child commands remain separately encoded; this generated file is local
        // operation metadata and is never included in a release archive.
        string transaction=prefix+".ps1";
        File.WriteAllText(transaction,command,new UTF8Encoding(true));
        var info=new ProcessStartInfo("powershell.exe"){WorkingDirectory=root,UseShellExecute=elevated,CreateNoWindow=!elevated,WindowStyle=ProcessWindowStyle.Hidden,
        };
        foreach(string argument in new[]{"-NoProfile","-ExecutionPolicy","Bypass","-File",transaction})info.ArgumentList.Add(argument);
        if(elevated)info.Verb="runas";
        // ShellExecute can wait on the secure-desktop UAC prompt. Keep WPF live.
        async Task<Process> Launch()
        {
            try{return await Task.Run(()=>Process.Start(info)??throw new IOException("Не удалось запустить управление подключением."));}
            catch{try{File.Delete(transaction);}catch(IOException){}catch(UnauthorizedAccessException){}throw;}
        }
        using var process=await Launch();
        DateTime last=DateTime.MinValue,cancelAt=DateTime.MaxValue;bool cancelReported=false,pending=false;
        try
        {
            while(!process.HasExited)
            {
                if(lifetime.IsCancellationRequested && !cancelReported){cancelReported=true;cancelAt=DateTime.UtcNow;progress?.Report(new(){Id=id,Phase="Cancelling",UpdatedUtc=DateTime.UtcNow});}
                if(cancelReported&&(DateTime.UtcNow-cancelAt).TotalSeconds>30)
                {
                    pending=true;
                    return new(){Id=id,Pending=true,Message="Завершение операции задержалось. Новое подключение заблокировано до окончания очистки; открой журнал."};
                }
                var state=AppPaths.Read(prefix+".progress.json",()=>new ConnectionProgress());
                if(!cancelReported && state.Id==id && state.UpdatedUtc>last && SessionHealthCache.Fresh(state.UpdatedUtc,DateTime.UtcNow,300))
                {last=state.UpdatedUtc;progress?.Report(state);}
                await Task.Delay(150);
            }
            var result=AppPaths.Read(prefix+".json",()=>new ConnectionResult());
            if(result.Id!=id)return new(){Id=id,Message="Нет подтверждения завершения операции. Открой журнал в расширенном режиме."};
            result.Message=Backend.Redact(result.Message);
            try{AppPaths.Write(Path.Combine(root,"logs","last-operation.json"),new{id,action,result.Success,result.Cancelled,result.Applied,updatedUtc=DateTime.UtcNow});}catch(IOException){}catch(UnauthorizedAccessException){}
            return result;
        }
        finally
        {
            // Never kill an elevated transaction midway. Cancellation is checked
            // by its owner at safe checkpoints; Start performs its own cleanup.
            if(!pending)foreach(string suffix in new[]{".json",".progress.json",".error.json",".cancel",".ps1"})try{File.Delete(prefix+suffix);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
}
