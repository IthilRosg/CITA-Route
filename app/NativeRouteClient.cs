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
    public string PolicyMode{get;set;}="SelectedRoutes";public string? ErrorCode{get;set;}public NativeRuntimeView? Runtime{get;set;}
    public bool Connected=>Phase=="Connected"&&Runtime?.CoreState=="Active";
    public bool Available=>Phase.Length>0&&Phase!="Unavailable";
}
public sealed class NativeControlReply{public bool Success{get;set;}public string Message{get;set;}="";public NativeRouteView? Native{get;set;}}
public static class NativeRoutePresentation
{
    public static string Title(NativeRouteView? s)=>s==null?"Служба недоступна":s.Phase switch{
        "Connected"=>s.Connected?"Подключено":"Проверяем подключение",
        "Preparing"=>"Подготавливаем подключение","Recovering" or "TransportDegraded" or "TransportUnavailable" or "TransportExhausted"=>"Восстанавливаем VPN",
        "Stopping"=>"Отключаем VPN","Disconnected"=>"Отключено",_=>"Требуется внимание"};
    public static string Detail(NativeRouteView? s)=>s==null?"Не удалось связаться со службой. Повтори проверку.":s.Phase switch{
        "Preparing"=>s.Runtime?.Phase switch{"CompilingPolicy"=>"Подготавливаем правила маршрутов…","ArmingGuard"=>"Включаем защиту соединения…","StartingCore"=>"Настраиваем сетевой адаптер…","Connecting"=>"Проверяем подключение через сервер…",_=>"Подготавливаем профиль…"},
        "Connected"=>s.PolicyMode=="FullVpn"?"Интернет через VPN · локальная сеть напрямую · IPv4":"Подключение по подготовленным правилам · IPv4",
        "Recovering" or "TransportDegraded" or "TransportUnavailable" or "TransportExhausted"=>"Восстанавливаем соединение. Защищённые адреса не переключаются напрямую.",
        "Stopping"=>"Завершаем соединения и освобождаем адаптер…","Disconnected"=>"Готово к подключению",
        "Unavailable"=>"Компоненты встроенного режима требуют настройки.",_=>"Соединение требует внимания. Проверь журнал перед повторным подключением."};
}
public sealed class NativeRouteClient
{
    public async Task<NativeControlReply> Command(string command,string? operation=null,CancellationToken cancellation=default)
    {
        if(command is not ("status" or "native-start" or "native-stop"))throw new ArgumentException("Native command scope.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var pipe=new NamedPipeClientStream(".","RoutePilot.Rescue.v1",PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);await pipe.ConnectAsync(timeout.Token);
        byte[] request=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{Command=command,RunId=operation})+"\n");await pipe.WriteAsync(request,timeout.Token);await pipe.FlushAsync(timeout.Token);
        using var bytes=new MemoryStream();byte[] one=new byte[1];while(bytes.Length<32768){if(await pipe.ReadAsync(one,timeout.Token)==0)throw new IOException("Служба закрыла соединение.");if(one[0]==10)return JsonSerializer.Deserialize<NativeControlReply>(bytes.ToArray(),AppPaths.Json)??throw new IOException("Пустой ответ службы.");bytes.WriteByte(one[0]);}throw new IOException("Ответ службы слишком большой.");
    }
    public async Task<NativeRouteView> Wait(string operation,bool connected,IProgress<NativeRouteView>? progress,CancellationToken cancellation)
    {
        while(true){cancellation.ThrowIfCancellationRequested();var reply=await Command("status",cancellation:cancellation);var state=reply.Native??throw new IOException("Встроенный режим не подготовлен.");progress?.Report(state);if(state.Operation!=operation)throw new IOException("Подключение изменилось. Проверь текущее состояние.");if(connected&&state.Connected||!connected&&state.Phase=="Disconnected"&&!state.Desired)return state;if(state.Phase is "RecoveryFailed" or "Unavailable")throw new IOException(NativeRoutePresentation.Detail(state));await Task.Delay(250,cancellation);}
    }
}
