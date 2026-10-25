using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace RoutePilot;

public partial class MainWindow
{
    private async void OpenProfileFolders_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            if(_inventory.ProfileDirectories.Length==0)await DiscoverConnectionsAsync(_inventory.CliPath);
            var paths=_inventory.ProfileDirectories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if(paths.Length==0){Notice("Папки профилей WireSock не найдены. Сначала импортируй профиль в WireSock или выбери .conf для импорта.");return;}
            var menu=new System.Windows.Controls.ContextMenu{Background=ConsoleTheme.Brush("#171517"),Foreground=ConsoleTheme.Text};
            foreach(string path in paths)
            {
                var item=new System.Windows.Controls.MenuItem{Header=path};
                item.Click+=(_,_)=>{try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path){UseShellExecute=true});}catch(Exception ex){Notice(Backend.FriendlyError(ex.Message),true);}};
                menu.Items.Add(item);
            }
            menu.PlacementTarget=sender as UIElement;menu.IsOpen=true;
        }
        catch(Exception ex){Notice(Backend.FriendlyError(ex.Message),true);}
    }
    private ConnectionInventory _inventory=new();
    private bool _discovering;
    private readonly ProfileMemory _profileMemory=new();
    private string _rememberedSession="";
    private Task? _discoveryWork;
    private Task DiscoverConnectionsAsync(string cli="")
    {
        if(_discoveryWork is {IsCompleted:false})return _discoveryWork;
        return _discoveryWork=DiscoverConnectionsCoreAsync(cli);
    }
    private async Task DiscoverConnectionsCoreAsync(string cli)
    {
        if(!_render&&_rescue.Installed&&await RefreshNativeAsync(force:true))return;
        if(_discovering)return;_discovering=true;RefreshRescueState();
        string selected=ProfileInput.SelectedItem as string??"";
        try
        {
            _inventory=await new ConnectionSetup(_root,_backend).Discover(cli);
            string settingsFile=Path.Combine(_root,"data","connection.json"),sessionFile=Path.Combine(_root,"runtime","session.json");
            DateTime choiceAt=File.Exists(settingsFile)?File.GetLastWriteTimeUtc(settingsFile):File.Exists(sessionFile)?File.GetLastWriteTimeUtc(sessionFile):DateTime.MinValue;
            _backend.CliPath=_inventory.CliPath;
            ProfileLabel.Text=string.IsNullOrEmpty(_inventory.SelectedProfile)?"Профиль: выбери подключение в настройках":"Профиль: "+_inventory.SelectedProfile;
            if(!_render && !File.Exists(Path.Combine(_root,"data","connection.json")) && !string.IsNullOrEmpty(_inventory.SelectedProfile) &&
                AppPaths.Read(Path.Combine(_root,"runtime","session.json"),()=>new EngineSession()).SourceProfile==_inventory.SelectedProfile)
                AppPaths.Write(Path.Combine(_root,"data","connection.json"),new{Version=1,SourceProfile=_inventory.SelectedProfile,CliPath=_inventory.CliPath,ExpectedEgressIp=""});
            string detectedPath=string.IsNullOrEmpty(_inventory.ExecutablePath)?_inventory.CliPath:_inventory.ExecutablePath;
            CliPathLabel.Text=string.IsNullOrEmpty(detectedPath)?"WireSock не найден":detectedPath+" · "+_inventory.Version;
            if(!_render && _inventory.SelectedProfile.Length>0 && File.Exists(Path.Combine(_root,"data","connection.json")))
                try{_profileMemory.Remember(_inventory.SelectedProfile,_inventory.CliPath,choiceAt,false);}catch(IOException){}catch(UnauthorizedAccessException){}
            ProfileInput.ItemsSource=_inventory.Profiles;
            ProfileInput.SelectedItem=_profileMemory.Select(_inventory.CliPath,_inventory.SelectedProfile,selected,_inventory.Profiles);
            ExpectedEgressInput.Text=(ProfileInput.SelectedItem as string)==_inventory.SelectedProfile?_inventory.ExpectedEgressIp:"";
            ConnectionSetupLabel.Text=string.IsNullOrEmpty(_inventory.Message)?(_inventory.Profiles.Length==0?"Профилей нет. Импортируй свой .conf из WireGuard или AmneziaWG.":"Выбери свой исходный профиль. CITA Route создаёт отдельную управляемую копию."):_inventory.Message;
            if(string.IsNullOrEmpty(_inventory.Message)&&ProfileInput.SelectedItem is string restored && restored!=_inventory.SelectedProfile)ConnectionSetupLabel.Text="Восстановлен последний профиль. Примени подключение для этой папки приложения.";
            else if(string.IsNullOrEmpty(_inventory.Message)&&ProfileInput.SelectedItem==null && _inventory.CliPath.Length>0 && _profileMemory.Preferred(_inventory.CliPath,_inventory.SelectedProfile).Length>0)ConnectionSetupLabel.Text="Последний профиль пока не найден. Повтори поиск или выбери другой профиль явно.";
        }
        catch(Exception ex){ConnectionSetupLabel.Text=Backend.Redact(ex.Message);}
        finally{_discovering=false;RefreshRescueState();SyncCompact();}
    }
    private async void FindWireSock_Click(object sender,RoutedEventArgs e)=>await DiscoverConnectionsAsync();
    private void ProfileInput_SelectionChanged(object sender,System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if(!_ready||_discovering)return;
        ExpectedEgressInput.Text=(ProfileInput.SelectedItem as string)==_inventory.SelectedProfile?_inventory.ExpectedEgressIp:"";
        SyncCompact();
    }
    private void RememberVerifiedProfile(EngineSession session)
    {
        if(_render||!_egressVerified||!session.Running||session.Recovering||string.IsNullOrEmpty(session.Id)||_rememberedSession==session.Id)return;
        try{_profileMemory.Remember(session.SourceProfile,_backend.CliPath,DateTime.UtcNow,true);_rememberedSession=session.Id;}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    private async void BrowseCli_Click(object sender,RoutedEventArgs e)
    {
        if(_busy||_engineRunning||_rescueState.BlocksRouting)return;
        var dialog=new Microsoft.Win32.OpenFileDialog{Title="Укажи WireSock: приложение или командный интерфейс",Filter="Программы WireSock|*.exe",CheckFileExists=true};
        if(dialog.ShowDialog(this)==true)await DiscoverConnectionsAsync(dialog.FileName);
    }
    private async void ImportProfile_Click(object sender,RoutedEventArgs e)
    {
        if(_busy||_engineRunning||_rescueState.BlocksRouting)return;
        if(string.IsNullOrEmpty(_inventory.CliPath)){Notice("Сначала найди установленный WireSock.",true);return;}
        var dialog=new Microsoft.Win32.OpenFileDialog{Title="Импорт своего WireGuard / AmneziaWG профиля",Filter="VPN-профиль|*.conf",CheckFileExists=true,
            InitialDirectory=_inventory.ProfileDirectories.FirstOrDefault(Directory.Exists)??""};
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
            try{_profileMemory.Remember(profile,_inventory.CliPath,DateTime.UtcNow,false);}catch(IOException){}catch(UnauthorizedAccessException){}
            File.Delete(Path.Combine(_root,"data","pending-agent-update.json"));
            Notice("Подключение настроено. Нажми «Подключить», когда будешь готов.");
            _lastService=DateTime.MinValue;await DiscoverConnectionsAsync();
        }
        catch(Win32Exception ex)when(ex.NativeErrorCode==1223){Notice("Запрос Windows отменён.");}
        catch(Exception ex){Notice(Backend.Redact(ex.Message),true);}
        finally{_busy=false;RefreshRescueState();}
    }
}
