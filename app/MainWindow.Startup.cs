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
