using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RoutePilot;

public partial class MainWindow
{
    async Task PrepareNativeComponentsAsync()
    {
        if(_render||_busy||_engineRunning||_rescueState.BlocksRouting)return;
        _busy=true;_connectionCancellation=new();SyncConnectionButtons();
        try
        {
            foreach(string name in new[]{"Install-NativeAgent.ps1","Install-NativeComponents.ps1"})if(!File.Exists(Path.Combine(_root,name)))throw new IOException("Для первого запуска встроенного режима установи CITA через полный установщик.");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_connectionCancellation.Token);timeout.CancelAfter(TimeSpan.FromMinutes(10));
            FooterStatus.Text="Загружаем встроенный модуль…";SyncCompact();var archive=await NativeComponentDownload.Fetch(_root,new Progress<int>(percent=>{FooterStatus.Text="Загрузка встроенного модуля: "+percent+"%";SyncCompact();}),timeout.Token);
            _connectionCancellation.Token.ThrowIfCancellationRequested();FooterStatus.Text="Ожидаем подтверждение Windows и устанавливаем модуль…";SyncCompact();
            _connectionCancellation.Dispose();_connectionCancellation=null;SetConnectionStage("Installing");SyncConnectionButtons();
            string Quote(string value)=>"'"+value.Replace("'","''")+"'";
            string command="$ErrorActionPreference='Stop';try{& "+Quote(Path.Combine(_root,"Install-NativeAgent.ps1"))+" -UserSid "+Quote(WindowsIdentity.GetCurrent().User!.Value)+" -UserProfile "+Quote(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))+";& "+Quote(Path.Combine(_root,"Install-NativeComponents.ps1"))+" -Package "+Quote(archive.Path)+" -Sha256 "+archive.Hash+";exit 0}catch{exit 1}";
            var info=new ProcessStartInfo("powershell.exe"){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden,Arguments="-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(command))};
            using var process=Process.Start(info)??throw new IOException("Не удалось запустить установку модуля.");await process.WaitForExitAsync();if(process.ExitCode!=0)throw new IOException("Модуль не установлен. Предыдущие компоненты сохранены; проверь журнал установки.");
            _nativeChoice.Selected=true;AppPaths.Write(NativeChoicePath,_nativeChoice);_nativeModeChanging=true;try{_nativeMode.SelectedIndex=1;}finally{_nativeModeChanging=false;}
            Notice("Встроенный модуль готов. Нажми «Подключить», чтобы выбрать свой профиль.");
        }
        catch(System.ComponentModel.Win32Exception error)when(error.NativeErrorCode==1223){Notice("Установка отменена в запросе Windows.");}
        catch(OperationCanceledException){Notice("Загрузка модуля отменена.");}
        catch(Exception error){Notice(Backend.FriendlyError(error.Message),true);}
        finally{_connectionCancellation?.Dispose();_connectionCancellation=null;_busy=false;_operationPhase="";_nativeReadUtc=DateTime.MinValue;await RefreshAsync();SyncConnectionButtons();}
    }
}
