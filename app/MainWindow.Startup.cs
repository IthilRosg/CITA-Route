using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Cita.Startup;

namespace RoutePilot;

public partial class MainWindow
{
    private void InitializeStartup()
    {
        StartOnSignInBox.IsChecked=!_render&&StartupOptions.Enabled(Environment.ProcessPath!);
        var state=StartupPaths.Read();
        ConnectBeforeSignInBox.IsChecked=state.Owns(_root)&&state.Enabled;
        StartupStatusLabel.Text=state.Owns(_root)?state.Message:"Подключение до входа выключено. Сначала выбери и примени профиль.";
        if(state.Owns(_root)&&state.Enabled&&StartupAgentRefreshPending())
            StartupStatusLabel.Text="После обновления требуется подтвердить Windows обновление автоподключения. Профиль выбирать заново не нужно.";
    }
    private async Task RefreshStartupAfterUpdateAsync()
    {
        string pending=Path.Combine(_root,"data","pending-agent-update.json");
        var state=StartupPaths.Read();
        if(_render||_busy||_engineRunning||state.Active||!state.Enabled||!state.Owns(_root)||_rescueState.BlocksRouting||!StartupAgentRefreshPending())return;
        // Tray startup must not unexpectedly raise UAC at every Windows sign-in.
        if(Array.IndexOf(Environment.GetCommandLineArgs(),"--startup")>=0)return;
        string attempt=Path.Combine(_root,"data","startup-refresh-attempt.json"),version=ReleaseUpdates.Current.ToString();
        if(AppPaths.Read(attempt,()=>new StartupRefreshAttempt()).Version==version)return;
        _busy=true;ApplyStartupButton.IsEnabled=false;SyncConnectionButtons();
        try
        {
            // Record before UAC: cancellation/failure is retried by explicit
            // Connect/Apply, not by repeated automatic prompts on each launch.
            AppPaths.Write(attempt,new StartupRefreshAttempt{Version=version});
            Notice("Обновляю автоподключение с прежним профилем. Подтверди запрос Windows…");
            await _rescue.Install();
            var updated=StartupPaths.Read();
            if(!updated.Owns(_root)||!updated.Enabled||updated.Active)throw new IOException("Служба не подтвердила обновление автоподключения.");
            File.Delete(pending);InitializeStartup();Notice("Автоподключение обновлено. Оно сработает при следующем запуске Windows.");
        }
        catch(System.ComponentModel.Win32Exception ex)when(ex.NativeErrorCode==1223){Notice("Обновление автоподключения отложено. Его можно завершить кнопкой «Подключить» или в настройках.");}
        catch(Exception ex){Notice("Не удалось обновить автоподключение: "+Backend.FriendlyError(ex.Message),true);}
        finally{_busy=false;ApplyStartupButton.IsEnabled=true;RefreshRescueState();SyncConnectionButtons();}
    }
    private sealed class StartupRefreshAttempt{public string Version{get;set;}="";}
    private bool StartupAgentRefreshPending()
    {
        if(File.Exists(Path.Combine(_root,"data","pending-agent-update.json")))return true;
        try
        {
            if(!File.Exists(RescueClient.InstalledExe))return true;
            return !Version.TryParse(FileVersionInfo.GetVersionInfo(RescueClient.InstalledExe).FileVersion,out var installed)||ReleaseUpdates.Compare(installed,ReleaseUpdates.Current)<0;
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){return true;}
    }
    private async void ApplyStartup_Click(object sender,RoutedEventArgs e)
    {
        if(_render||_busy)return;
        try
        {
            StartupOptions.Set(Environment.ProcessPath!,StartOnSignInBox.IsChecked==true);
            bool enabled=ConnectBeforeSignInBox.IsChecked==true;
            var state=StartupPaths.Read();
            if(!enabled&&!(state.Owns(_root)&&state.Enabled)){Notice("Настройка автозапуска окна сохранена.");return;}
            if(_engineRunning||state.Active||_rescueState.BlocksRouting){Notice("Автозапуск окна сохранён. Для изменения подключения до входа сначала отключи маршрут и заверши диагностику.",true);return;}
            if(enabled&&!File.Exists(Path.Combine(_root,"runtime","router.json"))){Notice("Сначала выбери профиль, подключись и отключись для проверки подключения.",true);return;}
            _busy=true;ApplyStartupButton.IsEnabled=false;
            Notice("Подтверди запрос Windows для настройки подключения до входа…");
            // One Windows confirmation installs current protected code and publishes its snapshot.
            if(enabled)await _rescue.Install(true);
            else if(!(await _rescue.Command("boot-disable",_root)).Success)throw new IOException("Служба не подтвердила выключение автоподключения.");
            InitializeStartup();Notice(enabled?"Автоподключение включено для следующего запуска Windows. Пароль Windows не сохраняется.":"Подключение до входа выключено.");
        }
        catch(System.ComponentModel.Win32Exception ex)when(ex.NativeErrorCode==1223){Notice("Запрос Windows отменён. Подключение до входа не изменено.");}
        catch(Exception ex){Notice(Backend.FriendlyError(ex.Message),true);}
        finally{_busy=false;ApplyStartupButton.IsEnabled=true;RefreshRescueState();}
    }
}
