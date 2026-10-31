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
    bool _nativeSetupInstalling;
    async Task PrepareNativeComponentsAsync()
    {
        if(_render||_busy||_engineRunning||_rescueState.BlocksRouting)return;
        _busy=true;_connectionCancellation=new();SyncConnectionButtons();
        try
        {
            foreach(string name in new[]{"Invoke-NativeBundleUpgrade.ps1","Install-NativeAgent.ps1","Install-NativeComponents.ps1"})if(!File.Exists(Path.Combine(_root,name)))throw new IOException("Для подготовки встроенного режима установи актуальную версию CITA через установщик.");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_connectionCancellation.Token);timeout.CancelAfter(TimeSpan.FromMinutes(10));
            FooterStatus.Text="Загружаем встроенный модуль…";SyncCompact();var archive=await NativeComponentDownload.Fetch(_root,new Progress<int>(percent=>{FooterStatus.Text="Загрузка встроенного модуля: "+percent+"%";SyncCompact();}),timeout.Token);
            _connectionCancellation.Token.ThrowIfCancellationRequested();FooterStatus.Text="Ожидаем подтверждение Windows и устанавливаем модуль…";SyncCompact();
            _connectionCancellation.Dispose();_connectionCancellation=null;_nativeSetupInstalling=true;SetConnectionStage("Installing");SyncConnectionButtons();
            string Quote(string value)=>"'"+value.Replace("'","''")+"'";
            string command="$ErrorActionPreference='Stop';try{& "+Quote(Path.Combine(_root,"Invoke-NativeBundleUpgrade.ps1"))+" -UserSid "+Quote(WindowsIdentity.GetCurrent().User!.Value)+" -UserProfile "+Quote(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))+" -Package "+Quote(archive.Path)+" -Sha256 "+archive.Hash+";exit 0}catch{$m=$_.Exception.Message;if($m -match 'No eligible physical uplink|One unambiguous physical uplink'){exit 11};if($m -match 'Multiple equally preferred uplinks'){exit 12};if($m -match 'Disconnect CITA routing|Disconnect the existing route'){exit 13};if($m -match 'package digest mismatch|role digest mismatch|notice digest mismatch'){exit 14};if($m -match 'previous components were restored'){exit 24};exit 25}";
            var info=new ProcessStartInfo("powershell.exe"){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden,Arguments="-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(command))};
            using var process=Process.Start(info)??throw new IOException("Не удалось запустить установку модуля.");await process.WaitForExitAsync();if(process.ExitCode!=0)throw new IOException(process.ExitCode switch{
                11=>"Модуль не установлен: не найден работающий интернет-адаптер с маршрутом IPv4.",
                12=>"Модуль не установлен: Windows использует несколько равнозначных интернет-маршрутов.",
                13=>"Модуль не установлен: сначала отключи действующий маршрут CITA.",
                14=>"Модуль не установлен: проверка целостности компонентов не пройдена.",
                24=>"Обновление модуля отклонено. Предыдущие компоненты восстановлены.",
                _=>"Завершение установки модуля не подтверждено. Проверь состояние службы и собери диагностику перед повторной попыткой."
            });
            _nativeChoice.Selected=true;AppPaths.Write(NativeChoicePath,_nativeChoice);_nativeModeChanging=true;try{_nativeMode.SelectedIndex=1;}finally{_nativeModeChanging=false;}
            Notice("Встроенный модуль готов. Нажми «Подключить», чтобы выбрать свой профиль.");
        }
        catch(System.ComponentModel.Win32Exception error)when(error.NativeErrorCode==1223){Notice("Установка отменена в запросе Windows.");}
        catch(OperationCanceledException){Notice("Загрузка модуля отменена.");}
        catch(Exception error){Notice(Backend.FriendlyError(error.Message),true);}
        finally{_nativeSetupInstalling=false;_connectionCancellation?.Dispose();_connectionCancellation=null;_busy=false;_operationPhase="";_nativeReadUtc=DateTime.MinValue;await RefreshAsync();SyncConnectionButtons();}
    }
}
