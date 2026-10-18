using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RoutePilot;

public sealed class UpdatePrompt : Window
{
    public bool Accepted{get;private set;}
    public UpdatePrompt(ReleaseUpdate release,bool ready)
    {
        Title="Обновление CITA Route";Width=610;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;WindowStyle=WindowStyle.None;ShowInTaskbar=false;
        Background=ConsoleTheme.Brush("#0C0C0D");Foreground=ConsoleTheme.Text;
        var content=new StackPanel();
        content.Children.Add(new TextBlock{Text="CITA SYSTEMS // UPDATE AVAILABLE",FontFamily=new FontFamily("Consolas"),FontSize=11,Foreground=ConsoleTheme.Red});
        content.Children.Add(new TextBlock{Text="CITA ROUTE "+release.Version.ToString(3),FontFamily=new FontFamily("Bahnschrift SemiCondensed"),FontSize=34,Margin=new Thickness(0,16,0,12)});
        content.Children.Add(new TextBlock{Text=ready?"Пакет загружен. Установка доступна после отключения маршрута.":"Доступна новая стабильная версия · "+Format.Bytes(release.Bytes),TextWrapping=TextWrapping.Wrap,Foreground=ConsoleTheme.Dim});
        var notes=new TextBlock{Text=string.IsNullOrWhiteSpace(release.Notes)?"Улучшения и исправления CITA Route.":release.Notes,TextWrapping=TextWrapping.Wrap,FontSize=13,Margin=new Thickness(0,16,0,16)};
        content.Children.Add(new ScrollViewer{Content=notes,MaxHeight=260,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        content.Children.Add(new TextBlock{Text="Активное подключение не отключается ради обновления.",Foreground=ConsoleTheme.Dim,FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,20)});
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var later=new Button{Content="Позже",Margin=new Thickness(0,0,10,0),IsCancel=true};later.Click+=(_,_)=>Close();
        var accept=new Button{Content=ready?"Установить":"Загрузить",Style=(Style)Application.Current.FindResource("Primary"),IsDefault=true};
        accept.Click+=(_,_)=>{Accepted=true;DialogResult=true;};buttons.Children.Add(later);buttons.Children.Add(accept);content.Children.Add(buttons);
        Content=new Border{Child=content,BorderBrush=ConsoleTheme.Red,BorderThickness=new Thickness(1),Padding=new Thickness(25)};
        MouseLeftButtonDown+=(_,e)=>{if(e.OriginalSource is TextBlock && e.ButtonState==MouseButtonState.Pressed)DragMove();};
    }
}
