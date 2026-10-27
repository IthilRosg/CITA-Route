using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RoutePilot;

public sealed class NativeRuntimeView
{
    public string Phase{get;set;}="";public string CoreState{get;set;}="";public string Ipv6Mode{get;set;}="";
}
public sealed class NativeRouteView
{
    public string Phase{get;set;}="";public bool Desired{get;set;}public string Operation{get;set;}="";
    public string ProfileName{get;set;}="Подготовленный профиль";public string ProfileId{get;set;}="";
    public string PolicyMode{get;set;}="SelectedRoutes";public string? ErrorCode{get;set;}public string? ImportStage{get;set;}public NativeRuntimeView? Runtime{get;set;}
    public bool Connected=>Phase=="Connected"&&Runtime?.CoreState=="Active";
    public bool Available=>Phase.Length>0&&Phase!="Unavailable";
}
public sealed class NativeControlReply{public bool Success{get;set;}public string Message{get;set;}="";public NativeRouteView? Native{get;set;}}
public static class NativeRoutePresentation
{
    public static string Title(NativeRouteView? s)=>s==null?"Служба недоступна":s.Phase switch{
        "Connected"=>s.Connected?"Подключено":"Проверяем подключение",
        "Importing"=>"Проверяем профиль","Preparing"=>"Подготавливаем подключение","Recovering" or "TransportDegraded" or "TransportUnavailable" or "TransportExhausted"=>"Восстанавливаем VPN",
        "Stopping"=>"Отключаем VPN","Unconfigured"=>"Выбери профиль","Disconnected"=>"Отключено",_=>"Требуется внимание"};
    public static string Detail(NativeRouteView? s)=>s==null?"Не удалось связаться со службой. Повтори проверку.":s.Phase switch{
        "Importing"=>s.ImportStage switch{"ReadingProfile"=>"Читаем профиль и определяем адрес сервера…","ValidatingProfile"=>"Проверяем параметры профиля…","ProbingHttps"=>"Проверяем HTTPS через сервер…","RetryingMtu"=>"Проверяем подключение с меньшим размером пакетов…","ProbingUdp"=>"Проверяем передачу UDP через сервер…","CompilingPolicy"=>"Подготавливаем правила маршрутизации…","WritingSnapshot"=>"Сохраняем защищённый профиль…",_=>"Проверяем конфиг и доступ через сервер…"},
        "Unconfigured"=>"Компоненты готовы. Импортируй свой профиль для подключения.",
        "Preparing"=>s.Runtime?.Phase switch{"CompilingPolicy"=>"Подготавливаем правила маршрутов…","ArmingGuard"=>"Включаем защиту соединения…","StartingCore"=>"Настраиваем сетевой адаптер…","Connecting"=>"Проверяем подключение через сервер…",_=>"Подготавливаем профиль…"},
        "Connected"=>s.PolicyMode=="FullVpn"?"VPN подключён. Проверенные HTTPS-домены могут идти напрямую.":"VPN подключён для адресов профиля. Проверенные HTTPS-домены могут идти напрямую.",
        "Recovering" or "TransportDegraded" or "TransportUnavailable" or "TransportExhausted"=>"Восстанавливаем соединение. Защищённые адреса не переключаются напрямую.",
        "RecoveryFailed"=>s.ErrorCode switch{"NativeHttpsUnavailable"=>"Не удалось проверить HTTPS через VPN. Проверь сервер и повтори подключение.","NativeEgressMismatch"=>"Выход VPN изменился; подключение остановлено до проверки сервера.","NativeUdpUnavailable"=>"Через VPN не прошла проверка UDP. Звонки и игры могут быть недоступны; проверь сервер.",_=>"Соединение требует внимания. Проверь журнал перед повторным подключением."},
        "Stopping"=>"Завершаем соединения и освобождаем адаптер…","Disconnected"=>"Готово к подключению",
        "Unavailable"=>"Компоненты встроенного режима требуют настройки.",_=>"Соединение требует внимания. Проверь журнал перед повторным подключением."};
}
public sealed class NativeRouteClient
{
    public async Task<NativeControlReply> Command(string command,string? operation=null,CancellationToken cancellation=default)
    {
        if(command is not ("status" or "native-start" or "native-stop" or "native-capabilities"))throw new ArgumentException("Native command scope.");
        return await Exchange(new{Command=command,RunId=operation},cancellation);
    }
    public Task<NativeControlReply> Import(string base64,string name,string operation,CancellationToken cancellation,string requestId="")
    {
        if(base64.Length>16384||name.Length>160)throw new ArgumentException("Native import size.");
        return Exchange(new{Command="native-import",RunId=operation,Profile=new{Base64=base64,Name=name,RequestId=requestId}},cancellation);
    }
    async Task<NativeControlReply> Exchange(object payload,CancellationToken cancellation)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var pipe=new NamedPipeClientStream(".","RoutePilot.Rescue.v1",PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);await pipe.ConnectAsync(timeout.Token);
        byte[] request=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)+"\n");if(request.Length>=32768)throw new IOException("Конфиг слишком большой для импорта.");try{await pipe.WriteAsync(request,timeout.Token);await pipe.FlushAsync(timeout.Token);}finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(request);}
        using var bytes=new MemoryStream();byte[] one=new byte[1];while(bytes.Length<32768){if(await pipe.ReadAsync(one,timeout.Token)==0)throw new IOException("Служба закрыла соединение.");if(one[0]==10)return JsonSerializer.Deserialize<NativeControlReply>(bytes.ToArray(),AppPaths.Json)??throw new IOException("Пустой ответ службы.");bytes.WriteByte(one[0]);}throw new IOException("Ответ службы слишком большой.");
    }
    public async Task<NativeRouteView> Wait(string operation,bool connected,IProgress<NativeRouteView>? progress,CancellationToken cancellation)
    {
        while(true){cancellation.ThrowIfCancellationRequested();var reply=await Command("status",cancellation:cancellation);var state=reply.Native??throw new IOException("Встроенный режим не подготовлен.");progress?.Report(state);if(state.Operation!=operation)throw new IOException("Подключение изменилось. Проверь текущее состояние.");if(connected&&state.Connected||!connected&&(state.Phase is "Disconnected" or "Unconfigured")&&!state.Desired)return state;if(state.Phase is "RecoveryFailed" or "Unavailable")throw new IOException(NativeRoutePresentation.Detail(state));await Task.Delay(250,cancellation);}
    }
    public async Task<NativeRouteView> WaitImport(string operation,IProgress<NativeRouteView>? progress,CancellationToken cancellation)
    {
        while(true){var reply=await Command("status",cancellation:cancellation);var state=reply.Native??throw new IOException("Служба недоступна.");if(state.Operation!=operation)throw new IOException("Операция импорта изменилась. Проверь выбранный профиль.");progress?.Report(state);if(state.Phase=="Disconnected"&&!state.Desired)return state;if(state.Phase is "RecoveryFailed" or "Unavailable")throw new IOException("Проверка профиля не пройдена. Прежний профиль сохранён.");await Task.Delay(250,cancellation);}
    }
}
