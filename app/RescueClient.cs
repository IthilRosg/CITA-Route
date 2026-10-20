using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RoutePilot;

public sealed class RescueProbe
{
    public string Stage{get;set;}="";public bool Success{get;set;}public bool Informational{get;set;}public string Detail{get;set;}="";public long ElapsedMs{get;set;}
    public string Label=>Stage switch{
        "IndependentGuard"=>"Независимая защита","VpnusBaseline"=>"Исходный VPNUS","VpnusDownObserved"=>"Отключение VPNUS", "PhysicalBaseline"=>"Прямой доступ",
        "WireSockState"=>"Состояние WireSock","WireSockEgress"=>"Выход AWG2","TunVpnEgress"=>"VPN через системный маршрут","TunDirectEgress"=>"Прямой системный маршрут",
        "DpiPhysicalEgress"=>"Физический выход DPI","Candidate"=>"Проба кандидата","AdaptiveSelection"=>"Автоматический выбор","ServiceAccess"=>"Доступность сервиса",
        "OpenAIProxy"=>"OpenAI через VPN","VpnusRecovery"=>"Возврат VPNUS","VpnusRestart"=>"Автоподключение VPNUS","Failure"=>"Ошибка",_=>Stage};
}
public sealed class RescueState
{
    public string Id{get;set;}="";public string Phase{get;set;}="Idle";public string Kind{get;set;}="";
    public string LastMessage{get;set;}="";public DateTime WorkDeadlineUtc{get;set;}public DateTime ReturnDeadlineUtc{get;set;}
    public DateTime StartedUtc{get;set;}
    public string TestName=>Kind switch{"DryRun"=>"Защита","VpnusRestart"=>"Перезапуск VPNUS","WireSockProxy"=>"WireSock","SystemRouting"=>"Системный маршрут",_=>"Диагностика"};
    public List<RescueProbe> Results{get;set;}=new();
    public bool Active=>Phase is "Armed" or "Testing" or "Restoring";
    public bool BlocksRouting=>Active || Phase=="RecoveryFailed";
    public bool Passed=>Phase=="Restored" && Results.Count>0 && Results.All(r=>r.Success||r.Informational);
}
public sealed class RescueReply { public bool Success{get;set;}public string Message{get;set;}="";public RescueState? State{get;set;} }
public sealed class RescueClient
{
    readonly string _root;
    public RescueClient(string root)=>_root=root;
    public static string InstalledExe=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RoutePilot","Agent","RoutePilot.Agent.exe");
    public bool Installed=>File.Exists(InstalledExe);
    public RescueState ReadState()
    {
        string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RoutePilot","run.json");
        if(!File.Exists(path))return new();
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        return JsonSerializer.Deserialize<RescueState>(stream,AppPaths.Json)??new();
    }
    public async Task<RescueReply> Command(string command,string? id=null)
    {
        if(command is not ("status" or "dry-run" or "test-vpnus" or "test-wiresock" or "test-system" or "recover" or "boot-stop" or "boot-disable"))throw new ArgumentException("Unknown rescue command.");
        using var pipe=new NamedPipeClientStream(".","RoutePilot.Rescue.v1",PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));await pipe.ConnectAsync(timeout.Token);
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{Command=command,RunId=id})+"\n"),timeout.Token);await pipe.FlushAsync(timeout.Token);
        using var bytes=new MemoryStream();byte[] one=new byte[1];
        while(bytes.Length<32768)
        {
            if(await pipe.ReadAsync(one,timeout.Token)==0)throw new IOException("Служба закрыла соединение.");
            if(one[0]==10)return JsonSerializer.Deserialize<RescueReply>(Encoding.UTF8.GetString(bytes.ToArray()),AppPaths.Json)??throw new IOException("Пустой ответ службы.");
            bytes.WriteByte(one[0]);
        }
        throw new IOException("Слишком большой ответ службы.");
    }
    public async Task Install(bool startup=false)
    {
        var start=new ProcessStartInfo("powershell.exe"){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=_root};
        start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-ExecutionPolicy");start.ArgumentList.Add("Bypass");start.ArgumentList.Add("-File");start.ArgumentList.Add(Path.Combine(_root,"Install-Agent.ps1"));
        start.ArgumentList.Add("-UserSid");start.ArgumentList.Add(WindowsIdentity.GetCurrent().User!.Value);
        start.ArgumentList.Add("-UserProfile");start.ArgumentList.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if(startup){start.ArgumentList.Add("-BootStartup");start.ArgumentList.Add("Enable");}
        using var process=await Task.Run(()=>Process.Start(start)??throw new IOException("Не удалось запустить установку."));await process.WaitForExitAsync();
        if(process.ExitCode!=0)throw new IOException("Установка службы не завершена. Подробности: logs/agent-install.log");
    }
    public static string Friendly(string message)
    {
        if(message.Contains("dry-run",StringComparison.OrdinalIgnoreCase))return "Сначала выполни проверку защиты: она не отключает VPNUS.";
        if(message.Contains("auto-connect",StringComparison.OrdinalIgnoreCase))return "В VPNUS должна быть включена настройка «Подключаться при запуске».";
        if(message.Contains("Stop the current"))return "Перед тестом останови текущий маршрут RoutePilot.";
        if(message.Contains("needs recovery"))return "Сначала заверши восстановление предыдущего теста.";
        return Backend.Redact(message);
    }
}
