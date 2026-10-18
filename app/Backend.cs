using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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
            try{return string.Equals(process.MainModule?.FileName,Path.Combine(_root,"runtime","smart-router.exe"),StringComparison.OrdinalIgnoreCase);}
            catch(Win32Exception){return Math.Abs((process.StartTime.ToUniversalTime()-session.StartedUtc.ToUniversalTime()).TotalSeconds)<15;}
        }
        catch(Exception ex)when(ex is ArgumentException or InvalidOperationException or Win32Exception){return false;}
    }
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
    public async Task<(bool Verified,string Message)> VerifyVpnEgressAsync()
    {
        try
        {
            if(RoutePilot.Agent.NetworkProbe.Vpnus!=null)return(false,"Обнаружен VPNUS: независимый выход WireSock не подтверждён.");
            return await RoutePilot.Agent.NetworkProbe.CheckEgress();
        }
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
        try{await process.WaitForExitAsync(cancel.Token);}catch(OperationCanceledException){try{process.Kill(true);}catch{}throw new TimeoutException("Истекло время ожидания ответа.");}
        await Task.WhenAll(output,error);
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
        if(action is not ("Start" or "Stop" or "UpdateRules"))throw new ArgumentException("Unknown action");
        string script=Path.Combine(_root,"SmartRouting.ps1").Replace("'","''");
        string state=Path.Combine(_root,"runtime","operation.json").Replace("'","''");
        string log=Path.Combine(_root,"logs","last-action.log").Replace("'","''");
        string operation=Guid.NewGuid().ToString("N");
        // All interpolated values are local paths escaped as PowerShell literals or closed action names.
        string storage=Path.Combine(_root,"FileStorage.ps1").Replace("'","''");
        string command="$ErrorActionPreference='Stop'; . '"+storage+"'; "+
            "function Report($s,$m){Write-JsonFile '"+state+"' @{id='"+operation+"';state=$s;message=$m}}; "+
            "Report 'running' '';try{ & '"+script+"' -Action "+action+" *>&1 | Out-File -LiteralPath '"+log+"' -Encoding utf8;Report 'complete' '';exit 0}catch{Report 'failed' $_.Exception.Message;exit 1}";
        var info=new ProcessStartInfo("powershell.exe"){WorkingDirectory=_root,UseShellExecute=elevated,CreateNoWindow=!elevated,WindowStyle=ProcessWindowStyle.Hidden};
        if(elevated)info.Verb="runas";
        info.Arguments="-NoProfile -ExecutionPolicy Bypass -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        using var process=Process.Start(info)??throw new InvalidOperationException("Не удалось запустить управление сетью.");
        using var cancellation=new CancellationTokenSource(90000);
        await process.WaitForExitAsync(cancellation.Token);
        try
        {
            using var report=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root,"runtime","operation.json")));
            if(report.RootElement.GetProperty("id").GetString()==operation)
                return(report.RootElement.GetProperty("state").GetString()=="complete",Redact(report.RootElement.GetProperty("message").GetString()??""));
        }catch{}
        return(false,"Операция не завершена. Подробности есть в журнале.");
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
        foreach(string file in new[]{"diagnostics.log","last-action.log","connection-lost.txt","recovery.log","recovery-error.log","cleanup-error.log","router.log","bridge.log","smart-router-startup.log","feeds-startup.log"})
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
        if(message.Contains("VPNUS"))return "Сначала отключи временный VPNUS. Затем повтори подключение.";
        if(message.Contains("handshake"))return "AWG2-сервер не ответил за 35 секунд. Проверь доступность исходного профиля WireSock.";
        if(message.Contains("Override profile"))return "В WireSock отключи замену правил профиля глобальными настройками.";
        if(message.Contains("transparent mode"))return "В WireSock отключи Virtual Adapter Mode.";
        if(message.Contains("already running"))return "Маршрутизатор уже запущен. Сначала нажми «Отключить».";
        if(message.Contains("Another network operation"))return "Другая сетевая операция ещё выполняется. Дождись её завершения.";
        if(message.Contains("Another WireSock profile"))return "В WireSock подключён другой профиль. Сначала отключи его в WireSock, затем подключи RoutePilot.";
        if(message.Contains("No active physical"))return "Нет активного подключения Wi-Fi или Ethernet.";
        return string.IsNullOrWhiteSpace(message)?"Операция не завершена. Открой журнал.":message;
    }
    public void Dispose()=>_http.Dispose();
}
