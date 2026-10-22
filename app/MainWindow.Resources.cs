using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using RoutePilot.Resources;

namespace RoutePilot;

public static class ResourcePresentation
{
    public static string Issue(ResourceIssue issue)=>issue.Code switch
    {
        "UdpPressure"=>"Заканчивается запас UDP-портов ("+issue.Scope+").",
        "UdpAllocationFailure"=>"Windows не смогла выделить UDP-порт ("+issue.Scope+").",
        "UdpAllocationDenied"=>"Windows запретила выделение UDP-порта ("+issue.Scope+").",
        "CommitPressure"=>"Заканчивается доступный объём выделяемой памяти Windows.",
        "PhysicalPressure"=>"Мало доступной оперативной памяти.",
        "CitaResourceGrowth"=>"Ресурсы компонента растут длительное время: "+issue.Scope+".",
        _=>"Ресурсы Windows требуют проверки."
    };
    public static string Summary(ResourceState? state)
    {
        if(state==null)return "Нет свежего отчёта службы наблюдения.";
        if(state.Issues.Count>0)
        {
            var issue=state.Issues[0];
            string prefix=state.Sample.ObservedUtc-issue.ObservedUtc>TimeSpan.FromSeconds(20)?"Последнее предупреждение: ":"";
            return prefix+Issue(issue);
        }
        if(state.Pending)return "Перепроверяем повышенную нагрузку Windows…";
        return state.Complete?"Порты и память Windows: запас доступен.":"Часть измерений Windows недоступна.";
    }
    public static string Report(ResourceState? state,DateTime now)
    {
        if(state!=null&&!ResourceStateCache.Valid(state,now))state=null;
        if(state==null)return "Свежий отчёт недоступен.\n\nПосле установки новой версии защищённая служба обновляется при следующем подключении с обычным запросом Windows. Если служба уже обновлена, проверь её состояние в журнале.\n\nОтсутствие отчёта не означает, что интернет отключён.";
        var text=new StringBuilder();text.AppendLine(Summary(state));
        text.AppendLine("Проверка: "+state.Sample.ObservedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")+" · служба "+state.AgentVersion);
        text.AppendLine();
        foreach(var issue in state.Issues)text.AppendLine("• "+Issue(issue));
        foreach(var pool in state.Sample.Pools)
        {
            text.AppendLine();text.AppendLine("UDP / "+pool.Family);
            if(pool.UsedPercent is {} percent)
                text.AppendLine($"Порты диапазона: занято {pool.Used} из {pool.Capacity} ({percent:0.0}%). Диапазон {pool.Start}–{pool.Start+pool.Count-1}.");
            else text.AppendLine("Занятость диапазона не определена.");
            text.AppendLine(pool.ReservationsAvailable?"Системные резервирования учтены: "+pool.Reserved+".":"Резервирования не прочитаны; доля рассчитана по полному диапазону, если он известен.");
            text.AppendLine("Выделение одного порта: "+(pool.Allocation switch{"Available"=>"успешно","ResourceFailure"=>"не хватило сетевых ресурсов","AccessDenied"=>"запрещено политикой Windows","NotSupported"=>"семейство адресов недоступно",_=>"не проверено"})+".");
        }
        text.AppendLine();text.AppendLine("Память Windows");
        if(state.Sample.MemoryAvailable)
        {
            text.AppendLine("Доступная RAM: "+Format.Bytes(state.Sample.PhysicalAvailable)+" из "+Format.Bytes(state.Sample.PhysicalTotal));
            text.AppendLine("Выделенная память: "+Format.Bytes(state.Sample.CommitTotal)+" из лимита "+Format.Bytes(state.Sample.CommitLimit));
            text.AppendLine("Системные дескрипторы: "+state.Sample.SystemHandles);
        }
        else text.AppendLine("Измерение недоступно.");
        if(state.Sample.Unavailable.Any(x=>x is "CitaProcessCoverage" or "ProcessCoverageLimited"))text.AppendLine("Полный срез процессов сейчас недоступен. По нему нельзя подтвердить исчезновение роста ресурсов.");
        text.AppendLine();text.AppendLine("Процессы с наибольшими показателями (срез, не доказательство причины)");
        if(state.Sample.ProcessesObservedUtc!=default)text.AppendLine("Срез процессов: "+state.Sample.ProcessesObservedUtc.ToLocalTime().ToString("HH:mm:ss")+"; полное обновление раз в 30 секунд.");
        text.AppendLine("PID · Процесс · UDP v4+v6 · Частная память · Дескрипторы");
        foreach(var process in state.Sample.Processes)
            text.AppendLine($"{process.Pid} · {process.Name} · {process.UdpPorts} · {Format.Bytes(process.PrivateBytes)} · {process.Handles}");
        if(state.Sample.UnresolvedOwners>0)text.AppendLine("Не удалось определить "+state.Sample.UnresolvedOwners+" крупных владельцев UDP. Процесс мог завершиться, PID измениться или доступ ограничен. Закрытие родителя не всегда освобождает унаследованные дочерними процессами сокеты.");
        text.AppendLine();
        text.AppendLine("Занятость портов — оценка по уникальным номерам: общие привязки и правила Windows могут влиять на фактическую доступность. Проверка выделяет по одному временному UDP-порту без отправки данных. Частная память не равна физической RAM; рост ресурсов сам по себе не доказывает утечку.");
        text.AppendLine();text.AppendLine("Что делать");
        text.AppendLine("Сохрани работу. В диспетчере задач проверь перечисленные процессы и по возможности закрой соответствующее приложение обычным способом. Службы Windows, защиту и WireSock останавливай только после проверки их назначения. После освобождения ресурсов монитор проверит состояние повторно.");
        text.AppendLine("Монитор не завершает процессы и не перезапускает сеть. В отчёте нет VPN-профилей, ключей, адресов соединений или командных строк.");
        if(state.Incidents.Count>0)
        {
            text.AppendLine();text.AppendLine("Последние изменения");
            foreach(var item in state.Incidents.TakeLast(10).Reverse())
                text.AppendLine(item.ObservedUtc.ToLocalTime().ToString("HH:mm:ss")+" · "+item.Code+" · "+item.Scope+" · "+item.Transition);
        }
        return text.ToString();
    }
}

public partial class MainWindow
{
    ResourceState? _resourceState;
    DateTime _resourceReadAt,_resourceSampleAt;
    string _resourceMonitorId="";
    int _resourceClearSamples;
    string ResourcePath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RoutePilot","resource-state.json");
    void RefreshResourceState()
    {
        if(_render||(DateTime.UtcNow-_resourceReadAt).TotalSeconds<5)return;
        _resourceReadAt=DateTime.UtcNow;
        _resourceState=ResourceStateCache.Read(ResourcePath,DateTime.UtcNow);
        ShowResourceState();
        if(!_preferences.ResourceNotificationsEnabled){_resourceClearSamples=0;_resourceSampleAt=default;_resourceMonitorId="";return;}
        if(_resourceState==null){_resourceClearSamples=0;_resourceSampleAt=default;_resourceMonitorId="";return;}
        if(_resourceMonitorId!=_resourceState.MonitorId||_resourceState.Sample.ObservedUtc<_resourceSampleAt)
        {_resourceClearSamples=0;_resourceSampleAt=default;_resourceMonitorId=_resourceState.MonitorId;}
        string key=string.Join("|",_resourceState.Issues.OrderBy(i=>i.Key).Select(i=>i.Key+":"+i.Level));
        if(key.Length>0)
        {
            _resourceClearSamples=0;
            bool current=_resourceState.Issues.Any(i=>ResourceStateCache.Fresh(i.ObservedUtc,DateTime.UtcNow));
            if(current&&key!=_preferences.ResourceNotificationKey&&(_resourceState.Level==2&&_preferences.ResourceNotificationLevel<2||(DateTime.UtcNow-_preferences.ResourceNotificationAt).TotalMinutes>=5))
            {
                _preferences.ResourceNotificationKey=key;_preferences.ResourceNotificationAt=DateTime.UtcNow;
                _preferences.ResourceNotificationLevel=_resourceState.Level;
                _tray?.ShowBalloonTip(7000,"CITA Route · ресурсы Windows",ResourcePresentation.Summary(_resourceState),System.Windows.Forms.ToolTipIcon.Warning);
                Event(ResourcePresentation.Summary(_resourceState));
            }
        }
        else if(_resourceState.Sample.ObservedUtc!=_resourceSampleAt)
        {
            if(_resourceState.Complete&&!_resourceState.Pending)_resourceClearSamples++;else _resourceClearSamples=0;
            if(_resourceClearSamples>=3&&!string.IsNullOrEmpty(_preferences.ResourceNotificationKey))
            {
                _preferences.ResourceNotificationKey="";
                _tray?.ShowBalloonTip(5000,"CITA Route","Предупреждение о ресурсах снято после повторных проверок.",System.Windows.Forms.ToolTipIcon.Info);
                Event("Предупреждение о ресурсах снято после повторных проверок.");
            }
        }
        _resourceSampleAt=_resourceState.Sample.ObservedUtc;
    }
    void ShowResourceState()
    {
        string summary=ResourcePresentation.Summary(_resourceState);
        ResourceSummary.Text=summary;ResourceSummary.Foreground=_resourceState?.Level>0?ConsoleTheme.Warn:ConsoleTheme.Dim;
        CompactResourceWarning.BorderBrush=_resourceState?.Level==2?ConsoleTheme.Red:ConsoleTheme.Warn;
        bool showWarning=_preferences.ResourceNotificationsEnabled&&_resourceState?.Level>0;
        CompactResourceWarning.Visibility=showWarning?Visibility.Visible:Visibility.Collapsed;
        CompactPolicyNote.Visibility=showWarning?Visibility.Collapsed:Visibility.Visible;
        CompactResourceText.Text=summary+" Подробнее ↗";
    }
    public void PreviewResourceWarning()
    {
        if(!_render)return;
        _preferences.ResourceNotificationsEnabled=true;
        ResourceNotificationsBox.IsChecked=true;
        var now=DateTime.UtcNow;
        _resourceState=new(){MonitorId=Guid.NewGuid().ToString("N"),AgentVersion=ReleaseUpdates.Current.ToString(3),UpdatedUtc=now,
            Sample=new(){ObservedUtc=now},Issues=new(){new(){Key="ports-IPv4",Code="UdpPressure",Scope="IPv4",Level=2,ObservedUtc=now}}};
        ShowResourceState();
    }
    private void ResourceNotifications_Changed(object sender,RoutedEventArgs e)
    {
        if(!_ready)return;
        _preferences.ResourceNotificationsEnabled=ResourceNotificationsBox.IsChecked==true;
        _preferences.ResourceNotificationKey="";_preferences.ResourceNotificationAt=default;_preferences.ResourceNotificationLevel=0;
        _resourceClearSamples=0;_resourceSampleAt=default;_resourceMonitorId="";
        SaveData();ShowResourceState();
        if(_preferences.ResourceNotificationsEnabled){_resourceReadAt=default;RefreshResourceState();}
    }
    private void ResourceDetails_Click(object sender,RoutedEventArgs e)=>ShowResourceDetails();
    void ShowResourceDetails()
    {
        if(!_render)RefreshResourceState();
        var text=new TextBox{Text=ResourcePresentation.Report(_resourceState,DateTime.UtcNow),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            Background=ConsoleTheme.Brush("#101011"),Foreground=ConsoleTheme.Text,BorderThickness=new Thickness(0),Padding=new Thickness(16)};
        var panel=new DockPanel();
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(12)};
        var taskManager=new Button{Content="Диспетчер задач",Margin=new Thickness(0,0,10,0)};
        taskManager.Click+=(_,_)=>{try{Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","Taskmgr.exe")){UseShellExecute=true});}catch{Notice("Не удалось открыть диспетчер задач.",true);}};
        var copy=new Button{Content="Копировать отчёт"};copy.Click+=(_,_)=>{try{Clipboard.SetText(text.Text);}catch{}};
        actions.Children.Add(taskManager);actions.Children.Add(copy);DockPanel.SetDock(actions,Dock.Bottom);panel.Children.Add(actions);panel.Children.Add(text);
        var window=new Window{Title="CITA Route · ресурсы Windows",Owner=this,Width=740,Height=650,MinWidth=420,MinHeight=360,MaxWidth=Math.Max(420,SystemParameters.WorkArea.Width),MaxHeight=Math.Max(360,SystemParameters.WorkArea.Height),
            WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=ConsoleTheme.Brush("#101011"),Foreground=ConsoleTheme.Text,Content=panel};
        window.ShowDialog();
    }
}
