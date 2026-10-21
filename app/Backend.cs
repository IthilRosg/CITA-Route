using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RoutePilot;

public sealed class Backend : IDisposable
{
    private readonly string _root;
    private readonly HttpClient _http=new(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromMilliseconds(950)};
    public string CliPath{get;set;}=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"WireSock Secure Connect","command-line","wiresock-connect-cli.exe");
    public Backend(string root){_root=root;Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);}
    public bool EngineRunning(EngineSession session)
    {
        if(!session.Running || session.RouterPid<=0)return false;
        try
        {
            using var process=Process.GetProcessById(session.RouterPid);
            if(process.HasExited || process.ProcessName!="smart-router")return false;
            if(session.RouterStartedUtc!=DateTime.MinValue && Math.Abs((process.StartTime.ToUniversalTime()-session.RouterStartedUtc.ToUniversalTime()).TotalMilliseconds)>=100)return false;
            using var handle=OpenProcess(0x1000,false,process.Id);
            var path=new StringBuilder(32768);int size=path.Capacity;
            return !handle.IsInvalid && QueryFullProcessImageName(handle,0,path,ref size) &&
                string.Equals(path.ToString(),Path.Combine(Cita.Startup.StartupPaths.RoutingRoot(_root),"runtime","smart-router.exe"),StringComparison.OrdinalIgnoreCase);
        }
        catch(Exception ex)when(ex is ArgumentException or InvalidOperationException or Win32Exception){return false;}
    }
    [DllImport("kernel32.dll",SetLastError=true)]
    static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int processId);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageName(SafeProcessHandle handle,int flags,StringBuilder path,ref int size);
    private HttpRequestMessage Request(HttpMethod method,string endpoint)
    {
        var message=new HttpRequestMessage(method,"http://127.0.0.1:20810/"+endpoint);
        string token=File.ReadAllText(Path.Combine(_root,"private","controller-token.txt")).Trim();
        message.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        return message;
    }
    public async Task<Snapshot> SnapshotAsync()
    {
        using var request=Request(HttpMethod.Get,"connections");using var response=await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();return Snapshot.Parse(await response.Content.ReadAsStringAsync());
    }
    public async Task<(bool Verified,string Message)> VerifyVpnEgressAsync(CancellationToken cancellation=default)
    {
        try
        {
            if(RoutePilot.Agent.NetworkProbe.Vpnus!=null)return(false,"Обнаружен VPNUS: независимый выход WireSock не подтверждён.");
            return await RoutePilot.Agent.NetworkProbe.CheckEgress(cancellation);
        }
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested){throw;}
        catch(Exception ex){return(false,"Не подтверждён VPN-выход: "+ex.Message);}
    }
    public async Task CloseConnectionAsync(string id)
    {
        if(!Guid.TryParse(id,out _))throw new ArgumentException("Некорректный ID соединения.");
        using var request=Request(HttpMethod.Delete,"connections/"+id);using var response=await _http.SendAsync(request);response.EnsureSuccessStatusCode();
    }
    public async Task<(int Code,string Output)> RunAsync(string exe,string[] args,int timeout=10000)
    {
        var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=_root,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(string arg in args)info.ArgumentList.Add(arg);
        using var process=Process.Start(info)??throw new InvalidOperationException("Не удалось запустить процесс.");
        using var outputBytes=new MemoryStream();using var errorBytes=new MemoryStream();
        Task output=process.StandardOutput.BaseStream.CopyToAsync(outputBytes),error=process.StandardError.BaseStream.CopyToAsync(errorBytes);
        using var cancel=new CancellationTokenSource(timeout);
        try{await process.WaitForExitAsync(cancel.Token);await Task.WhenAll(output,error).WaitAsync(cancel.Token);}
        catch(OperationCanceledException){try{process.Kill(true);}catch{}throw new TimeoutException("Истекло время ожидания ответа.");}
        string Decode(byte[] bytes)
        {
            try{return new UTF8Encoding(false,true).GetString(bytes);}
            catch(DecoderFallbackException){return Encoding.GetEncoding(866).GetString(bytes);}
        }
        return(process.ExitCode,Redact(Decode(outputBytes.ToArray())+Environment.NewLine+Decode(errorBytes.ToArray())));
    }
    public async Task<string> WireSockStatusAsync()
    {
        if(!File.Exists(CliPath))return "WireSock не установлен";
        var result=await RunAsync(CliPath,new[]{"status"},4000);
        return result.Output.Trim();
    }
    public async Task<(bool Success,string Message)> ActionAsync(string action,bool elevated)
    {
        var result=await ConnectionOperation.Run(_root,action,elevated);
        return(result.Success,result.Message);
    }
    public async Task<string> CheckDomainAsync(string input)
    {
        string domain=DomainRules.Normalize(input);
        foreach(var rule in DomainRules.Read(Path.Combine(_root,"force-direct.txt")))if(DomainRules.Matches(domain,rule))return "Напрямую · твоё исключение";
        foreach(var rule in DomainRules.Read(Path.Combine(_root,"force-vpn.txt")))if(DomainRules.Matches(domain,rule))return "VPN · твоё правило";
        foreach(string file in new[]{"russia_inside.srs","discord.srs"})
        {
            var result=await RunAsync(Path.Combine(_root,"runtime","smart-router.exe"),new[]{"rule-set","match",Path.Combine(_root,"runtime","rules",file),domain,"-f","binary"});
            if(result.Code!=0)throw new InvalidOperationException("Не удалось проверить список правил.");
            if(result.Output.Contains("match rules",StringComparison.OrdinalIgnoreCase))return "VPN · "+(file.StartsWith("discord")?"список Discord":"список ограничений");
        }
        return "Напрямую · правило по умолчанию";
    }
    public static string Redact(string text)=>Regex.Replace(text,@"[A-Za-z0-9+/]{43}=","[ключ скрыт]");
    public static string FriendlyHealth(string code,bool healthy)=>healthy?"Соединение через выбранный VPN проверено.":code switch
    {
        "EgressMismatch"=>"Выход VPN отличается от профиля. Проверь подключение.",
        "ProfileChanged" or "ProfileNotConnected"=>"Выбранный профиль WireSock не подтверждён.",
        "MissingExpectedEgress"=>"Укажи IP выхода VPN в расширенных настройках подключения.",
        "InvalidConfiguration"=>"Требуется настройка подключения.",
        _=>"Проверяем доступность соединения. Маршрут целиком не перезапускается."
    };
    public string ReadLog()
    {
        var result=new StringBuilder();
        try
        {
            var state=new RescueClient(_root).ReadState();
            if(!string.IsNullOrEmpty(state.Id))
            {
                result.AppendLine("── Автономная диагностика · "+state.TestName+" · "+state.StartedUtc.ToLocalTime().ToString("dd.MM HH:mm:ss")+" · "+state.Phase+" ──");
                foreach(var probe in state.Results)result.AppendLine((probe.Informational?"· ":probe.Success?"✓ ":"✕ ")+probe.Label+": "+Redact(probe.Detail));
                result.AppendLine();
            }
        }catch(IOException){}catch(JsonException){}
        var files=new[]{"diagnostics.log","last-action.log","connection-setup.log","connection-lost.txt","recovery.log","recovery-error.log","cleanup-error.log","router.log","bridge.log","smart-router-startup.log","feeds-startup.log"};
        var operation=AppPaths.Read(Path.Combine(_root,"logs","last-operation.json"),()=>new ConnectionResult());
        if(Guid.TryParseExact(operation.Id,"N",out _))
            files=Directory.GetFiles(Path.Combine(_root,"logs"),"operation-"+operation.Id+"-*.log").Select(p=>Path.GetFileName(p)).Concat(files).ToArray();
        foreach(string file in files)
        {
            string path=Path.Combine(_root,"logs",file);if(!File.Exists(path))continue;
            result.AppendLine("── "+file+" · "+File.GetLastWriteTime(path).ToString("dd.MM HH:mm:ss")+" ──");
            try
            {
                using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
                if(stream.Length>16000)stream.Seek(-16000,SeekOrigin.End);
                using var reader=new StreamReader(stream,Encoding.UTF8,true);result.AppendLine(Redact(reader.ReadToEnd()));
            }catch(IOException){result.AppendLine("Журнал временно занят.");}
        }
        return result.Length==0?"Журнал пуст. Подключись, чтобы увидеть события.":result.ToString();
    }
    public static string FriendlyError(string message)
    {
        if(message.Contains("Update preparation is in progress"))return "CITA Route готовит обновление. Подключение будет доступно после установки.";
        if(message.Contains("virtual routing adapter is still in use"))return "Виртуальный адаптер занят или ещё отключается. Подожди и повтори подключение.";
        if(message.Contains("Helper exited before its network interface") || message.Contains("helper did not finish network"))return "Сетевой компонент не смог завершить запуск. Подробности — в журнале.";
        if(message.Contains("routing engine exited during policy setup"))return "Маршрутизатор остановился до применения правил. Подробности — в журнале.";
        if(message.Contains("Previous VPN restore needs attention"))return "Подключение не завершено; прежний VPN не удалось вернуть автоматически. Открой журнал и проверь прежнее подключение.";
        if(message.Contains("VPNUS reconnected") || message.Contains("VPNUS connection has no supported") || message.Contains("VPNUS service identity"))return "Не удалось безопасно остановить VPNUS автоматически. Проверь его клиент и повтори подключение.";
        if(message.Contains("competing WireSock connection did not stop"))return "WireSock не подтвердил отключение прежнего подключения. Проверь его состояние и повтори попытку.";
        if(message.Contains("VPNUS"))return "Сначала отключи временный VPNUS. Затем повтори подключение.";
        if(message.Contains("handshake"))return "AWG2-сервер не ответил за 35 секунд. Проверь доступность исходного профиля WireSock.";
        if(message.Contains("Override profile"))return "В WireSock отключи замену правил профиля глобальными настройками.";
        if(message.Contains("transparent mode"))return "В WireSock отключи Virtual Adapter Mode.";
        if(message.Contains("already running"))return "Маршрутизатор уже запущен. Сначала нажми «Отключить».";
        if(message.Contains("Another network operation"))return "Другая сетевая операция ещё выполняется. Дождись её завершения.";
        if(message.Contains("Another WireSock profile"))return "В WireSock подключён другой профиль. Сначала отключи его в WireSock, затем подключи CITA Route.";
        if(message.Contains("No active physical"))return "Нет активного подключения Wi-Fi или Ethernet.";
        if(message.Contains("CLI was not found"))return "WireSock не найден. Выбери его в окне подключения.";
        if(message.Contains("WireSock state could not be verified"))return "Статус WireSock пока не подтверждён. Подожди или проверь его службу.";
        if(message.Contains("Stop the current RoutePilot engine"))return "Другая копия CITA Route управляет сетью. Сначала отключи маршрут в ней.";
        if(message.Contains("Disconnect RoutePilot before"))return "Перед настройкой отключи действующий маршрут CITA Route. Подробности — в журнале.";
        if(message.Contains("Select an existing WireSock"))return "Профиль больше не доступен. Выбери другой профиль подключения.";
        if(message.Contains("EgressMismatch"))return "Выход VPN отличается от профиля. Проверь IP выхода в расширенных настройках.";
        if(message.Contains("MissingExpectedEgress"))return "WireSock не сообщил IP выхода VPN. Укажи его в расширенных настройках подключения.";
        if(message.Contains("ProfileNotConnected") || message.Contains("ProfileChanged"))return "WireSock не подтвердил выбранный профиль во время проверки. Повтори подключение.";
        if(message.Contains("StatusUnavailable"))return "Не удалось прочитать состояние WireSock. Проверь его службу и повтори подключение.";
        if(message.Contains("data path did not pass"))return "VPN не прошёл проверку HTTPS. Проверь профиль и доступность сервера.";
        if(message.Contains("Connection operation cancelled"))return "Подключение отменено.";
        return string.IsNullOrWhiteSpace(message)?"Операция не завершена. Открой журнал.":message;
    }
    public void Dispose()=>_http.Dispose();
}
