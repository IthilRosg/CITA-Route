using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace RoutePilot;

public partial class MainWindow
{
    sealed class NativeChoice{public bool Selected{get;set;}public string ConfirmedProfile{get;set;}="";}
    readonly NativeRouteClient _nativeClient=new();
    NativeChoice _nativeChoice=new();NativeRouteView? _nativeView;
    ComboBox _nativeMode=new();ComboBoxItem _nativeItem=new();TextBlock _nativeHint=new();
    Button _nativeImportButton=new();
    bool _nativeModeChanging;DateTime _nativeReadUtc;DateTime _nativeRetryUtc;Border? _nativeWireSockCard;
    bool UseNativeBackend=>_nativeChoice.Selected||_nativeView?.Desired==true||_nativeView?.Phase=="Importing";
    string NativeChoicePath=>Path.Combine(_root,"data","transport-choice.json");
    void InitializeNativeUi()
    {
        _nativeChoice=AppPaths.Read(NativeChoicePath,()=>new NativeChoice());
        var wireSockCard=((FrameworkElement)CliPathLabel.Parent).Parent as Border;_nativeWireSockCard=wireSockCard;
        if(wireSockCard?.Parent is not StackPanel settings)return;
        var content=new StackPanel();content.Children.Add(new TextBlock{Text="Способ подключения",FontSize=18,FontWeight=FontWeights.SemiBold});
        _nativeMode.Margin=new Thickness(0,12,0,0);_nativeMode.Items.Add(new ComboBoxItem{Content="Авто · WireSock"});_nativeItem=new ComboBoxItem{Content="Встроенный VPN · предварительный",IsEnabled=false};_nativeMode.Items.Add(_nativeItem);_nativeMode.SelectedIndex=_nativeChoice.Selected?1:0;
        _nativeMode.SelectionChanged+=async(_,_)=>
        {
            if(_nativeModeChanging)return;
            if(_busy||_engineRunning||_nativeView?.Desired==true){_nativeModeChanging=true;_nativeMode.SelectedIndex=_nativeChoice.Selected?1:0;_nativeModeChanging=false;Notice("Сначала отключи текущее подключение.");return;}
            _nativeChoice.Selected=_nativeMode.SelectedIndex==1;AppPaths.Write(NativeChoicePath,_nativeChoice);_nativeReadUtc=DateTime.MinValue;await DiscoverConnectionsAsync();await RefreshAsync();
        };
        content.Children.Add(_nativeMode);_nativeHint.TextWrapping=TextWrapping.Wrap;_nativeHint.FontSize=12;_nativeHint.Foreground=ConsoleTheme.Dim;_nativeHint.Margin=new Thickness(0,10,0,0);content.Children.Add(_nativeHint);
        _nativeImportButton.Content="Импорт .conf во встроенный режим";_nativeImportButton.Margin=new Thickness(0,12,0,0);_nativeImportButton.HorizontalAlignment=HorizontalAlignment.Left;_nativeImportButton.IsEnabled=false;_nativeImportButton.Click+=async(_,_)=>await ImportNativeProfileAsync();content.Children.Add(_nativeImportButton);
        settings.Children.Insert(0,new Border{Style=(Style)FindResource("Card"),Margin=new Thickness(0,0,0,16),Child=content});
    }
    async Task<bool> RefreshNativeAsync(bool force=false)
    {
        if(_render)return false;
        if(!_rescue.Installed&&!UseNativeBackend)return false;
        if(force||DateTime.UtcNow>=_nativeRetryUtc&&(DateTime.UtcNow-_nativeReadUtc).TotalSeconds>=1)
        {
            _nativeReadUtc=DateTime.UtcNow;
            try{var reply=await _nativeClient.Command("status");_nativeView=reply.Native;_nativeRetryUtc=DateTime.MinValue;}
            catch{_nativeRetryUtc=DateTime.UtcNow.AddSeconds(10);_nativeItem.IsEnabled=false;if(!UseNativeBackend)return false;ApplyNativeView(null);return true;}
        }
        if(DateTime.UtcNow<_nativeRetryUtc){if(!UseNativeBackend)return false;ApplyNativeView(null);return true;}
        _nativeItem.IsEnabled=_nativeView?.Available==true;
        _nativeImportButton.IsEnabled=_nativeView?.Phase=="Disconnected"&&!_busy&&!_discovering&&!_engineRunning;
        SetNativeQualityCaption(UseNativeBackend);
        if(_nativeWireSockCard!=null)_nativeWireSockCard.Visibility=UseNativeBackend?Visibility.Collapsed:Visibility.Visible;
        _nativeMode.IsEnabled=!_busy&&!_discovering&&!_engineRunning&&_nativeView?.Desired!=true;
        _nativeHint.Text=_nativeView?.Available==true?"Можно импортировать собственный IPv4-профиль с одним сервером. IPv6 в конфиге и команды запуска пока не поддерживаются. Живые списки «Авто» доступны через WireSock.":"Встроенный режим появится после подготовки его компонентов и профиля. Сейчас доступен «Авто» через WireSock.";
        if(!UseNativeBackend)return false;
        if((_nativeView?.Desired==true||_nativeView?.Phase=="Importing")&&!_nativeChoice.Selected){_nativeChoice.Selected=true;AppPaths.Write(NativeChoicePath,_nativeChoice);_nativeModeChanging=true;_nativeMode.SelectedIndex=1;_nativeModeChanging=false;}
        ApplyNativeView(_nativeView);return true;
    }
    internal void ApplyNativeView(NativeRouteView? state)
    {
        bool previousDesired=_nativeView?.Desired==true||_nativeView?.Phase=="Importing";if(state!=null)_nativeView=state;_engineRunning=state!=null?state.Desired||state.Phase=="Importing":previousDesired;_engineAvailable=state?.Available==true;_vpnConnected=state?.Connected==true;
        SetStatus(NativeRoutePresentation.Title(state),state?.Connected==true);FooterStatus.Text=NativeRoutePresentation.Detail(state);
        DownloadRateLabel.Text=UploadRateLabel.Text=ConnectionCountLabel.Text=VpnCountLabel.Text=DirectCountLabel.Text=UptimeLabel.Text="—";VpnShareBar.Value=0;
        CompactVpnRtt.Text="Задержка: —";CompactVpnLoss.Text="Потери: —";
        SyncCompact();
        if(_trayToggle!=null){_trayToggle.Text=_engineRunning?"Отключить":"Подключить";_trayToggle.Enabled=!_busy;}
        if(_tray!=null)_tray.Text="CITA Route · "+_connectionStatus.ToLowerInvariant();
    }
    void SyncNativeCompact()
    {
        if(!UseNativeBackend)return;
        CompactTraffic.Text="—";SetNativeQualityCaption(true);
        CompactProfile.Text=_nativeView?.ProfileName??"Профиль встроенного VPN";CompactProfile.ToolTip=CompactProfile.Text;
        if(!_updateInstalling&&!_updateDownloading&&!_lastNoticeError){CompactStage.Text=FooterStatus.Text;CompactStage.ToolTip=FooterStatus.Text;}
    }
    void SetNativeQualityCaption(bool native)
    {
        if(CompactVpnRtt.Parent is Grid panel&&panel.Parent is StackPanel content&&content.Children.Count>0&&content.Children[0] is TextBlock label)label.Text=native?"ТУННЕЛЬ VPN / ВСТРОЕННЫЙ":"ТУННЕЛЬ VPN / WIRESOCK";
    }
    bool ConfirmNativeProfile(bool force=false)
    {
        var state=_nativeView;if(state?.Available!=true){Notice("Встроенный профиль ещё не подготовлен. Выбери «Авто · WireSock» или дождись настройки встроенного режима.",true);return false;}
        string id=state.ProfileId.Length>0?state.ProfileId:state.ProfileName;
        if(!force&&_nativeChoice.ConfirmedProfile==id)return true;
        var window=new Window{Title="Профиль встроенного VPN",Owner=this,Width=360,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=ConsoleTheme.Brush("#101010"),Foreground=ConsoleTheme.Text};
        var body=new StackPanel{Margin=new Thickness(20)};body.Children.Add(new TextBlock{Text="Выбери профиль",FontSize=20});var choice=new ComboBox{Margin=new Thickness(0,16,0,12),ItemsSource=new[]{state.ProfileName},SelectedIndex=0};body.Children.Add(choice);
        body.Children.Add(new TextBlock{Text=NativeRoutePresentation.Detail(withPhase()),TextWrapping=TextWrapping.Wrap,Foreground=ConsoleTheme.Dim,Margin=new Thickness(0,0,0,14)});
        var accept=new Button{Content="Использовать профиль",Padding=new Thickness(12,9,12,9)};accept.Click+=(_,_)=>window.DialogResult=true;body.Children.Add(accept);window.Content=body;
        if(window.ShowDialog()!=true)return false;_nativeChoice.ConfirmedProfile=id;AppPaths.Write(NativeChoicePath,_nativeChoice);return true;
        NativeRouteView withPhase()=>new(){Phase="Connected",PolicyMode=state.PolicyMode};
    }
    async Task<bool> NativeActionAsync(bool start,bool startOnly=false)
    {
        if(_render)return false;if(_busy){_connectionCancellation?.Cancel();return false;}
        _busy=true;_connectionCancellation=new();SyncConnectionButtons();string? operation=null;bool requestedStart=false;
        try
        {
            var state=(await _nativeClient.Command("status")).Native;if(state==null||!state.Available)throw new IOException("Встроенный режим не подготовлен.");_nativeView=state;
            if(startOnly&&(state.Desired||state.Phase=="Importing"))return true;
            if(state.Phase=="Importing")start=false;
            if(start&&!state.Desired&&!ConfirmNativeProfile())return false;
            var reply=await _nativeClient.Command(start?"native-start":"native-stop",start?null:state.Operation);
            if(!reply.Success||reply.Native==null)throw new IOException("Служба не приняла запрос. Проверь журнал подключения.");operation=reply.Native.Operation;requestedStart=start;
            using var limit=CancellationTokenSource.CreateLinkedTokenSource(_connectionCancellation.Token);limit.CancelAfter(TimeSpan.FromSeconds(60));
            await _nativeClient.Wait(operation,start,new Progress<NativeRouteView>(ApplyNativeView),limit.Token);Notice(start?"Подключено. Выход VPN проверен.":"Подключение отключено.");return true;
        }
        catch(Exception ex)
        {
            bool cleanupConfirmed=!requestedStart;
            if(requestedStart&&operation!=null)try{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(25));var stopped=await _nativeClient.Command("native-stop",operation,cleanup.Token);if(stopped.Success){await _nativeClient.Wait(operation,false,new Progress<NativeRouteView>(ApplyNativeView),cleanup.Token);cleanupConfirmed=true;}}catch{}
            if(!cleanupConfirmed){Notice("Не удалось подтвердить остановку. Проверь состояние службы; защищённые адреса остаются закрыты.",true);return false;}
            Notice(ex is OperationCanceledException?"Операция отменена. Проверяем завершение подключения…":Backend.FriendlyError(ex.Message),ex is not OperationCanceledException);return false;
        }
        finally{_connectionCancellation.Dispose();_connectionCancellation=null;_busy=false;_nativeReadUtc=DateTime.MinValue;await RefreshAsync();SyncConnectionButtons();}
    }
}
