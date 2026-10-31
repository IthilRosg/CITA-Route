using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RoutePilot.Agent;

namespace RoutePilot;

internal static class NativeLaunchPolicy
{
    internal static bool MayProceed(bool startOnly,bool busy)=>!startOnly||!busy;
    internal static bool WantsStart(bool startOnly,bool cachedDesired)=>startOnly||!cachedDesired;
    internal static bool ConfirmedProfileMatches(string? remembered,string? profileId)
        =>!string.IsNullOrWhiteSpace(remembered)&&!string.IsNullOrWhiteSpace(profileId)&&string.Equals(remembered,profileId,StringComparison.Ordinal);
}

public sealed class NativeRuntimeView
{
    public string Phase{get;set;}="";public string CoreState{get;set;}="";public string Ipv6Mode{get;set;}="";
    public string ZapretState{get;set;}="";public string? ZapretProbeReason{get;set;}
    public int Repairs{get;set;}public int NativeDnsProtocol{get;set;}public string AutomaticRouting{get;set;}="";public int ObservedDestinations{get;set;}public int IdentifiedDestinations{get;set;}public int CalibratedDomains{get;set;}public int CalibrationChecks{get;set;}public int CalibrationPassed{get;set;}public int CalibrationFailed{get;set;}public int CalibrationErrors{get;set;}public long PolicyDirectDials{get;set;}public NativeTransportView? TransportHealth{get;set;}
}
public sealed class NativeTransportView{public string State{get;set;}="";public int Epoch{get;set;}public bool? Https{get;set;}public bool? Udp{get;set;}public long CompletedUnixMilliseconds{get;set;}public int SocketRepairs{get;set;}public int Restarts{get;set;}}
public sealed class NativeTraceEntryView{public string Domain{get;set;}="";public string Application{get;set;}="";public string Event{get;set;}="";public long UnixMilliseconds{get;set;}}
public sealed class NativeRouteView
{
    public string Phase{get;set;}="";public bool Desired{get;set;}public string Operation{get;set;}="";public long Generation{get;set;}
    public string ConnectionId{get;set;}="";public int ConnectionProtocol{get;set;}
    public string ProfileName{get;set;}="Подготовленный профиль";public string ProfileId{get;set;}="";
    public string PolicyMode{get;set;}="SelectedRoutes";public string? ErrorCode{get;set;}public string? ErrorStage{get;set;}public string? ImportStage{get;set;}public NativeRuntimeView? Runtime{get;set;}
    public string? ZapretIssue{get;set;}
    public bool TraceEnabled{get;set;}public NativeTraceEntryView[] TraceEntries{get;set;}=[];
    public bool Connected=>Phase=="Connected"&&Runtime?.CoreState=="Active";
    public bool Available=>Phase.Length>0&&Phase!="Unavailable";
}
public sealed class NativeControlReply{public bool Success{get;set;}public string Message{get;set;}="";public NativeRouteView? Native{get;set;}}
internal static class NativeConnectionPolicy
{
    internal static bool SupportsProtocol(NativeControlReply? capabilities)=>capabilities?.Success==true&&capabilities.Native?.ConnectionProtocol==1;
    internal static bool ValidId(string? id)=>id is {Length:32}&&System.Linq.Enumerable.All(id,c=>char.IsAsciiHexDigit(c)&&!char.IsUpper(c));
    internal static string? StopIdentity(NativeRouteView state)=>state.Phase is not ("Importing" or "Unconfigured")&&ValidId(state.ConnectionId)?state.ConnectionId:null;
    internal static bool Owns(NativeRouteView? state,string id)=>ValidId(id)&&string.Equals(state?.ConnectionId,id,StringComparison.Ordinal);
    internal static bool ConfirmsAbsent(NativeRouteView? state,string id,string expectedOperation)
    {
        if(state?.Available!=true||!ValidId(id)||Owns(state,id))return false;
        if(ValidId(state.ConnectionId))return true;
        return string.IsNullOrEmpty(state.ConnectionId)&&!state.Desired&&(state.Phase is "Disconnected" or "Unconfigured")&&string.Equals(state.Operation,expectedOperation,StringComparison.Ordinal);
    }
    internal static void RequireOwned(NativeRouteView? state,string id)
    {if(!Owns(state,id))throw new IOException("Подключение изменилось. Проверь текущее состояние.");}
    internal static async Task<bool> CleanupOwnAsync(string id,string expectedOperation,Func<Task<NativeControlReply>> read,Func<string,Task<NativeControlReply>> stop,Func<string,Task> wait)
    {
        var status=await read();var current=status.Native;
        if(!status.Success||current?.Available!=true)throw new IOException("Состояние подключения недоступно.");
        if(ConfirmsAbsent(current,id,expectedOperation))return true;
        if(!Owns(current,id))throw new IOException("Не удалось определить принадлежность подключения.");
        var stopped=await stop(id);if(!stopped.Success)return false;
        await wait(id);return true;
    }
}
public static class NativeRoutePresentation
{
    static string? EffectivePhase(NativeRouteView? s)=>s?.Phase=="Connected"&&s.Runtime?.CoreState is "CoreRepairFailed" or "CoreRecoveryUnconfirmed" or "GuardUnconfirmed" or "GuardLost" or "GuardLostUnconfirmed" or "TransportCloseUnconfirmed" or "LeaseCloseUnconfirmed" or "PolicyFailClosed" or "PolicyRecoveryUnconfirmed"?s.Runtime.CoreState:s?.Phase;
    public static bool RequiresAttention(NativeRouteView s)=>EffectivePhase(s) is "RecoveryFailed" or "Unavailable" or "CoreRecoveryUnconfirmed" or "PolicyRecoveryUnconfirmed" or "GuardLost" or "GuardLostUnconfirmed" or "TransportCloseUnconfirmed" or "LeaseCloseUnconfirmed";
    static string ConnectedDetail(NativeRouteView s)
    {
        string vpn=s.PolicyMode=="FullVpn"?"VPN подключён.":"VPN подключён для адресов профиля.";
        if(!string.IsNullOrEmpty(s.ZapretIssue))return vpn+" Модуль обхода DPI недоступен; автоматический выбор использует VPN и прямой путь.";
        return s.Runtime?.AutomaticRouting switch{
            "CheckingDirect"=>vpn+" Проверяем прямой доступ для нового адреса…",
            "CheckingZapret"=>vpn+" Проверяем обход DPI для нового адреса…",
            "Stopped"=>vpn+" Автоматические проверки прямого пути остановлены.",
            "Unavailable" or "NotConfigured"=>vpn+" Автоматические проверки прямого пути недоступны.",
            _=>vpn+" Проверенные HTTPS-домены могут идти напрямую."
        };
    }
    public static string Title(NativeRouteView? s)=>s==null?"Служба недоступна":s.ErrorCode=="NativeMaintenanceInProgress"?"Обновление модуля":EffectivePhase(s) switch{
        "Connected"=>s.Connected?"Подключено":s.Runtime?.CoreState is "TransportDegraded" or "TransportUnavailable" or "TransportExhausted" or "Exhausted"?"Восстанавливаем VPN":"Проверяем подключение",
        "Importing"=>"Проверяем профиль","Preparing"=>"Подготавливаем подключение","Recovering" or "CoreRepairFailed" or "TransportDegraded" or "TransportUnavailable" or "PolicyFailClosed"=>"Восстанавливаем VPN","TransportExhausted"=>"Требуется внимание",
        "GuardUnconfirmed"=>"Проверяем защиту маршрутов",
        "Stopping"=>"Отключаем VPN","Unconfigured"=>"Выбери профиль","Disconnected"=>"Отключено",_=>"Требуется внимание"};
    public static string Detail(NativeRouteView? s)=>s==null?"Не удалось связаться со службой. Повтори проверку.":s.ErrorCode=="NativeMaintenanceInProgress"?"Обновление модуля не завершено. Требуется завершить восстановление перед подключением.":EffectivePhase(s) switch{
        "Importing"=>s.ImportStage switch{"ReadingProfile"=>"Читаем профиль и определяем адрес сервера…","ValidatingProfile"=>"Проверяем параметры профиля…","ProbingHttps"=>"Проверяем HTTPS через сервер…","RetryingMtu"=>"Проверяем подключение с меньшим размером пакетов…","ProbingUdp"=>"Проверяем передачу UDP через сервер…","CompilingPolicy"=>"Подготавливаем правила маршрутизации…","WritingSnapshot"=>"Сохраняем защищённый профиль…",_=>"Проверяем конфиг и доступ через сервер…"},
        "Unconfigured"=>"Компоненты готовы. Импортируй свой профиль для подключения.",
        "Preparing"=>s.Runtime?.Phase switch{"CompilingPolicy"=>"Подготавливаем правила маршрутов…","ArmingGuard"=>"Включаем защиту соединения…","StartingCore"=>"Настраиваем сетевой адаптер…","Connecting"=>"Проверяем подключение через сервер…",_=>"Подготавливаем профиль…"},
        "Connected" when !s.Connected=>s.Runtime?.CoreState is "TransportDegraded" or "TransportUnavailable" or "TransportExhausted" or "Exhausted"?"Соединение нестабильно; CITA пытается его восстановить. Защищённые адреса не переключаются напрямую.":"Проверяем состояние VPN-соединения…",
        "Connected"=>ConnectedDetail(s),
        "PolicyFailClosed"=>"Не удалось применить маршруты. Подключение остановлено, готовим восстановление…",
        "CoreRepairFailed"=>"Не удалось перезапустить сетевой модуль. Подготовим повторную попытку; защищённые адреса не переключаются напрямую.",
        "CoreRecoveryUnconfirmed"=>"Не удалось подтвердить завершение прежнего сетевого модуля. Собери диагностику перед повторным подключением.",
        "PolicyRecoveryUnconfirmed"=>"Не удалось подтвердить безопасное завершение маршрутов. Проверь журнал и собери диагностику.",
        "GuardUnconfirmed"=>"Проверяем защиту маршрутов. Доступность VPN пока не подтверждена…",
        "GuardLost" or "GuardLostUnconfirmed"=>"Не удалось подтвердить защиту маршрутов. Подключение требует проверки; собери диагностику.",
        "TransportCloseUnconfirmed" or "LeaseCloseUnconfirmed"=>"Не удалось подтвердить остановку сетевого модуля. Собери диагностику перед повторным подключением.",
        "RecoveryFailed" when s.ErrorCode=="PolicyRecoveryUnconfirmed"=>"Не удалось подтвердить безопасное завершение маршрутов. Проверь журнал и собери диагностику перед повторной попыткой.",
        "RecoveryFailed" when s.ErrorCode=="CoreRecoveryUnconfirmed"=>"Не удалось подтвердить завершение прежнего сетевого модуля. Автовосстановление остановлено; собери диагностику перед повторным подключением.",
        "RecoveryFailed" when s.ErrorCode is "GuardLost" or "GuardLostUnconfirmed"=>"Не удалось подтвердить защиту маршрутов. Автовосстановление остановлено; собери диагностику перед повторным подключением.",
        "RecoveryFailed" when s.ErrorCode is "TransportCloseUnconfirmed" or "LeaseCloseUnconfirmed"=>"Не удалось подтвердить остановку сетевого модуля. Автовосстановление остановлено; собери диагностику перед повторным подключением.",
        "Recovering" or "TransportDegraded" or "TransportUnavailable"=>"Восстанавливаем соединение. Защищённые адреса не переключаются напрямую.",
        "TransportExhausted"=>"Проверяем возможность восстановления соединения; защищённые адреса не переключаются напрямую.",
        "RecoveryFailed"=>s.ErrorCode switch{"RepairBudgetExhausted"=>"Автоматические попытки восстановления исчерпаны. Проверь сервер и повтори подключение.","NativeHttpsUnavailable"=>"Не удалось проверить HTTPS через VPN. Проверь сервер и повтори подключение.","NativeEgressMismatch"=>"Выход VPN изменился; подключение остановлено до проверки сервера.","NativeUdpTimeout"=>"Проверка UDP через VPN не получила ответ вовремя. Проверь сеть и сервер, затем повтори подключение.","NativeUdpUnavailable"=>"Через VPN не прошла проверка UDP. Звонки и игры могут быть недоступны; проверь сервер.",_=>s.ErrorStage switch{"Initializing"=>"Не удалось подготовить сетевую защиту или двигатель. Собери диагностику перед повторной попыткой.","Connecting"=>"Не удалось установить соединение с VPN. Собери диагностику и проверь сервер.","Importing"=>"Не удалось проверить профиль. Прежний профиль сохранён.","Stopping"=>"Не удалось подтвердить завершение подключения. Проверь состояние и собери диагностику.",_=>"Соединение требует внимания. Собери диагностику перед повторным подключением."}},
        "Stopping"=>"Завершаем соединения и освобождаем адаптер…","Disconnected"=>"Готово к подключению",
        "Unavailable"=>"Компоненты встроенного режима требуют настройки.",_=>"Соединение требует внимания. Проверь журнал перед повторным подключением."};
}
public sealed class NativeRouteClient
{
    public async Task<NativeControlReply> Command(string command,string? operation=null,CancellationToken cancellation=default)
    {
        if(command is not ("status" or "native-start" or "native-stop" or "native-capabilities" or "native-trace-on" or "native-trace-off" or "native-trace-read" or "native-forced-read" or "native-forced-init"))throw new ArgumentException("Native command scope.");
        return await Exchange(new{Command=command,RunId=operation},cancellation);
    }
    public Task<NativeControlReply> Import(string base64,string name,string operation,CancellationToken cancellation,string requestId="")
    {
        if(base64.Length>16384||name.Length>160)throw new ArgumentException("Native import size.");
        return Exchange(new{Command="native-import",RunId=operation,Profile=new{Base64=base64,Name=name,RequestId=requestId}},cancellation);
    }
    public Task<NativeControlReply> StartConnection(string operation,string connectionId,CancellationToken cancellation)
    {
        if(!NativeConnectionPolicy.ValidId(operation)||!NativeConnectionPolicy.ValidId(connectionId))throw new ArgumentException("Invalid native connection identity.");
        return Exchange(new{Command="native-start",RunId=operation,ConnectionId=connectionId},cancellation);
    }
    public Task<NativeControlReply> StopConnection(string connectionId,CancellationToken cancellation)
    {
        if(!NativeConnectionPolicy.ValidId(connectionId))throw new ArgumentException("Invalid native connection identity.");
        return Exchange(new{Command="native-stop-session",ConnectionId=connectionId},cancellation);
    }
    public Task<NativeControlReply> ReadForcedIntent(ulong minimumRevision,CancellationToken cancellation)
        =>Exchange(new{Command="native-forced-read",Forced=new{ExpectedRevision=minimumRevision}},cancellation);
    public Task<NativeControlReply> InitializeForcedIntent(CancellationToken cancellation)
        =>Exchange(new{Command="native-forced-init"},cancellation);
    public Task<NativeControlReply> ReplaceForcedIntent(ulong expectedRevision,NativeForcedIntentRule[] rules,CancellationToken cancellation)
    {
        if(rules is null||rules.Length>128)throw new ArgumentException("Forced intent size.");
        return Exchange(new{Command="native-forced-replace",Forced=new{ExpectedRevision=expectedRevision,Rules=rules}},cancellation);
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
        while(true){cancellation.ThrowIfCancellationRequested();var reply=await Command("status",cancellation:cancellation);var state=reply.Native??throw new IOException("Встроенный режим не подготовлен.");progress?.Report(state);if(state.Operation!=operation)throw new IOException("Подключение изменилось. Проверь текущее состояние.");if(connected&&state.Connected||!connected&&(state.Phase is "Disconnected" or "Unconfigured")&&!state.Desired)return state;if(NativeRoutePresentation.RequiresAttention(state))throw new IOException(NativeRoutePresentation.Detail(state));await Task.Delay(250,cancellation);}
    }
    public async Task<NativeRouteView> WaitImport(string operation,IProgress<NativeRouteView>? progress,CancellationToken cancellation)
    {
        while(true){var reply=await Command("status",cancellation:cancellation);var state=reply.Native??throw new IOException("Служба недоступна.");if(state.Operation!=operation)throw new IOException("Операция импорта изменилась. Проверь выбранный профиль.");progress?.Report(state);if(state.Phase=="Disconnected"&&!state.Desired)return state;if(state.Phase is "RecoveryFailed" or "Unavailable")throw new IOException("Проверка профиля не пройдена. Прежний профиль сохранён.");await Task.Delay(250,cancellation);}
    }
    public async Task<NativeRouteView> WaitConnection(string connectionId,bool connected,IProgress<NativeRouteView>? progress,CancellationToken cancellation)
    {
        while(true){cancellation.ThrowIfCancellationRequested();var reply=await Command("status",cancellation:cancellation);if(!reply.Success)throw new IOException("Не удалось подтвердить состояние подключения.");var state=reply.Native??throw new IOException("Встроенный режим не подготовлен.");NativeConnectionPolicy.RequireOwned(state,connectionId);progress?.Report(state);if(connected&&state.Desired&&state.Connected||!connected&&(state.Phase is "Disconnected" or "Unconfigured")&&!state.Desired)return state;if(NativeRoutePresentation.RequiresAttention(state))throw new IOException(NativeRoutePresentation.Detail(state));await Task.Delay(250,cancellation);}
    }
}
