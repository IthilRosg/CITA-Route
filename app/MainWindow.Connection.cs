using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace RoutePilot;

public partial class MainWindow
{
    private ConnectionInventory _inventory=new();
    private bool _discovering;
    private async Task DiscoverConnectionsAsync(string cli="")
    {
        if(_discovering)return;_discovering=true;
        string selected=ProfileInput.SelectedItem as string??"";
        try
        {
            _inventory=await new ConnectionSetup(_root,_backend).Discover(cli);
            _backend.CliPath=_inventory.CliPath;
            ProfileLabel.Text=string.IsNullOrEmpty(_inventory.SelectedProfile)?"Профиль: выбери подключение в настройках":"Профиль: "+_inventory.SelectedProfile;
            if(!_render && !File.Exists(Path.Combine(_root,"data","connection.json")) && !string.IsNullOrEmpty(_inventory.SelectedProfile) &&
                AppPaths.Read(Path.Combine(_root,"runtime","session.json"),()=>new EngineSession()).SourceProfile==_inventory.SelectedProfile)
                AppPaths.Write(Path.Combine(_root,"data","connection.json"),new{Version=1,SourceProfile=_inventory.SelectedProfile,CliPath=_inventory.CliPath,ExpectedEgressIp=""});
            CliPathLabel.Text=string.IsNullOrEmpty(_inventory.CliPath)?"WireSock не найден":_inventory.CliPath+" · "+_inventory.Version;
            ProfileInput.ItemsSource=_inventory.Profiles;
            ProfileInput.SelectedItem=_inventory.Profiles.Contains(selected)?selected:_inventory.Profiles.Contains(_inventory.SelectedProfile)?_inventory.SelectedProfile:_inventory.Profiles.Length==1?_inventory.Profiles[0]:null;
            ExpectedEgressInput.Text=_inventory.ExpectedEgressIp;
            ConnectionSetupLabel.Text=string.IsNullOrEmpty(_inventory.Message)?(_inventory.Profiles.Length==0?"Профилей нет. Импортируй свой .conf из WireGuard или AmneziaWG.":"Выбери свой исходный профиль. RoutePilot создаёт отдельную управляемую копию."):_inventory.Message;
        }
        catch(Exception ex){ConnectionSetupLabel.Text=Backend.Redact(ex.Message);}
        finally{_discovering=false;}
    }
    private async void FindWireSock_Click(object sender,RoutedEventArgs e)=>await DiscoverConnectionsAsync();
    private async void BrowseCli_Click(object sender,RoutedEventArgs e)
    {
        if(_busy||_engineRunning||_rescueState.BlocksRouting)return;
        var dialog=new Microsoft.Win32.OpenFileDialog{Title="WireSock Secure Connect CLI",Filter="WireSock CLI|wiresock-connect-cli.exe",CheckFileExists=true};
        if(dialog.ShowDialog(this)==true)await DiscoverConnectionsAsync(dialog.FileName);
    }
    private async void ImportProfile_Click(object sender,RoutedEventArgs e)
    {
        if(_busy||_engineRunning||_rescueState.BlocksRouting)return;
        if(string.IsNullOrEmpty(_inventory.CliPath)){Notice("Сначала найди установленный WireSock.",true);return;}
        var dialog=new Microsoft.Win32.OpenFileDialog{Title="Импорт своего WireGuard / AmneziaWG профиля",Filter="VPN-профиль|*.conf",CheckFileExists=true};
        if(dialog.ShowDialog(this)!=true)return;
        _busy=true;RefreshRescueState();
        try
        {
            await new ConnectionSetup(_root,_backend).Apply("Import",_inventory.CliPath,import:dialog.FileName);
            await DiscoverConnectionsAsync(_inventory.CliPath);
            ProfileInput.SelectedItem=Path.GetFileNameWithoutExtension(dialog.FileName);
            Notice("Профиль импортирован в WireSock. Нажми «Применить подключение».");
        }
        catch(Win32Exception ex)when(ex.NativeErrorCode==1223){Notice("Запрос Windows отменён.");}
        catch(Exception ex){Notice(Backend.Redact(ex.Message),true);}
        finally{_busy=false;RefreshRescueState();}
    }
    private async void ApplyConnection_Click(object sender,RoutedEventArgs e)=>await ApplyConnectionAsync();
    private async Task ApplyConnectionAsync()
    {
        if(_busy||_engineRunning||_rescueState.BlocksRouting)return;
        if(ProfileInput.SelectedItem is not string profile || string.IsNullOrEmpty(_inventory.CliPath)){Notice("Выбери профиль WireSock в настройках подключения.",true);return;}
        string egress=ExpectedEgressInput.Text.Trim();
        if(egress.Length>0 && (!System.Net.IPAddress.TryParse(egress,out var ip) || ip.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork)){Notice("IP выхода: укажи IPv4 или оставь поле пустым.",true);return;}
        _busy=true;RefreshRescueState();
        Notice("Готовлю подключение и службу восстановления. Подтверди запрос Windows.");
        try
        {
            await new ConnectionSetup(_root,_backend).Apply("Apply",_inventory.CliPath,profile,egress:egress);
            File.Delete(Path.Combine(_root,"data","pending-agent-update.json"));
            Notice("Подключение настроено. Нажми «Подключить», когда будешь готов.");
            _lastService=DateTime.MinValue;await DiscoverConnectionsAsync();
        }
        catch(Win32Exception ex)when(ex.NativeErrorCode==1223){Notice("Запрос Windows отменён.");}
        catch(Exception ex){Notice(Backend.Redact(ex.Message),true);}
        finally{_busy=false;RefreshRescueState();}
    }
}
