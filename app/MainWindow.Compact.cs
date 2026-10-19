using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RoutePilot;

public partial class MainWindow
{
    bool _compact=true,_placing;
    bool _pendingConnectionOperation;
    CancellationTokenSource? _connectionCancellation;
    string _operationPhase="";
    DateTime _phaseStarted;
    bool _lastNoticeError;
    DateTime _lastNoticeAt=DateTime.MinValue;
    int _uiOperationGeneration;
    IProgress<ConnectionProgress> OperationProgress()
    {
        int generation=++_uiOperationGeneration;
        return new Progress<ConnectionProgress>(p=>{if(_busy&&generation==_uiOperationGeneration)SetConnectionStage(p.Phase);});
    }

    void CapturePlacement()
    {
        if(_placing||!IsLoaded)return;
        var bounds=WindowState==WindowState.Normal?new Rect(Left,Top,ActualWidth,ActualHeight):RestoreBounds;
        if(bounds.IsEmpty || !double.IsFinite(bounds.Width))return;
        var value=new WindowPlacement{Left=bounds.Left,Top=bounds.Top,Width=bounds.Width,Height=bounds.Height,Maximized=!_compact&&WindowState==WindowState.Maximized};
        if(_compact)_preferences.CompactWindow=value;else _preferences.AdvancedWindow=value;
    }
    public void ApplyView(bool compact,bool remember=true)
    {
        if(remember)CapturePlacement();
        _placing=true;
        try
        {
            _compact=compact;
            if(remember)_preferences.ViewMode=compact?"Compact":"Advanced";
            AdvancedHost.Visibility=compact?Visibility.Collapsed:Visibility.Visible;
            CompactHost.Visibility=compact?Visibility.Visible:Visibility.Collapsed;
            WindowState=WindowState.Normal;
            var work=SystemParameters.WorkArea;
            MinWidth=Math.Min(compact?380:1120,Math.Max(320,work.Width-24));
            MinHeight=Math.Min(compact?460:760,Math.Max(320,work.Height-24));
            var saved=(compact?_preferences.CompactWindow:_preferences.AdvancedWindow)??new WindowPlacement();
            Width=WindowPlacement.Bounded(saved.Width,compact?420:1360,MinWidth,work.Width-24);
            Height=WindowPlacement.Bounded(saved.Height,compact?490:900,MinHeight,work.Height-24);
            Left=Math.Clamp(saved.Left is {} x && double.IsFinite(x)?x:work.Left+(work.Width-Width)/2,work.Left,Math.Max(work.Left,work.Right-Width));
            Top=Math.Clamp(saved.Top is {} y && double.IsFinite(y)?y:work.Top+(work.Height-Height)/2,work.Top,Math.Max(work.Top,work.Bottom-Height));
            if(!compact&&saved.Maximized)WindowState=WindowState.Maximized;
        }
        finally{_placing=false;}
        SyncCompact();
        if(remember){SaveData();if(!_compact){RefreshPolicyState();TrafficChart.SetPoints(_points);UpdateConnectionGrids();UpdateFeedState();}}
    }
    private void CompactView_Click(object sender,RoutedEventArgs e)=>ApplyView(true);
    private void CompactMenu_Click(object sender,RoutedEventArgs e)
    {
        var menu=new ContextMenu{Background=ConsoleTheme.Brush("#171517"),Foreground=ConsoleTheme.Text};
        void Add(string label,Action action){var item=new MenuItem{Header=label};item.Click+=(_,_)=>action();menu.Items.Add(item);}
        Add("Расширенный вид",()=>ApplyView(false));
        Add("Проверить обновления",async()=>await CheckUpdatesAsync(true));
        menu.Items.Add(new Separator());
        Add("Открыть журнал",()=>{ApplyView(false);PreviewPage(3);});
        Add("Отключить и выйти",async()=>{if(!_busy&&await PerformActionAsync("Stop"))ExitApplication();});
        menu.PlacementTarget=sender as UIElement;menu.IsOpen=true;
    }
    void SetConnectionStage(string phase)
    {
        if(phase!="UpdatingRules")ClearNativeQuality();
        if(_operationPhase!=phase){_phaseStarted=DateTime.UtcNow;_operationPhase=phase;}
        CompactStage.Foreground=ConsoleTheme.Text;
        CompactStage.Text=phase=="Discovering"?"Ищем WireSock и доступные профили…":ConnectionProgress.Label(phase);
        CompactStage.ToolTip=CompactStage.Text;
        if(phase=="Stopping")SetStatus("Отключение…",false);
        else if(phase!="UpdatingRules")SetStatus(phase=="Cancelling"?"Отмена подключения":"Подключение…",false);
        SyncConnectionButtons();
    }
    void SyncConnectionButtons()
    {
        string label=_busy?(_connectionCancellation!=null?(_connectionCancellation.IsCancellationRequested?"Отменяем…":"Отменить"):"Подожди…"):_engineRunning?"Отключить":"Подключить";
        bool enabled=(!_busy||(_connectionCancellation is {IsCancellationRequested:false}))&&!_rescueState.BlocksRouting&&!_pendingConnectionOperation;
        CompactConnectButton.Content=label;CompactConnectButton.IsEnabled=enabled;
        ConnectButton.Content=label;ConnectButton.IsEnabled=enabled;
        CompactProfileButton.IsEnabled=!_busy&&!_engineRunning&&!_rescueState.BlocksRouting&&!_pendingConnectionOperation;
        bool active=_busy||_updateDownloading||_updateInstalling;
        CompactActivity.Visibility=active?Visibility.Visible:Visibility.Collapsed;
        CompactActivity.IsIndeterminate=active&&IsVisible&&WindowState!=WindowState.Minimized;
    }
    void SyncCompact()
    {
        CompactState.Text=_connectionStatus;
        CompactDot.Fill=StatusLabel.Foreground;
        CompactDownload.Text=DownloadRateLabel.Text;CompactUpload.Text=UploadRateLabel.Text;CompactUptime.Text=UptimeLabel.Text;
        string? profile=ProfileInput.SelectedItem as string;
        CompactProfile.Text=string.IsNullOrWhiteSpace(profile)?"Выбрать профиль подключения":profile;
        CompactProfile.ToolTip=CompactProfile.Text;
        if(_updateInstalling||_updateDownloading){CompactStage.Text=UpdateStatusLabel.Text;CompactStage.Foreground=ConsoleTheme.Text;}
        else if(_busy&&_operationPhase.Length>0)
        {
            if((DateTime.UtcNow-_phaseStarted).TotalSeconds>25)CompactStage.Text=$"{(int)(DateTime.UtcNow-_phaseStarted).TotalSeconds} с · "+(_operationPhase=="WaitingElevation"?"Ожидаем ответ в запросе Windows…":ConnectionProgress.Label(_operationPhase));
        }
        else if(_lastNoticeError || (DateTime.UtcNow-_lastNoticeAt).TotalSeconds<8)
        {CompactStage.Text=NoticeText.Text;CompactStage.Foreground=_lastNoticeError?ConsoleTheme.Warn:ConsoleTheme.Text;}
        else
        {
            CompactStage.Text=_rescueState.BlocksRouting?"Автономная диагностика управляет сетью.":_engineRunning?FooterStatus.Text:"Готово к подключению";
            CompactStage.Foreground=_rescueState.BlocksRouting?ConsoleTheme.Warn:ConsoleTheme.Dim;
        }
        CompactStage.ToolTip=CompactStage.Text;SyncConnectionButtons();
    }
    async Task<ConnectionSelection?> SelectConnectionAsync(bool force,CancellationToken cancellation)
    {
        SetConnectionStage("Discovering");
        await DiscoverConnectionsAsync(_inventory.CliPath);
        cancellation.ThrowIfCancellationRequested();
        while(true)
        {
            string? selected=ProfileInput.SelectedItem as string;
            if(!force && _inventory.CliPath.Length>0 && selected!=null && _inventory.Profiles.Contains(selected))
                return new(_inventory.CliPath,selected,selected==_inventory.SelectedProfile?_inventory.ExpectedEgressIp:"");
            bool missing=_profileMemory.Preferred(_inventory.CliPath,_inventory.SelectedProfile).Length>0 && selected==null;
            var prompt=new ProfilePrompt(_inventory,selected,missing,!force){Owner=this};
            SetConnectionStage("ChoosingProfile");
            if(prompt.ShowDialog()!=true)return null;
            cancellation.ThrowIfCancellationRequested();
            if(prompt.Action=="Select")
            {
                ProfileInput.SelectedItem=prompt.SelectedProfile;SyncCompact();
                return new(_inventory.CliPath,prompt.SelectedProfile,prompt.SelectedProfile==_inventory.SelectedProfile?_inventory.ExpectedEgressIp:"");
            }
            if(prompt.Action=="Browse")
            {
                var dialog=new Microsoft.Win32.OpenFileDialog{Title="WireSock Secure Connect",Filter="WireSock CLI|wiresock-connect-cli.exe",CheckFileExists=true};
                if(dialog.ShowDialog(this)!=true)return null;
                await DiscoverConnectionsAsync(dialog.FileName);
            }
            else if(prompt.Action=="Import")
            {
                var dialog=new Microsoft.Win32.OpenFileDialog{Title="Импорт профиля",Filter="WireGuard / AmneziaWG|*.conf",CheckFileExists=true};
                if(dialog.ShowDialog(this)!=true)return null;
                SetConnectionStage("WaitingElevation");
                await new ConnectionSetup(_root,_backend).Apply("Import",_inventory.CliPath,import:dialog.FileName);
                await DiscoverConnectionsAsync(_inventory.CliPath);
                ProfileInput.SelectedItem=Path.GetFileNameWithoutExtension(dialog.FileName);
            }
            cancellation.ThrowIfCancellationRequested();force=false;
        }
    }
    sealed class AppliedConnection
    {
        public string CliPath{get;set;}="";public string SourceProfile{get;set;}="";public string ExpectedEgressIp{get;set;}="";
        public string WireSockCliPath{get;set;}="";public string WireSockSourceProfile{get;set;}="";public string DataDirectory{get;set;}="";public string AppRoot{get;set;}="";
    }
    bool NeedsConnectionSetup(ConnectionSelection selection)
    {
        if(!_rescue.Installed || File.Exists(Path.Combine(_root,"data","pending-agent-update.json")))return true;
        string agentExe=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RoutePilot","Agent","RoutePilot.Agent.exe");
        try
        {
            if(!Version.TryParse(System.Diagnostics.FileVersionInfo.GetVersionInfo(agentExe).FileVersion,out var installed))return true;
            int compare=ReleaseUpdates.Compare(installed,ReleaseUpdates.Current);
            if(compare<0)return true;
            if(compare>0)throw new IOException("Установленная служба новее этой версии. Сначала обнови CITA Route.");
        }
        catch(System.IO.FileNotFoundException){return true;}
        var local=AppPaths.Read(Path.Combine(_root,"data","connection.json"),()=>new AppliedConnection());
        var agent=AppPaths.Read(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RoutePilot","Agent","agent-config.json"),()=>new AppliedConnection());
        return !ConnectionBindingMatches(local,agent,selection,_root,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RoutePilot"));
    }
    static bool ConnectionBindingMatches(AppliedConnection local,AppliedConnection agent,ConnectionSelection selection,string root,string dataRoot)=>
        local.SourceProfile==selection.Profile && string.Equals(local.CliPath,selection.Cli,StringComparison.OrdinalIgnoreCase) && local.ExpectedEgressIp==selection.Egress &&
        agent.WireSockSourceProfile==selection.Profile && string.Equals(agent.WireSockCliPath,selection.Cli,StringComparison.OrdinalIgnoreCase) && agent.ExpectedEgressIp==selection.Egress &&
        string.Equals(Path.TrimEndingDirectorySeparator(agent.DataDirectory),Path.TrimEndingDirectorySeparator(dataRoot),StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.TrimEndingDirectorySeparator(agent.AppRoot),Path.TrimEndingDirectorySeparator(root),StringComparison.OrdinalIgnoreCase);
    private async void ChooseCompactProfile_Click(object sender,RoutedEventArgs e)
    {
        if(_render||_busy||_engineRunning||_rescueState.BlocksRouting)return;
        _busy=true;_connectionCancellation=new();
        try
        {
            var selected=await SelectConnectionAsync(true,_connectionCancellation.Token);
            if(selected!=null){_lastNoticeError=false;Notice("Профиль выбран. Можно подключаться.");}
        }
        catch(OperationCanceledException){Notice("Выбор отменён.");}
        catch(Exception ex){Notice(Backend.FriendlyError(ex.Message),true);}
        finally{_connectionCancellation.Dispose();_connectionCancellation=null;_busy=false;_operationPhase="";++_uiOperationGeneration;await RefreshAsync();SyncCompact();}
    }
    private async Task ToggleAsync()
    {
        if(_render)return;
        if(_pendingConnectionOperation || ConnectionOperation.Active(_root)){Notice("Предыдущая операция ещё завершается. Подробности — в журнале.",true);return;}
        if(_busy){_connectionCancellation?.Cancel();if(_connectionCancellation!=null)SetConnectionStage("Cancelling");return;}
        if(_engineRunning){await PerformActionAsync("Stop");return;}
        if(_rescueState.BlocksRouting){Notice("Дождись завершения автономной диагностики и восстановления.",true);return;}
        _busy=true;_lastNoticeError=false;_connectionCancellation=new();SyncConnectionButtons();
        try
        {
            var selection=await SelectConnectionAsync(false,_connectionCancellation.Token);
            if(selection==null){Notice("Подключение отменено.");return;}
            bool setup=NeedsConnectionSetup(selection);
            var progress=OperationProgress();
            var result=await ConnectionOperation.Run(_root,"Start",true,setup?selection:null,progress,_connectionCancellation.Token);
            _pendingConnectionOperation=result.Pending;
            if(result.Success&&_connectionCancellation.IsCancellationRequested)
            {
                SetConnectionStage("Cancelling");
                var cleanup=await ConnectionOperation.Run(_root,"Stop",true);
                result.Success=false;result.Cancelled=cleanup.Success;
                result.Message=cleanup.Success?"":"Подключение успело завершиться. Нажми «Отключить», чтобы остановить маршрут.";
            }
            if(result.Applied)
            {
                _profileMemory.Remember(selection.Profile,selection.Cli,DateTime.UtcNow,false);
                File.Delete(Path.Combine(_root,"data","pending-agent-update.json"));
            }
            string message=result.Success?"Подключено. VPN-выход проверен.":result.Cancelled?"Подключение отменено.":Backend.FriendlyError(result.Message);
            Notice(message,!result.Success&&!result.Cancelled);Event(message);_lastService=DateTime.MinValue;
        }
        catch(OperationCanceledException){Notice("Подключение отменено.");}
        catch(Win32Exception ex)when(ex.NativeErrorCode==1223){Notice("Запрос Windows отменён.");}
        catch(Exception ex){Notice(Backend.FriendlyError(ex.Message),true);Event(ex.Message);}
        finally
        {
            _connectionCancellation.Dispose();_connectionCancellation=null;_busy=false;_operationPhase="";++_uiOperationGeneration;
            await RefreshAsync();SyncCompact();
        }
    }
}
