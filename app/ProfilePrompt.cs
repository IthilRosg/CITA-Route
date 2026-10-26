using System;
using System.Windows;
using System.Windows.Controls;

namespace RoutePilot;

public sealed class ProfilePrompt:Window
{
    public string SelectedProfile{get;private set;}="";
    public string Action{get;private set;}="";
    public ProfilePrompt(ConnectionInventory inventory,string? selected,bool missing,bool connect)
    {
        Title="Профиль CITA Route";Width=Math.Min(440,SystemParameters.WorkArea.Width-24);MaxHeight=SystemParameters.WorkArea.Height-24;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
        Background=ConsoleTheme.Brush("#101011");Foreground=ConsoleTheme.Text;FontFamily=new("Segoe UI");
        var panel=new StackPanel{Margin=new Thickness(22)};Content=panel;
        panel.Children.Add(new TextBlock{Text="ПРОФИЛЬ ПОДКЛЮЧЕНИЯ",FontFamily=new("Consolas"),FontSize=11,Foreground=ConsoleTheme.Red});
        var compatibility=WireSockCompatibility.Assess(inventory);
        panel.Children.Add(new TextBlock{Text=!compatibility.CanManage?compatibility.Message:missing?"Прошлый профиль не найден. Выбери другой явно.":inventory.Profiles.Length==0?"Импортируй свой конфиг WireGuard или AmneziaWG.":"Выбери профиль для CITA Route.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,14)});
        var profiles=new ComboBox{ItemsSource=inventory.Profiles,SelectedItem=selected,Margin=new Thickness(0,0,0,14)};
        if(inventory.Profiles.Length>0)panel.Children.Add(profiles);
        var confirm=new Button{Content=connect?"Подключить":"Выбрать",Style=(Style)FindResource("Primary"),IsDefault=true,IsEnabled=compatibility.CanManage&&profiles.SelectedItem is string,Margin=new Thickness(0,0,0,10)};
        profiles.SelectionChanged+=(_,_)=>confirm.IsEnabled=compatibility.CanManage&&profiles.SelectedItem is string;
        confirm.Click+=(_,_)=>{SelectedProfile=profiles.SelectedItem as string??"";Action="Select";DialogResult=true;};
        if(inventory.Profiles.Length>0)panel.Children.Add(confirm);
        var extra=new WrapPanel();panel.Children.Add(extra);
        if(compatibility.OfferInstall)
        {
            var install=new Button{Content="Установить WireSock…",Margin=new Thickness(0,0,8,8)};
            install.Click+=(_,_)=>{Action="InstallWireSock";DialogResult=true;};extra.Children.Add(install);
        }
        if(inventory.CliPath.Length>0)
        {
            var import=new Button{Content="Импорт .conf…",Margin=new Thickness(0,0,8,0)};
            import.Click+=(_,_)=>{Action="Import";DialogResult=true;};extra.Children.Add(import);
        }
        else
        {
            var browse=new Button{Content="Указать WireSock…",Margin=new Thickness(0,0,8,0)};
            browse.Click+=(_,_)=>{Action="Browse";DialogResult=true;};extra.Children.Add(browse);
        }
        var cancel=new Button{Content="Отмена",IsCancel=true};extra.Children.Add(cancel);
        panel.Children.Add(new TextBlock{Text="После применения профиль сохранится для следующего подключения.",TextWrapping=TextWrapping.Wrap,FontSize=11,Foreground=ConsoleTheme.Dim,Margin=new Thickness(0,16,0,0)});
    }
}
