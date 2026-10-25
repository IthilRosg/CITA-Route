using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace RoutePilot;

public partial class MainWindow
{
    async Task ImportNativeProfileAsync()
    {
        if(_render||_busy||_engineRunning||_rescueState.BlocksRouting)return;
        var dialog=new OpenFileDialog{Title="Профиль WireGuard / AmneziaWG",Filter="Конфигурация (*.conf)|*.conf",CheckFileExists=true,Multiselect=false};if(dialog.ShowDialog(this)!=true)return;
        _busy=true;_connectionCancellation=new();SyncConnectionButtons();string? operation=null;
        try
        {
            string encoded;
            using(var input=new FileStream(dialog.FileName,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(input.Length is <1 or >12288)throw new IOException("Для встроенного режима нужен .conf размером до 12 КБ.");byte[] bytes=new byte[checked((int)input.Length)];
                try{await input.ReadExactlyAsync(bytes,_connectionCancellation.Token);encoded=Convert.ToBase64String(bytes);}finally{CryptographicOperations.ZeroMemory(bytes);}
            }
            var before=(await _nativeClient.Command("status",cancellation:_connectionCancellation.Token)).Native;
            if(before?.Phase is not ("Disconnected" or "Unconfigured")||before.Desired)throw new IOException("Сначала отключи подключение и дождись завершения операции.");
            operation=Guid.NewGuid().ToString("N");var reply=await _nativeClient.Import(encoded,Path.GetFileNameWithoutExtension(dialog.FileName),before.Operation,_connectionCancellation.Token,operation);
            if(!reply.Success||reply.Native==null)throw new IOException("Служба не приняла импорт. Проверь выбранный профиль.");
            _nativeChoice.Selected=true;AppPaths.Write(NativeChoicePath,_nativeChoice);
            _nativeModeChanging=true;try{_nativeMode.SelectedIndex=1;}finally{_nativeModeChanging=false;}
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_connectionCancellation.Token);deadline.CancelAfter(TimeSpan.FromSeconds(60));
            var selected=await _nativeClient.WaitImport(operation,new Progress<NativeRouteView>(ApplyNativeView),deadline.Token);_nativeChoice.ConfirmedProfile=selected.ProfileId;AppPaths.Write(NativeChoicePath,_nativeChoice);
            Notice("Профиль проверен и выбран. Можно подключаться.");
        }
        catch(Exception ex)
        {
            if(operation!=null)try{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(25));var ack=await _nativeClient.Command("native-stop",operation,cleanup.Token);if(!ack.Success)throw new IOException();await _nativeClient.Wait(operation,false,new Progress<NativeRouteView>(ApplyNativeView),cleanup.Token);}catch{Notice("Остановка импорта не подтверждена. Проверь состояние службы.",true);return;}
            Notice(ex is OperationCanceledException?"Импорт отменён. Проверь текущий выбранный профиль.":Backend.FriendlyError(ex.Message),ex is not OperationCanceledException);
        }
        finally{_connectionCancellation.Dispose();_connectionCancellation=null;_busy=false;_nativeReadUtc=DateTime.MinValue;await RefreshAsync();SyncConnectionButtons();}
    }
}
