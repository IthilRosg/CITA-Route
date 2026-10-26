using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
namespace RoutePilot;
public partial class MainWindow
{
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
        if(_nativeWireSockCard?.Parent!=null||_nativeMode.Parent!=null)throw new Exception("Standalone must not expose a legacy settings card or backend switch.");
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
