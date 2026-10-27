using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
namespace RoutePilot;
public partial class MainWindow
{
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
        if(AutoUpdateWireSockBox.Parent is Panel autoPanel)autoPanel.Children.Remove(AutoUpdateWireSockBox);
        var about=new StackPanel();about.Children.Add(new TextBlock{Text="CITA Route · самостоятельный клиент",FontSize=18});
        about.Children.Add(new TextBlock{Text="Собственный движок WireGuard / AmneziaWG. Импорт .conf без отдельного VPN-клиента.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)});
        var releases=new Button{Content="Предварительные выпуски на GitHub",Margin=new Thickness(0,12,0,0)};releases.Click+=OpenReleases_Click;about.Children.Add(releases);
        settings.Children.Add(new Border{Style=(Style)FindResource("Card"),Child=about});
        if(Pages.Items[2] is TabItem rules)rules.Content=new TextBlock{Text="Автоматическая маршрутизация: новые HTTPS-домены используют VPN, затем проверяется прямой путь. Текущие решения видны в соединениях. Редактор списков прежнего клиента здесь не используется.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(24),FontSize=15};
        WireSockStatusLabel.Text="Встроенный VPN · WireGuard / AmneziaWG";
    }
    internal void VerifyStandalonePresentation()
    {
        if(!_render||!UseNativeBackend||!_nativeChoice.Selected)throw new Exception("Standalone must select its native backend regardless of saved legacy preferences.");
        foreach(var code in new[]{"NativeHttpsUnavailable","NativeEgressMismatch","NativeUdpUnavailable"})if(NativeRoutePresentation.Detail(new NativeRouteView{Phase="RecoveryFailed",ErrorCode=code})==NativeRoutePresentation.Detail(new NativeRouteView{Phase="RecoveryFailed"}))throw new Exception("Specific native validation errors must be visible.");
        if(_nativeWireSockCard?.Parent!=null||_nativeMode.Parent!=null)throw new Exception("Standalone must not expose a legacy settings card or backend switch.");
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
