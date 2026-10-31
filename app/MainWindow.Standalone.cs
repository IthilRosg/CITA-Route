using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
namespace RoutePilot;
public partial class MainWindow
{
    readonly CheckBox _nativeTraceToggle=new(){Content="Показывать недавние проверки сайтов в этом подключении",Margin=new Thickness(0,16,0,8)};
    readonly TextBox _nativeTraceText=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=240,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Visibility=Visibility.Collapsed};
    bool _nativeTraceBusy;DateTime _nativeTraceNext;
    async Task RefreshNativeTraceAsync(bool? enable=null)
    {
        if(_nativeTraceBusy||!ClientEdition.NativeOnly)return;
        var state=_nativeView;
        if(state==null||(enable!=false&&!state.Connected)){_nativeTraceToggle.IsChecked=false;_nativeTraceText.Visibility=Visibility.Collapsed;_nativeTraceText.Text="";return;}
        _nativeTraceBusy=true;
        try
        {
            var reply=await _nativeClient.Command(enable==true?"native-trace-on":enable==false?"native-trace-off":"native-trace-read",state.Operation);
            if(!reply.Success||reply.Native==null)throw new IOException("История проверок недоступна.");
            var trace=reply.Native;_nativeTraceToggle.IsChecked=trace.TraceEnabled;_nativeTraceText.Visibility=trace.TraceEnabled?Visibility.Visible:Visibility.Collapsed;
            _nativeTraceText.Text=trace.TraceEntries.Length==0?"Новых проверок пока нет.":string.Join("\n",trace.TraceEntries.Reverse().Select(x=>
            {
                string time;try{time=DateTimeOffset.FromUnixTimeMilliseconds(x.UnixMilliseconds).ToLocalTime().ToString("HH:mm:ss");}catch(ArgumentOutOfRangeException){time="—";}
                string label=x.Event switch{"observed"=>"обнаружено","started"=>"проверяется direct","probe-passed"=>"direct подтверждён","probe-failed"=>"direct не подтвердился","applied"=>"direct разрешён для новых соединений",_=>"неизвестно"};
                return $"{time} · {x.Domain} · {x.Application} · {label}";
            }));
            _nativeTraceNext=DateTime.UtcNow.AddSeconds(3);
        }
        catch(Exception ex)when(ex is IOException or OperationCanceledException or System.Text.Json.JsonException)
        {_nativeTraceToggle.IsChecked=false;_nativeTraceText.Text="";_nativeTraceText.Visibility=Visibility.Collapsed;if(enable==true)Notice("Не удалось включить наблюдение за проверками.",true);}
        finally{_nativeTraceBusy=false;}
    }
    void UpdateNativeImportAvailability()
    {
        _nativeImportButton.IsEnabled=!_busy&&!_discovering&&!_engineRunning&&!_rescueState.BlocksRouting&&!_pendingConnectionOperation&&
            (ClientEdition.NativeOnly||(_nativeView?.Phase is "Disconnected" or "Unconfigured"));
    }
    bool StandaloneAgentMatchesPackage()
    {
        try{
            using var product=System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(_root,"product.json")));
            string? expected=product.RootElement.GetProperty("distribution").GetProperty("agentSha256").GetString();
            using var input=System.IO.File.OpenRead(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RoutePilot","Agent","RoutePilot.Agent.exe"));
            return string.Equals(expected,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)),StringComparison.OrdinalIgnoreCase);
        }catch{return false;}
    }
    void ConfigureStandaloneSettings(StackPanel settings)
    {
        UIElement? Card(FrameworkElement element){FrameworkElement? current=element;while(current!=null&&current.Parent!=settings)current=current.Parent as FrameworkElement;return current;}
        var keep=new[]{Card(StartOnSignInBox),Card(CloseToTrayBox)}.Where(c=>c!=null).Distinct().ToArray();
        settings.Children.Clear();foreach(var card in keep)settings.Children.Add(card!);
        SidebarPolicyFirst.Text="Новые HTTPS → VPN";SidebarPolicySecond.Text="Прямой путь проверяется";
        CompactPolicyNote.Text="Новые HTTPS → VPN · прямой путь проверяется";
        OverviewBackendLabel.Text="ВСТРОЕННЫЙ VPN";TodayLabel.Text="—";TodayLabel.ToolTip=null;LifetimeLabel.Text="Статистика пока недоступна";
        EmptyConnectionsLabel.Text="Подробная статистика соединений пока недоступна.";
        if(AutoUpdateWireSockBox.Parent is Panel autoPanel)autoPanel.Children.Remove(AutoUpdateWireSockBox);
        var about=new StackPanel();about.Children.Add(new TextBlock{Text="CITA Route · самостоятельный клиент",FontSize=18});
        about.Children.Add(new TextBlock{Text="Собственный движок WireGuard / AmneziaWG. Импорт .conf без отдельного VPN-клиента.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)});
        var releases=new Button{Content="Предварительные выпуски на GitHub",Margin=new Thickness(0,12,0,0)};releases.Click+=OpenReleases_Click;about.Children.Add(releases);
        settings.Children.Add(new Border{Style=(Style)FindResource("Card"),Child=about});
        if(Pages.Items[2] is TabItem rules){var panel=new StackPanel{Margin=new Thickness(24)};panel.Children.Add(new TextBlock{Text="Автоматическая маршрутизация",FontSize=20});panel.Children.Add(new TextBlock{Text="Новые HTTPS-соединения сначала используют VPN. CITA проверяет прямой путь и применяет подтверждённые решения только к новым соединениям. Проверка не гарантирует работу всех функций сайта.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12)});panel.Children.Add(_nativeRoutingStats);panel.Children.Add(new TextBlock{Text="Встроенный обход DPI для автоматического выбора пока недоступен.",TextWrapping=TextWrapping.Wrap,Foreground=ConsoleTheme.Dim,Margin=new Thickness(0,12,0,0)});_nativeTraceToggle.Click+=async(_,_)=>await RefreshNativeTraceAsync(_nativeTraceToggle.IsChecked==true);panel.Children.Add(_nativeTraceToggle);panel.Children.Add(new TextBlock{Text="При включении показываются домен и имя приложения. Данные хранятся только в памяти текущего подключения до 10 минут и не включаются в диагностику.",TextWrapping=TextWrapping.Wrap,Foreground=ConsoleTheme.Dim,Margin=new Thickness(0,0,0,8)});panel.Children.Add(_nativeTraceText);rules.Content=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=panel};}
        WireSockStatusLabel.Text="Встроенный VPN · WireGuard / AmneziaWG";
    }
    internal void VerifyStandalonePresentation()
    {
        if(!_render||!UseNativeBackend||!_nativeChoice.Selected)throw new Exception("Standalone must select its native backend regardless of saved legacy preferences.");
        foreach(var code in new[]{"NativeHttpsUnavailable","NativeEgressMismatch","NativeUdpUnavailable","NativeUdpTimeout"})if(NativeRoutePresentation.Detail(new NativeRouteView{Phase="RecoveryFailed",ErrorCode=code})==NativeRoutePresentation.Detail(new NativeRouteView{Phase="RecoveryFailed"}))throw new Exception("Specific native validation errors must be visible.");
        var degraded=new NativeRouteView{Phase="Connected",Runtime=new NativeRuntimeView{CoreState="TransportDegraded"}};
        if(degraded.Connected||NativeRoutePresentation.Title(degraded)=="Подключено"||NativeRoutePresentation.Detail(degraded).StartsWith("VPN подключён"))throw new Exception("Degraded transport must not be presented as healthy.");
        var unconfirmedPolicy=new NativeRouteView{Phase="PolicyRecoveryUnconfirmed",Runtime=new NativeRuntimeView{CoreState="Active"}};
        if(unconfirmedPolicy.Connected||NativeRoutePresentation.Title(unconfirmedPolicy)=="Подключено"||!NativeRoutePresentation.Detail(unconfirmedPolicy).Contains("не удалось подтвердить",StringComparison.OrdinalIgnoreCase))throw new Exception("Unconfirmed policy recovery must remain visible even when the core still reports active.");
        var failedPolicy=new NativeRouteView{Phase="RecoveryFailed",ErrorCode="PolicyRecoveryUnconfirmed"};
        if(!NativeRoutePresentation.Detail(failedPolicy).Contains("не удалось подтвердить",StringComparison.OrdinalIgnoreCase))throw new Exception("Policy recovery failure needs a specific explanation.");
        foreach(string routing in new[]{"Stopped","Unavailable","NotConfigured"})
        {
            var limited=new NativeRouteView{Phase="Connected",Runtime=new NativeRuntimeView{CoreState="Active",AutomaticRouting=routing}};
            string detail=NativeRoutePresentation.Detail(limited);
            if(!detail.Contains("Автоматические проверки")||detail.Contains("Проверенные HTTPS-домены"))throw new Exception("Unavailable automatic routing must be visible even when VPN is connected.");
        }
        if(_nativeWireSockCard?.Parent!=null||_nativeMode.Parent!=null)throw new Exception("Standalone must not expose a legacy settings card or backend switch.");
        _nativeSetupInstalling=true;
        foreach(var snapshot in new NativeRouteView?[]{null,new(){Phase="Connected",Desired=true,Runtime=new(){CoreState="Active"}}}){
            ApplyNativeView(snapshot);
            if(_vpnConnected||_engineRunning||_engineAvailable||!FooterStatus.Text.Contains("Применяем обновление",StringComparison.Ordinal))throw new Exception("Installation was overwritten by stale connected or unavailable status.");
        }
        _nativeSetupInstalling=false;
        int picks=0;string confirmed=_nativeChoice.ConfirmedProfile;
        NativeFilePickerFixture=()=>{picks++;return null;};
        foreach(var state in new NativeRouteView?[]{null,new(){Phase="Unconfigured"},new(){Phase="Disconnected",ProfileId="fixture",ProfileName="fixture"}})
        {
            _nativeView=state;SyncConnectionButtons();
            if(!_nativeImportButton.IsEnabled||!CompactProfileButton.IsEnabled)throw new Exception("Profile picker must be available before setup and after disconnect.");
            int before=picks;CompactProfileButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));_nativeImportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(picks!=before+2||_nativeChoice.ConfirmedProfile!=confirmed)throw new Exception("Both profile buttons must open the picker and cancellation must preserve the selection.");
        }
        _busy=true;SyncConnectionButtons();if(_nativeImportButton.IsEnabled||CompactProfileButton.IsEnabled)throw new Exception("Profile selection must be disabled during operations.");
        int blocked=picks;CompactProfileButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));_nativeImportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));if(picks!=blocked)throw new Exception("Profile picker opened during an operation.");
        _busy=false;_engineRunning=true;SyncConnectionButtons();if(_nativeImportButton.IsEnabled||CompactProfileButton.IsEnabled)throw new Exception("Profile selection must be disabled while connected.");
        _engineRunning=false;_nativeView=null;NativeFilePickerFixture=null;SyncConnectionButtons();
        ShowActivated=false;ShowInTaskbar=false;Opacity=0;ApplyView(true,false);Show();UpdateLayout();
        if(Width>460||Height>520)throw new Exception("Standalone compact window exceeded its bounds.");
        ApplyView(false);PreviewPage(4);UpdateLayout();
        void CheckVisible(DependencyObject node){
            if(node is FrameworkElement visible&&visible.IsVisible){
                string? text=node is TextBlock block?block.Text:node is ContentControl control?control.Content as string:null;
                if(text?.Contains("WireSock",StringComparison.OrdinalIgnoreCase)==true)throw new Exception("Legacy option visible in standalone settings.");
            }
            for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);i++)CheckVisible(System.Windows.Media.VisualTreeHelper.GetChild(node,i));
        }
        CheckVisible(this);_exiting=true;Closing-=OnClosing;Close();_backend.Dispose();
    }
}
