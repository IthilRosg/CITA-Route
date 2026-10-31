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
    Button _nativeSetupButton=new();
    readonly TextBlock _nativeRoutingStats=new(){Text="Статистика проверок появится после подключения.",TextWrapping=TextWrapping.Wrap};
    bool _nativeModeChanging;DateTime _nativeReadUtc;DateTime _nativeRetryUtc;Border? _nativeWireSockCard;
    bool UseNativeBackend=>ClientEdition.NativeOnly||_nativeChoice.Selected||_nativeView?.Desired==true||_nativeView?.Phase=="Importing";
    string NativeChoicePath=>Path.Combine(_root,"data","transport-choice.json");
    void InitializeNativeUi()
    {
        _nativeChoice=AppPaths.Read(NativeChoicePath,()=>new NativeChoice());
        if(ClientEdition.NativeOnly)_nativeChoice.Selected=true;
        var wireSockCard=((FrameworkElement)CliPathLabel.Parent).Parent as Border;_nativeWireSockCard=wireSockCard;
        if(wireSockCard?.Parent is not StackPanel settings)return;
        if(ClientEdition.NativeOnly)ConfigureStandaloneSettings(settings);
        var content=new StackPanel();content.Children.Add(new TextBlock{Text=ClientEdition.NativeOnly?"Подключение · WireGuard / AmneziaWG":"Способ подключения",FontSize=18,FontWeight=FontWeights.SemiBold});
        _nativeMode.Margin=new Thickness(0,12,0,0);_nativeMode.Items.Add(new ComboBoxItem{Content="Авто · WireSock"});_nativeItem=new ComboBoxItem{Content="Встроенный Авто · предварительный",IsEnabled=false};_nativeMode.Items.Add(_nativeItem);_nativeMode.SelectedIndex=_nativeChoice.Selected?1:0;
        _nativeMode.SelectionChanged+=async(_,_)=>
        {
            if(_nativeModeChanging)return;
            if(_busy||_engineRunning||_nativeView?.Desired==true){_nativeModeChanging=true;_nativeMode.SelectedIndex=_nativeChoice.Selected?1:0;_nativeModeChanging=false;Notice("Сначала отключи текущее подключение.");return;}
            _nativeChoice.Selected=_nativeMode.SelectedIndex==1;AppPaths.Write(NativeChoicePath,_nativeChoice);_nativeReadUtc=DateTime.MinValue;await DiscoverConnectionsAsync();await RefreshAsync();
        };
        if(!ClientEdition.NativeOnly)content.Children.Add(_nativeMode);_nativeHint.TextWrapping=TextWrapping.Wrap;_nativeHint.FontSize=12;_nativeHint.Foreground=ConsoleTheme.Dim;_nativeHint.Margin=new Thickness(0,10,0,0);content.Children.Add(_nativeHint);
        _nativeImportButton.Content="Импорт .conf во встроенный режим";_nativeImportButton.Margin=new Thickness(0,12,0,0);_nativeImportButton.HorizontalAlignment=HorizontalAlignment.Left;_nativeImportButton.IsEnabled=ClientEdition.NativeOnly;_nativeImportButton.Click+=async(_,_)=>await ImportNativeProfileAsync();content.Children.Add(_nativeImportButton);
        _nativeSetupButton.Content="Подготовить встроенный модуль";_nativeSetupButton.Margin=new Thickness(0,10,0,0);_nativeSetupButton.HorizontalAlignment=HorizontalAlignment.Left;_nativeSetupButton.Click+=async(_,_)=>await PrepareNativeComponentsAsync();content.Children.Add(_nativeSetupButton);
        settings.Children.Insert(0,new Border{Style=(Style)FindResource("Card"),Margin=new Thickness(0,0,0,16),Child=content});
    }
    async Task<bool> RefreshNativeAsync(bool force=false)
    {
        if(_render)return false;
        UpdateNativeImportAvailability();
        if(!_rescue.Installed&&!UseNativeBackend)return false;
        if(force||DateTime.UtcNow>=_nativeRetryUtc&&(DateTime.UtcNow-_nativeReadUtc).TotalSeconds>=1)
        {
            _nativeReadUtc=DateTime.UtcNow;
            try{var reply=await _nativeClient.Command("status");_nativeView=reply.Native;_nativeRetryUtc=DateTime.MinValue;}
            catch{_nativeRetryUtc=DateTime.UtcNow.AddSeconds(10);_nativeItem.IsEnabled=false;if(!UseNativeBackend)return false;ApplyNativeView(null);return true;}
        }
        if(DateTime.UtcNow<_nativeRetryUtc){if(!UseNativeBackend)return false;ApplyNativeView(null);return true;}
        _nativeItem.IsEnabled=_nativeView?.Available==true;
        _nativeSetupButton.IsEnabled=!_busy&&!_discovering&&!_engineRunning;_nativeSetupButton.Visibility=Visibility.Visible;_nativeSetupButton.Content=_nativeView?.Available==true?"Обновить встроенный модуль":"Подготовить встроенный модуль";
        UpdateNativeImportAvailability();
        SetNativeQualityCaption(UseNativeBackend);
        if(_nativeWireSockCard!=null)_nativeWireSockCard.Visibility=UseNativeBackend?Visibility.Collapsed:Visibility.Visible;
        _nativeMode.IsEnabled=!ClientEdition.NativeOnly&&!_busy&&!_discovering&&!_engineRunning&&_nativeView?.Desired!=true;
        _nativeHint.Text=_nativeView?.Available==true?"Импортируй свой IPv4-профиль с одним сервером. Новые HTTPS-домены сначала используют VPN, затем проверяется прямой путь. IPv6 и команды запуска из профиля пока не поддерживаются.":"Нажми «Подключить»: CITA подготовит собственный движок и предложит импортировать .conf. Отдельный VPN-клиент не требуется.";
        if(!UseNativeBackend)return false;
        if((_nativeView?.Desired==true||_nativeView?.Phase=="Importing")&&!_nativeChoice.Selected){_nativeChoice.Selected=true;AppPaths.Write(NativeChoicePath,_nativeChoice);_nativeModeChanging=true;_nativeMode.SelectedIndex=1;_nativeModeChanging=false;}
        ApplyNativeView(_nativeView);
        if(ClientEdition.NativeOnly)
        {
            _nativeTraceToggle.IsEnabled=_nativeView?.Connected==true||(_nativeTraceToggle.IsChecked==true&&_nativeView?.Desired==true);
            if(_nativeView?.Desired!=true){_nativeTraceToggle.IsChecked=false;_nativeTraceText.Text="";_nativeTraceText.Visibility=Visibility.Collapsed;}
            else if(_nativeTraceToggle.IsChecked==true&&_nativeView.Connected&&DateTime.UtcNow>=_nativeTraceNext)await RefreshNativeTraceAsync();
        }
        return true;
    }
    internal void ApplyNativeView(NativeRouteView? state)
    {
        if(_nativeSetupInstalling){_engineRunning=false;_engineAvailable=false;_vpnConnected=false;SetStatus("Устанавливаем модуль",false);FooterStatus.Text="Применяем обновление и проверяем компоненты…";SyncCompact();return;}
        bool previousDesired=_nativeView?.Desired==true||_nativeView?.Phase=="Importing";if(state!=null)_nativeView=state;_engineRunning=state!=null?state.Desired||state.Phase=="Importing":previousDesired;_engineAvailable=state?.Available==true;_vpnConnected=state?.Connected==true;
        SetStatus(NativeRoutePresentation.Title(state),state?.Connected==true);FooterStatus.Text=NativeRoutePresentation.Detail(state);
        if(ClientEdition.NativeOnly){var r=state?.Runtime;_nativeRoutingStats.Text=r==null?"Статистика проверок появится после подключения.":$"За эту сессию: проверок прямого пути — {r.CalibrationChecks}, подтверждено — {r.CalibrationPassed}, не подтверждено — {r.CalibrationFailed}, ошибок проверки — {r.CalibrationErrors}. Активных прямых решений — {r.CalibratedDomains}. Прямых соединений с последнего запуска двигателя — {r.PolicyDirectDials}.\nСреди недавно замеченных HTTPS-направлений: {r.ObservedDestinations}, приложение определено для {r.IdentifiedDestinations}.";}
        if(ClientEdition.NativeOnly)ProfileLabel.Text=string.IsNullOrWhiteSpace(state?.ProfileName)?"Профиль: выбери .conf":"Профиль: "+state.ProfileName;
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
        if(!_updateInstalling&&!_updateDownloading&&!_busy&&!_lastNoticeError&&(DateTime.UtcNow-_lastNoticeAt).TotalSeconds>=8){CompactStage.Text=FooterStatus.Text;CompactStage.ToolTip=FooterStatus.Text;}
    }
    void SetNativeQualityCaption(bool native)
    {
        native|=ClientEdition.NativeOnly;
        if(CompactVpnRtt.Parent is Grid panel&&panel.Parent is StackPanel content&&content.Children.Count>0&&content.Children[0] is TextBlock label)label.Text=native?"ТУННЕЛЬ VPN / ВСТРОЕННЫЙ":"ТУННЕЛЬ VPN / WIRESOCK";
    }
    bool ConfirmNativeProfile(bool force=false)
    {
        var state=_nativeView;if(state?.Available!=true){Notice("Сначала подготовь встроенный модуль и импортируй профиль .conf.",true);return false;}
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
        if(start&&_nativeView?.Phase=="Unconfigured")
        {if(startOnly){Notice("Автоподключение: сначала импортируй профиль встроенного режима.");return false;}await ImportNativeProfileAsync();if(_nativeView?.Phase!="Disconnected"||string.IsNullOrEmpty(_nativeView.ProfileId))return false;}
        if(_render)return false;if(_busy){if(!startOnly)_connectionCancellation?.Cancel();return false;}
        _busy=true;_connectionCancellation=new();SyncConnectionButtons();string? operation=null;string? connectionId=null;string? stoppingId=null;string? startOperation=null;bool requestedStart=false;
        try
        {
            var state=(await _nativeClient.Command("status",cancellation:_connectionCancellation.Token)).Native;if(state==null||!state.Available)throw new IOException("Встроенный режим не подготовлен.");_nativeView=state;
            if(startOnly&&state.Phase=="Unconfigured"){Notice("Автоподключение: сначала импортируй профиль встроенного режима.");return false;}
            if(start&&state.Phase=="Stopping"&&!state.Desired){using var cleanup=CancellationTokenSource.CreateLinkedTokenSource(_connectionCancellation.Token);cleanup.CancelAfter(TimeSpan.FromSeconds(35));state=await _nativeClient.Wait(state.Operation,false,new Progress<NativeRouteView>(ApplyNativeView),cleanup.Token);_nativeView=state;}
            if(startOnly&&(state.Desired||state.Phase=="Importing"))return true;
            if(state.Phase=="Importing")start=false;
            if(start&&!state.Desired)
            {
                if(startOnly&&!NativeLaunchPolicy.ConfirmedProfileMatches(_nativeChoice.ConfirmedProfile,state.ProfileId)){Notice("Автоподключение: подтверди выбранный профиль кнопкой «Подключить».",true);return false;}
                if(!startOnly&&!ConfirmNativeProfile())return false;
            }
            NativeControlReply reply;
            if(start)
            {
                var capabilities=await _nativeClient.Command("native-capabilities",cancellation:_connectionCancellation.Token);
                if(!NativeConnectionPolicy.SupportsProtocol(capabilities))throw new IOException("Обнови встроенный модуль CITA Route: установленная служба не поддерживает безопасное подключение.");
                connectionId=Guid.NewGuid().ToString("N");startOperation=state.Operation;
                if(startOnly)NativeLaunchRequest.Save(_root,state,connectionId);
                requestedStart=true;
                reply=await _nativeClient.StartConnection(state.Operation,connectionId,_connectionCancellation.Token);
                if(!reply.Success||reply.Native==null)throw new IOException("Служба не приняла запрос. Проверь журнал подключения.");
                NativeConnectionPolicy.RequireOwned(reply.Native,connectionId);
                if(!string.Equals(reply.Native.ProfileId,state.ProfileId,StringComparison.Ordinal))throw new IOException("Выбранный профиль изменился. Проверь подключение.");
            }
            else if(NativeConnectionPolicy.StopIdentity(state) is string ownStopId)
            {
                stoppingId=ownStopId;reply=await _nativeClient.StopConnection(stoppingId,_connectionCancellation.Token);
            }
            else reply=await _nativeClient.Command("native-stop",state.Operation,_connectionCancellation.Token);
            if(!reply.Success||reply.Native==null)throw new IOException("Служба не приняла запрос. Проверь журнал подключения.");operation=reply.Native.Operation;
            using var limit=CancellationTokenSource.CreateLinkedTokenSource(_connectionCancellation.Token);limit.CancelAfter(TimeSpan.FromSeconds(60));
            if(start)await _nativeClient.WaitConnection(connectionId!,true,new Progress<NativeRouteView>(ApplyNativeView),limit.Token);
            else if(stoppingId!=null)await _nativeClient.WaitConnection(stoppingId,false,new Progress<NativeRouteView>(ApplyNativeView),limit.Token);
            else await _nativeClient.Wait(operation,false,new Progress<NativeRouteView>(ApplyNativeView),limit.Token);
            Notice(start?"Подключено. Выход VPN проверен.":"Подключение отключено.");return true;
        }
        catch(Exception ex)
        {
            bool cleanupConfirmed=!requestedStart;
            if(requestedStart&&connectionId!=null)try{
                using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(25));
                cleanupConfirmed=await NativeConnectionPolicy.CleanupOwnAsync(connectionId,startOperation??"",
                    ()=>_nativeClient.Command("status",cancellation:cleanup.Token),
                    id=>_nativeClient.StopConnection(id,cleanup.Token),
                    async id=>{await _nativeClient.WaitConnection(id,false,new Progress<NativeRouteView>(ApplyNativeView),cleanup.Token);});
            }catch{}
            if(!cleanupConfirmed){Notice("Не удалось подтвердить остановку. Проверь состояние службы; защищённые адреса остаются закрыты.",true);return false;}
            Notice(ex is OperationCanceledException?"Операция отменена. Проверяем завершение подключения…":Backend.FriendlyError(ex.Message),ex is not OperationCanceledException);return false;
        }
        finally{_connectionCancellation.Dispose();_connectionCancellation=null;_busy=false;_nativeReadUtc=DateTime.MinValue;await RefreshAsync();SyncConnectionButtons();}
    }
}
