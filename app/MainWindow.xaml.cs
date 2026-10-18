using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms=System.Windows.Forms;

namespace RoutePilot;

public partial class MainWindow : Window
{
    private readonly string _root;
    private readonly Backend _backend;
    private readonly bool _render;
    private readonly Preferences _preferences;
    private readonly TrafficHistory _history;
    private readonly DispatcherTimer _timer=new();
    private readonly List<RatePoint> _points=new();
    private List<ConnectionRow> _connections=new();
    private List<UserRule> _rules=new();
    private readonly List<string> _events=new();
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _trayToggle;
    private bool _ready,_refreshing,_busy,_engineAvailable,_engineRunning,_bridgeReady,_vpnConnected,_exiting,_suppressTelemetry;
    private string _lastEpoch="";
    private DateTime _lastAt=DateTime.MinValue,_lastService=DateTime.MinValue,_lastSave=DateTime.MinValue;
    private string _serviceStatus="Проверяю состояние WireSock…";
    private long _lastUpload,_lastDownload;
    private RescueClient _rescue=null!;
    private RescueState _rescueState=new();
    private Task<(bool Verified,string Message)>? _egressCheck;
    private string _egressEpoch="",_egressMessage="VPN-выход ещё не проверен.";
    private bool _egressVerified;
    private DateTime _lastEgress=DateTime.MinValue;
    private RoutingPolicyPreferences _routingPolicy=new();
    public bool IsRefreshing=>_refreshing||_discovering;
    public void PreviewPage(int index)
    {
        RadioButton[] buttons={NavOverview,NavConnections,NavRules,NavLogs,NavSettings};buttons[index].IsChecked=true;
        Pages.SelectedIndex=index;UpdateLayout();
    }
    public MainWindow(string root,bool render)
    {
        _root=root;_render=render;_backend=new Backend(root);
        _rescue=new RescueClient(root);
        Directory.CreateDirectory(Path.Combine(root,"data"));Directory.CreateDirectory(Path.Combine(root,"logs"));
        _preferences=AppPaths.Read(Path.Combine(root,"data","preferences.json"),()=>new Preferences());
        _routingPolicy=AppPaths.Read(Path.Combine(root,"data","routing-policy.json"),()=>new RoutingPolicyPreferences());
        _history=AppPaths.Read(Path.Combine(root,"data","traffic.json"),()=>new TrafficHistory());
        InitializeComponent();
        _preferences.RefreshSeconds=Math.Clamp(_preferences.RefreshSeconds,1,3);
        CloseToTrayBox.IsChecked=_preferences.CloseToTray;RefreshIntervalInput.SelectedIndex=_preferences.RefreshSeconds-1;
        RoutingModeInput.SelectedIndex=_routingPolicy.Mode=="Auto"?1:0;AllowListedDpiBox.IsChecked=_routingPolicy.AllowListedDpi;
        _rules=DomainRules.Read(Path.Combine(root,"force-direct.txt")).Select(d=>new UserRule{Domain=d,Target="Напрямую"}).Concat(
            DomainRules.Read(Path.Combine(root,"force-vpn.txt")).Select(d=>new UserRule{Domain=d,Target="VPN"})).ToList();
        UpdateRulesGrid();UpdateHistoryLabels();
        _ready=true;
        Pages.SelectedIndex=0;
        if(!render)CreateTray();
        _timer.Interval=TimeSpan.FromSeconds(_preferences.RefreshSeconds);
        _timer.Tick+=async(_,_)=>await RefreshAsync();
        _timer.Tick+=async(_,_)=>{if((DateTime.UtcNow-_lastUpdateCheck).TotalHours>=6)await CheckUpdatesAsync();};
        Loaded+=async(_,_)=>{await DiscoverConnectionsAsync();if(!render)_timer.Start();await RefreshAsync();if(!render)_=CheckUpdatesAsync();};
        Closing+=OnClosing;
        NavOverview.IsChecked=true;
        if(NetworkInterface.GetAllNetworkInterfaces().Any(n=>n.Name=="VPNUS" && n.OperationalStatus==OperationalStatus.Up))
            Notice("VPNUS работает. Кнопка «Проверить» запустит автономный тест и автоматически вернёт VPNUS после проверки.");
    }
    private void CreateTray()
    {
        var menu=new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть CITA Route",null,(_,_)=>Dispatcher.Invoke(ShowWindow));
        _trayToggle=new Forms.ToolStripMenuItem("Подключить");
        _trayToggle.Click+=(_,_)=>Dispatcher.Invoke(async()=>await ToggleAsync());menu.Items.Add(_trayToggle);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Отключить и выйти",null,(_,_)=>Dispatcher.Invoke(async()=>{if(await PerformActionAsync("Stop"))ExitApplication();}));
        menu.Items.Add("Выйти, оставить маршрут",null,(_,_)=>Dispatcher.Invoke(ExitApplication));
        _tray=new Forms.NotifyIcon{Text="CITA Route · отключено",Visible=true,ContextMenuStrip=menu,Icon=System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)??System.Drawing.SystemIcons.Application};
        _tray.DoubleClick+=(_,_)=>Dispatcher.Invoke(ShowWindow);
    }
    public void ShowWindow(){Show();WindowState=WindowState.Normal;Activate();}
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        if(_busy){e.Cancel=true;Notice("Дождись завершения текущей операции.");return;}
        if(!_exiting && !_render && _preferences.CloseToTray){e.Cancel=true;Hide();return;}
        if(!_exiting)ExitApplication();
    }
    public void ExitApplication()
    {
        if(_busy){ShowWindow();Notice("Дождись завершения текущей операции.");return;}
        if(_exiting)return;_exiting=true;_updateLifetime.Cancel();_timer.Stop();SaveData();_tray?.Dispose();_backend.Dispose();Application.Current.Shutdown();
    }
    private void SaveData()
    {
        if(_render)return;
        try{AppPaths.Write(Path.Combine(_root,"data","traffic.json"),_history);AppPaths.Write(Path.Combine(_root,"data","preferences.json"),_preferences);}
        catch(IOException){FooterStatus.Text="Не удалось сохранить локальную историю.";}
    }
    private void UpdateHistoryLabels()
    {
        var day=_history.Days.TryGetValue(Format.Day,out var value)?value:new ByteCount();
        TodayLabel.Text=Format.Bytes(day.Total);TodayLabel.ToolTip="Загрузка: "+Format.Bytes(day.Download)+" · Отдача: "+Format.Bytes(day.Upload);
        LifetimeLabel.Text="Всего учтено: "+Format.Bytes(_history.TotalUpload+_history.TotalDownload);
    }
    public async Task RefreshAsync()
    {
        if(!_ready || _refreshing || _exiting || _suppressTelemetry)return;
        _refreshing=true;
        try
        {
            if((DateTime.UtcNow-_lastService).TotalSeconds>=5)
            {
                _lastService=DateTime.UtcNow;
                try{_serviceStatus=await _backend.WireSockStatusAsync();}catch{_serviceStatus="Служба WireSock временно недоступна";}
                if(_exiting)return;
                WireSockStatusLabel.Text=_serviceStatus;
                _vpnConnected=_serviceStatus.Contains("SmartRouting-AWG2",StringComparison.OrdinalIgnoreCase) &&
                    (_serviceStatus.Contains("Подключено к серверу",StringComparison.OrdinalIgnoreCase) || _serviceStatus.Contains("Connected to",StringComparison.OrdinalIgnoreCase));
            }
            Snapshot? snapshot=null;
            try{snapshot=await _backend.SnapshotAsync();}catch(Exception ex)when(ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or JsonException){}
            if(_exiting)return;
            var session=AppPaths.Read(Path.Combine(_root,"runtime","session.json"),()=>new EngineSession());
            _engineAvailable=snapshot!=null;
            _engineRunning=_engineAvailable || _backend.EngineRunning(session) || (session.Running && session.Recovering);
            _bridgeReady=session.BridgeReady;
            RefreshRescueState();
            RefreshPolicyState();
            string egressEpoch=session.Id+":"+session.BridgePid;
            if(_egressEpoch!=egressEpoch || !_engineAvailable || !_vpnConnected)
            {_egressVerified=false;_egressMessage="VPN-выход ещё не проверен.";if(_egressEpoch!=egressEpoch){_egressEpoch=egressEpoch;_egressCheck=null;_lastEgress=DateTime.MinValue;}}
            if(_engineAvailable && _vpnConnected && session.BridgeReady && !_rescueState.BlocksRouting)
            {
                if(_egressCheck?.IsCompleted==true)
                {
                    var health=await _egressCheck;_egressVerified=health.Verified;_egressMessage=health.Message;_egressCheck=null;
                }
                if(_egressCheck==null && (DateTime.UtcNow-_lastEgress).TotalSeconds>=30)
                {_lastEgress=DateTime.UtcNow;_egressCheck=_backend.VerifyVpnEgressAsync();}
            }
            bool vpnReady=false;
            if(snapshot!=null)
            {
                vpnReady=_vpnConnected && session.BridgeReady && _egressVerified;
                string epoch=string.IsNullOrWhiteSpace(session.Id)?"unmanaged-"+_history.CreatedUtc.Ticks:session.Id;
                var now=DateTime.UtcNow;
                double elapsed=(now-_lastAt).TotalSeconds;
                double upload=0,download=0;
                if(_lastAt!=DateTime.MinValue && elapsed>0 && _lastEpoch==epoch)
                {upload=Math.Max(0,snapshot.Upload-_lastUpload)/elapsed;download=Math.Max(0,snapshot.Download-_lastDownload)/elapsed;}
                if(!_render && session.Running && !string.IsNullOrWhiteSpace(session.Id))_history.Apply(epoch,snapshot.Upload,snapshot.Download,Format.Day);
                _lastAt=now;_lastEpoch=epoch;_lastUpload=snapshot.Upload;_lastDownload=snapshot.Download;
                _points.Add(new RatePoint(now,upload,download));_points.RemoveAll(p=>(now-p.Time).TotalSeconds>60);
                DownloadRateLabel.Text=Format.Rate(download);UploadRateLabel.Text=Format.Rate(upload);
                _connections=snapshot.Connections.OrderByDescending(c=>c.Download+c.Upload).ToList();
                int vpn=_connections.Count(c=>c.IsVpn),dpi=_connections.Count(c=>c.IsDpi),direct=_connections.Count-vpn-dpi;
                ConnectionCountLabel.Text=_connections.Count.ToString();VpnCountLabel.Text=vpn.ToString();DirectCountLabel.Text=direct.ToString()+(dpi>0?" + "+dpi+" DPI":"");VpnShareBar.Value=_connections.Count==0?0:(double)vpn/_connections.Count;
                UptimeLabel.Text=session.Running?Format.Uptime(session.StartedUtc):"—";
                SetStatus(vpnReady?"VPN проверен":_vpnConnected&&session.BridgeReady?"VPN не проверен":"Восстановление VPN",vpnReady);
                FooterStatus.Text=vpnReady?"HTTPS/IPv4 через VPN проверен · "+Format.Time:_egressMessage;
            }
            else
            {
                _lastAt=DateTime.MinValue;_connections.Clear();
                DownloadRateLabel.Text=UploadRateLabel.Text=ConnectionCountLabel.Text=VpnCountLabel.Text=DirectCountLabel.Text="—";VpnShareBar.Value=0;UptimeLabel.Text="—";
                SetStatus(_engineRunning?"Статистика недоступна":"Отключено",false);
                FooterStatus.Text=_engineRunning?"Маршрутизатор запущен · API статистики недоступен":"Маршрутизатор остановлен · "+Format.Time;
                _points.RemoveAll(p=>(DateTime.UtcNow-p.Time).TotalSeconds>60);
            }
            TrafficChart.SetPoints(_points);UpdateConnectionGrids();UpdateHistoryLabels();UpdateFeedState();
            if(session.Running && session.Recovering){SetStatus("Автовосстановление",false);FooterStatus.Text="Маршрутизатор переподключается автоматически. «Отключить» отменит повторные попытки.";}
            if(_rescueState.Active){SetStatus("Автономный тест",false);FooterStatus.Text="Служба проверяет сеть и контролирует возврат VPNUS. Результаты — в настройках.";}
            if(!_busy){ConnectButton.Content=_engineRunning?"Отключить":"Подключить";ConnectButton.IsEnabled=!_rescueState.BlocksRouting;}
            if(_trayToggle!=null){_trayToggle.Text=_engineRunning?"Отключить":"Подключить";_trayToggle.Enabled=!_busy&&!_rescueState.BlocksRouting;}
            if(_tray!=null)_tray.Text="CITA Route · "+(vpnReady?"подключено":_engineRunning?(_engineAvailable?"восстановление VPN":"нет статистики"):"отключено");
            if((DateTime.UtcNow-_lastSave).TotalSeconds>5){_lastSave=DateTime.UtcNow;SaveData();}
            if(Pages.SelectedIndex==3)ShowLogs();
        }
        finally{_refreshing=false;}
    }
    private void SetStatus(string label,bool connected)
    {
        StatusLabel.Text=label.ToUpperInvariant();SidebarState.Text=label;
        StatusBadge.Background=new SolidColorBrush((Color)ColorConverter.ConvertFromString(connected?"#283D29":"#27343C"));
        StatusLabel.Foreground=new SolidColorBrush((Color)ColorConverter.ConvertFromString(connected?"#C0EF86":"#AFBEC9"));SidebarDot.Fill=StatusLabel.Foreground;
    }
    private void UpdateFeedState()
    {
        string path=Path.Combine(_root,"runtime","feeds-state.json");
        string domains="";
        try{using var rules=JsonDocument.Parse(File.ReadAllText(Path.Combine(_root,"runtime","rules","russia_inside.json")));int count=rules.RootElement.GetProperty("rules").EnumerateArray().Sum(r=>r.TryGetProperty("domain_suffix",out var d)?d.GetArrayLength():0);domains=count+" доменов · ";}catch{}
        try{using var state=JsonDocument.Parse(File.ReadAllText(path));var utc=DateTime.Parse(state.RootElement.GetProperty("updatedUtc").GetString()!).ToUniversalTime();var date=TimeZoneInfo.ConvertTimeFromUtc(utc,TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time"));FeedStateLabel.Text=domains+"Обновлено "+date.ToString("dd.MM HH:mm");}
        catch{FeedStateLabel.Text=domains+"Начальные списки загружены";}
    }
    private void UpdateConnectionGrids()
    {
        string? selected=(ConnectionsGrid.SelectedItem as ConnectionRow)?.Id;
        string query=ConnectionSearch.Text.Trim();
        var filtered=_connections.Where(c=>query.Length==0 || (c.Host+" "+c.Process+" "+c.Route).Contains(query,StringComparison.OrdinalIgnoreCase)).ToList();
        ConnectionsGrid.ItemsSource=filtered;
        if(selected!=null)ConnectionsGrid.SelectedItem=filtered.FirstOrDefault(c=>c.Id==selected);
        RecentGrid.ItemsSource=_connections.Take(4).ToList();EmptyConnectionsLabel.Visibility=_connections.Count==0?Visibility.Visible:Visibility.Collapsed;
    }
    private void UpdateRulesGrid()=>RulesGrid.ItemsSource=_rules.OrderBy(r=>r.Domain).ToList();
    private void Notice(string text,bool error=false)
    {
        NoticeBorder.Visibility=Visibility.Visible;NoticeText.Text=text;
        NoticeBorder.Background=new SolidColorBrush((Color)ColorConverter.ConvertFromString(error?"#352724":"#263026"));
        NoticeBorder.BorderBrush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(error?"#68463B":"#3C4D32"));
    }
    private void Event(string text){_events.Insert(0,Format.Time+"  "+text);if(_events.Count>50)_events.RemoveAt(_events.Count-1);}
    private async Task ToggleAsync(){if(_busy)return;if(_rescueState.BlocksRouting){Notice("Автономный тест управляет сетью. Дождись восстановления.");return;}if(!_engineRunning && (!_rescue.Installed || !File.Exists(Path.Combine(_root,"data","connection.json")) || File.Exists(Path.Combine(_root,"data","pending-agent-update.json")))){PreviewPage(4);await DiscoverConnectionsAsync();Notice("Выбери профиль WireSock и нажми «Применить подключение». Затем можно подключаться.");return;}await PerformActionAsync(_engineRunning?"Stop":"Start");}
    private void RefreshRescueState()
    {
        try{_rescueState=_rescue.ReadState();}
        catch{_rescueState=new RescueState{Phase="RecoveryFailed",LastMessage="Не удалось прочитать состояние службы."};}
        string phase=_rescueState.Phase switch{"Testing"=>"Выполняется тест","Armed"=>"Подготовка теста","Restoring"=>"Восстановление","RecoveryFailed"=>"Требуется восстановление","Restored"=>_rescueState.Passed?"Проверка пройдена":"Тест не пройден · очистка завершена",_=>"Готово к проверке"};
        RescueStatusLabel.Text=_rescue.Installed?_rescueState.TestName+" · "+phase:"Служба не установлена";
        if(_rescueState.Active)
        {var remaining=_rescueState.ReturnDeadlineUtc-DateTime.UtcNow;RescueStatusLabel.Text+=" · резерв возврата до "+Math.Max(0,(int)remaining.TotalSeconds)+" сек.";}
        RescueDetailLabel.Text=string.Join(Environment.NewLine,_rescueState.Results.Select(r=>(r.Informational?"· ":r.Success?"✓ ":"✕ ")+r.Label+" · "+Backend.Redact(r.Detail)));
        if(_rescueState.Phase=="RecoveryFailed")RescueDetailLabel.Text+=Environment.NewLine+Backend.Redact(_rescueState.LastMessage);
        InstallRescueButton.IsEnabled=!_busy&&!_engineRunning&&!_rescueState.BlocksRouting;
        ProfileInput.IsEnabled=FindWireSockButton.IsEnabled=BrowseCliButton.IsEnabled=ImportProfileButton.IsEnabled=ApplyConnectionButton.IsEnabled=ExpectedEgressInput.IsEnabled=!_busy&&!_engineRunning&&!_rescueState.BlocksRouting;
        GuardTestButton.IsEnabled=_rescue.Installed&&!_busy&&!_engineRunning&&!_rescueState.BlocksRouting;
        WireSockTestButton.IsEnabled=VpnusTestButton.IsEnabled=SystemTestButton.IsEnabled=GuardTestButton.IsEnabled && RoutePilot.Agent.NetworkProbe.Vpnus!=null;
        if(!_engineRunning && RoutePilot.Agent.NetworkProbe.Vpnus==null)RescueDetailLabel.Text+="\nРасширенные автономные тесты требуют настроенного VPNUS. Обычный маршрут и встроенная проверка HTTPS работают без него.";
        RecoverTestButton.IsEnabled=_rescue.Installed&&!_busy&&_rescueState.BlocksRouting&&_rescueState.Phase!="Restoring";
    }
    private void RefreshPolicyState()
    {
        RoutingModeInput.IsEnabled=AllowListedDpiBox.IsEnabled=!_engineRunning&&!_busy&&!_rescueState.BlocksRouting;
        try
        {
            using var stream=new FileStream(Path.Combine(_root,"runtime","policy-state.json"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            using var document=JsonDocument.Parse(stream);var state=document.RootElement;
            var updated=state.GetProperty("updatedUtc").GetDateTime();
            string phase=state.GetProperty("phase").GetString()!;
            var session=AppPaths.Read(Path.Combine(_root,"runtime","session.json"),()=>new EngineSession());
            bool sameSession=_engineRunning && state.GetProperty("epoch").GetString()!.StartsWith(session.Id+":",StringComparison.Ordinal);
            bool live=sameSession && (DateTime.UtcNow-updated).TotalSeconds<10;
            PolicyStateLabel.Text=sameSession && phase=="NetworkChanged"?"Сеть изменилась · ожидается восстановление":sameSession && phase=="Error"?"Ошибка контроллера · ожидается восстановление":!live || phase=="Stopped"?"Контроллер маршрутов остановлен":phase=="Applying"?"Подтверждается применение маршрутов":phase=="Auto"?"Авто · выбираются новые маршруты":"Режим списков · VPN для ограничений";
            if(live && phase=="Applying" && state.TryGetProperty("publication",out var pendingPublication) && pendingPublication.GetProperty("phase").GetString()=="ApplyFailed")PolicyStateLabel.Text="Новые правила не подтверждены · ожидается повторная проверка";
            var bytes=state.GetProperty("bytes");
            PolicyDetailLabel.Text="Учтено в активных потоках за сессию: напрямую "+Format.Bytes(bytes.GetProperty("direct").GetInt64())+" · DPI "+Format.Bytes(bytes.GetProperty("dpi").GetInt64())+" · VPN "+Format.Bytes(bytes.GetProperty("vpn").GetInt64())+Environment.NewLine+
                "Проб: "+state.GetProperty("pending").GetInt32()+" · решений: "+state.GetProperty("decisions").GetArrayLength()+Environment.NewLine+
                string.Join(Environment.NewLine,state.GetProperty("events").EnumerateArray().Take(4).Select(e=>e.GetString()));
            if(state.TryGetProperty("blocked",out var blocked) && blocked.GetInt32()>0)PolicyDetailLabel.Text+=Environment.NewLine+"Ожидают подтверждённого пути: "+blocked.GetInt32()+". Повторная проверка выполняется при обращении.";
            if(state.TryGetProperty("stale",out var stale) && stale.GetInt32()>0)PolicyDetailLabel.Text+=Environment.NewLine+"Сохранён прежний выход до перепроверки: "+stale.GetInt32()+".";
            if(state.TryGetProperty("publication",out var publication) && publication.TryGetProperty("phase",out var applied) && applied.GetString()=="Applied")PolicyDetailLabel.Text+=Environment.NewLine+"Последнее применение правил: "+publication.GetProperty("milliseconds").GetInt64()+" мс.";
        }
        catch{PolicyStateLabel.Text=_engineRunning?"Контроллер маршрутов недоступен":"Контроллер маршрутов остановлен";PolicyDetailLabel.Text="В режиме «Авто» первая неудачная попытка может завершиться ошибкой. Проверки не повторяют данные приложения.";}
    }
    private void RoutingPolicy_Changed(object sender,RoutedEventArgs e)
    {
        if(!_ready||_render)return;
        _routingPolicy.Mode=RoutingModeInput.SelectedIndex==1?"Auto":"Lists";_routingPolicy.AllowListedDpi=AllowListedDpiBox.IsChecked==true;
        try{AppPaths.Write(Path.Combine(_root,"data","routing-policy.json"),_routingPolicy);Notice("Режим сохранён для следующего подключения.");}catch(Exception ex){Notice(ex.Message,true);}
    }
    private void ClearPolicy_Click(object sender,RoutedEventArgs e)
    {
        if(_rescueState.BlocksRouting){Notice("Дождись завершения автономного теста.");return;}
        try
        {
            File.WriteAllText(Path.Combine(_root,"runtime","clear-policy.request"),Guid.NewGuid().ToString("N"));
            if(!_engineRunning)File.Delete(Path.Combine(_root,"runtime","policy-state.json"));
            Notice("Кэш решений очищается. Настройки твоих правил сохраняются.");RefreshPolicyState();
        }catch(Exception ex){Notice(ex.Message,true);}
    }
    private async void InstallRescue_Click(object sender,RoutedEventArgs e)
    {
        await ApplyConnectionAsync();
    }
    private async void RescueTest_Click(object sender,RoutedEventArgs e)
    {
        if(_busy||sender is not Button button)return;_busy=true;
        try
        {
            string command=(string)button.Tag;
            var result=await _rescue.Command(command,command=="recover"?_rescueState.Id:null);
            Notice(result.Success?command=="test-vpnus"?"Проверяется перезапуск VPNUS. Служба контролирует возврат связи.":"Автономный тест запущен. Результат появится здесь.":RescueClient.Friendly(result.Message),!result.Success);
        }
        catch(Exception ex){Notice("Служба восстановления недоступна: "+ex.Message,true);}
        finally{_busy=false;RefreshRescueState();}
    }
    private async Task<bool> PerformActionAsync(string action)
    {
        if(_rescueState.BlocksRouting){Notice("Дождись завершения автономного теста и восстановления.");return false;}
        if(_busy)return false;_busy=true;ConnectButton.IsEnabled=false;DiagnosticsButton.IsEnabled=false;UpdateFeedsButton.IsEnabled=false;
        ConnectButton.Content=action=="Start"?"Подключение…":action=="Stop"?"Отключение…":"Обновление…";
        Notice(action=="Start"?"Подтверди запрос Windows. Подключаю WireSock и проверяю туннель…":action=="Stop"?"Отключаю умный маршрут…":"Загружаю и проверяю списки через VPN…");
        try
        {
            var result=await _backend.ActionAsync(action,action!="UpdateRules");
            string message=result.Success?(action=="Start"?"Умный маршрут подключён.":action=="Stop"?"Маршрут отключён.":"Списки обновлены. Новые соединения используют свежие правила."):Backend.FriendlyError(result.Message);
            Notice(message,!result.Success);Event(message);_lastService=DateTime.MinValue;return result.Success;
        }
        catch(Win32Exception ex)when(ex.NativeErrorCode==1223){Notice("Запрос Windows отменён. Настройки сети не изменены.");return false;}
        catch(Exception ex){Notice(Backend.FriendlyError(ex.Message),true);Event(ex.Message);return false;}
        finally{_busy=false;ConnectButton.IsEnabled=true;DiagnosticsButton.IsEnabled=true;UpdateFeedsButton.IsEnabled=true;await RefreshAsync();}
    }
    private async void Connect_Click(object sender,RoutedEventArgs e)=>await ToggleAsync();
    private async void Diagnostics_Click(object sender,RoutedEventArgs e)
    {
        if(_rescueState.BlocksRouting){Notice("Дождись завершения автономного теста и восстановления.");return;}
        if(_busy)return;
        if(_engineRunning){Notice("Для изолированной проверки сначала отключи умный маршрут.");return;}
        if(!File.Exists(Path.Combine(_root,"runtime","router.json"))){PreviewPage(4);Notice("Сначала выбери профиль и примени подключение в настройках.");return;}
        if(_rescue.Installed && RoutePilot.Agent.NetworkProbe.Vpnus!=null)
        {
            _busy=true;
            try{NavSettings.IsChecked=true;var reply=await _rescue.Command("test-system");Notice(reply.Success?"Запущена проверка с временным отключением и автоматическим возвратом VPNUS.":RescueClient.Friendly(reply.Message),!reply.Success);}
            catch(Exception ex){Notice(ex.Message,true);}finally{_busy=false;RefreshRescueState();}
            return;
        }
        _busy=true;_suppressTelemetry=true;DiagnosticsButton.IsEnabled=false;ConnectButton.IsEnabled=false;Notice("Проверяю конфигурацию и выбор маршрутов. Системная сеть не меняется.");
        try
        {
            var result=await _backend.RunAsync("powershell.exe",new[]{"-NoProfile","-ExecutionPolicy","Bypass","-File",Path.Combine(_root,"Verify-Routing.ps1")},60000);
            File.WriteAllText(Path.Combine(_root,"logs","diagnostics.log"),result.Output);
            string message=result.Code==0?"Правила работают: Яндекс — напрямую, YouTube — через VPN. Доступность AWG2 проверяется при подключении.":"Проверка не пройдена. Подробности сохранены в diagnostics.log.";
            Notice(message,result.Code!=0);Event(message);
        }
        catch(Exception ex){Notice(ex.Message,true);}
        finally{_suppressTelemetry=false;_busy=false;DiagnosticsButton.IsEnabled=true;ConnectButton.IsEnabled=true;await RefreshAsync();}
    }
    private async void UpdateFeeds_Click(object sender,RoutedEventArgs e)
    {if(!_engineRunning || !_vpnConnected || !_bridgeReady){Notice("Сначала подключи умный маршрут: списки скачиваются через VPN.");return;}await PerformActionAsync("UpdateRules");}
    private void Navigate_Click(object sender,RoutedEventArgs e)
    {
        if(Pages==null || sender is not RadioButton button)return;int page=int.Parse((string)button.Tag);Pages.SelectedIndex=page;
        string[] titles={"Обзор сети","Активные соединения","Правила маршрутизации","Журнал","Настройки"};
        string[] subtitles={"Твой интернет. Под нужным маршрутом.","Что подключено и каким путём идёт трафик.","Ограниченные сайты через VPN. Остальное напрямую.","Состояние подключения и результаты проверок.","Поведение приложения и текущее подключение."};
        PageTitle.Text=titles[page];PageSubtitle.Text=subtitles[page];if(_ready&&page==3)ShowLogs();
    }
    private void AllConnections_Click(object sender,RoutedEventArgs e)=>NavConnections.IsChecked=true;
    private void ConnectionSearch_Changed(object sender,TextChangedEventArgs e){if(_ready)UpdateConnectionGrids();}
    private void ConnectionSelection_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(ConnectionsGrid.SelectedItem is ConnectionRow row)ConnectionDetailLabel.Text=row.Address+" · "+row.Network+" · Правило: "+row.Rule;
    }
    private async void CloseConnection_Click(object sender,RoutedEventArgs e)
    {
        if(ConnectionsGrid.SelectedItem is not ConnectionRow row){Notice("Сначала выбери соединение.");return;}
        try{await _backend.CloseConnectionAsync(row.Id);Event("Соединение закрыто: "+row.Host);await RefreshAsync();}catch(Exception ex){Notice(ex.Message,true);}
    }
    private void AddRule_Click(object sender,RoutedEventArgs e)=>AddRule();
    private void RuleInput_KeyDown(object sender,KeyEventArgs e){if(e.Key==Key.Enter)AddRule();}
    private void AddRule()
    {
        if(_rescueState.BlocksRouting){Notice("Во время автономного теста изменения правил отложены.");return;}
        try
        {
            string domain=DomainRules.Normalize(RuleDomainInput.Text),target=RuleTargetInput.SelectedIndex==0?"VPN":"Напрямую";
            var proposed=_rules.Where(r=>r.Domain!=domain).Append(new UserRule{Domain=domain,Target=target}).ToList();
            DomainRules.Save(_root,proposed);_rules=proposed;UpdateRulesGrid();RuleDomainInput.Clear();Notice("Правило сохранено: "+domain+" → "+target+". Оно действует для новых соединений.");Event("Добавлено правило: "+domain+" → "+target);
        }catch(Exception ex){Notice(ex.Message,true);}
    }
    private void RemoveRule_Click(object sender,RoutedEventArgs e)
    {
        if(_rescueState.BlocksRouting){Notice("Во время автономного теста изменения правил отложены.");return;}
        if(RulesGrid.SelectedItem is not UserRule rule){Notice("Выбери своё правило в таблице.");return;}
        try{var next=_rules.Where(r=>r.Domain!=rule.Domain).ToList();DomainRules.Save(_root,next);_rules=next;UpdateRulesGrid();Notice("Своё правило удалено. Для сайта снова действуют общие списки.");Event("Удалено правило: "+rule.Domain);}catch(Exception ex){Notice(ex.Message,true);}
    }
    private async void CheckDomain_Click(object sender,RoutedEventArgs e)
    {
        try{DomainResultLabel.Text=await _backend.CheckDomainAsync(CheckDomainInput.Text);}catch(Exception ex){DomainResultLabel.Text=ex.Message;}
    }
    private void ShowLogs()=>LogText.Text=string.Join(Environment.NewLine,_events)+Environment.NewLine+Environment.NewLine+_backend.ReadLog();
    private void RefreshLogs_Click(object sender,RoutedEventArgs e)=>ShowLogs();
    private static void OpenPath(string path)=>Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
    private void OpenLogs_Click(object sender,RoutedEventArgs e)=>OpenPath(Path.Combine(_root,"logs"));
    private void OpenReadme_Click(object sender,RoutedEventArgs e)=>OpenPath(Path.Combine(_root,"README.md"));
    private void Preferences_Changed(object sender,RoutedEventArgs e)
    {
        if(!_ready)return;_preferences.CloseToTray=CloseToTrayBox.IsChecked==true;_preferences.RefreshSeconds=Math.Max(1,RefreshIntervalInput.SelectedIndex+1);_timer.Interval=TimeSpan.FromSeconds(_preferences.RefreshSeconds);SaveData();
    }
    private void TitleDrag(object sender,MouseButtonEventArgs e){if(e.ClickCount==2)WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;else if(e.ButtonState==MouseButtonState.Pressed)try{DragMove();}catch(InvalidOperationException){}}
    private void Minimize_Click(object sender,RoutedEventArgs e)=>WindowState=WindowState.Minimized;
    private void Maximize_Click(object sender,RoutedEventArgs e)=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
}
