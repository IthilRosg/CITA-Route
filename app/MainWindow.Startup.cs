using System;
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
        if(!_render&&_preferences.StartupBehaviorVersion<1)
        {
            // Preserve the user's previous auto-connect choice on migration.
            if(state.Owns(_root)&&state.Enabled)_preferences.ConnectOnLaunch=true;
            _preferences.StartupBehaviorVersion=1;
            AppPaths.Write(Path.Combine(_root,"data","preferences.json"),_preferences);
        }
        ConnectOnLaunchBox.IsChecked=_preferences.ConnectOnLaunch;
        StartupStatusLabel.Text=!ClientEdition.NativeOnly&&state.Owns(_root)&&state.Enabled?
            "Сохранено прежнее подключение до входа. Для перехода отключи маршрут и нажми «Применить автозапуск». Текущее подключение сохраняется.":
            "Запуск окна и подключение настраиваются отдельно. При автоподключении используется последний профиль.";
    }
    private async Task<bool> RetireLegacyStartupAsync()
    {
        if(ClientEdition.NativeOnly)return true;
        var state=StartupPaths.Read();
        if(!state.Owns(_root)||!state.Enabled)return true;
        if(state.Active||_engineRunning||_rescueState.BlocksRouting)return false;
        var result=await _rescue.Command("boot-disable",_root);
        var updated=StartupPaths.Read();
        if(!result.Success||(updated.Enabled&&updated.Owns(_root)))
            throw new IOException("Не удалось выключить прежнее подключение до входа. Настройка автоподключения не изменена.");
        return true;
    }
    private async Task<bool> RefreshStartupAfterUpdateAsync()
    {
        if(_render||_busy)return false;
        try{bool retired=await RetireLegacyStartupAsync();InitializeStartup();return retired;}
        catch(Exception ex){Notice(Backend.FriendlyError(ex.Message),true);return false;}
    }
    private async void ApplyStartup_Click(object sender,RoutedEventArgs e)
    {
        if(_render||_busy)return;
        _busy=true;ApplyStartupButton.IsEnabled=false;
        try
        {
            if(!await RetireLegacyStartupAsync())
            {Notice("Для перехода со старого подключения до входа сначала отключи маршрут. Настройки пока не изменены.",true);return;}
            StartupOptions.Set(Environment.ProcessPath!,StartOnSignInBox.IsChecked==true);
            _preferences.ConnectOnLaunch=ConnectOnLaunchBox.IsChecked==true;
            _preferences.StartupBehaviorVersion=1;SaveData();
            InitializeStartup();Notice("Настройки сохранены. Автоподключение сработает при следующем запуске CITA Route.");
        }
        catch(Exception ex){Notice(Backend.FriendlyError(ex.Message),true);}
        finally{_busy=false;ApplyStartupButton.IsEnabled=true;RefreshRescueState();SyncConnectionButtons();}
    }
}
