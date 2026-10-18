using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace RoutePilot;

public partial class MainWindow
{
    private ReleaseUpdate? _release;
    private DateTime _lastUpdateCheck=DateTime.MinValue;
    private bool _updateChecking,_updateDownloading;
    private string? _downloadedPackage;
    private readonly CancellationTokenSource _updateLifetime=new();
    private async Task CheckUpdatesAsync(bool manual=false)
    {
        if(_render||_updateChecking||_updateDownloading||_exiting)return;
        _updateChecking=true;_lastUpdateCheck=DateTime.UtcNow;
        try
        {
            UpdateStatusLabel.Text="Проверяю GitHub Releases…";
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var http=ReleaseUpdates.CreateClient(_engineAvailable);
            _release=await new ReleaseUpdates(http).Check(timeout.Token);
            UpdateStatusLabel.Text=_release==null?"Новых стабильных версий нет. Текущая: "+ReleaseUpdates.Current.ToString(3):"Доступна CITA Route "+_release.Version.ToString(3);
            DownloadUpdateButton.Visibility=_release!=null?Visibility.Visible:Visibility.Collapsed;
            if(_release!=null && manual)Notice("Доступно обновление "+_release.Version.ToString(3)+". Его можно загрузить в настройках.");
        }
        catch(OperationCanceledException){if(!_exiting)UpdateStatusLabel.Text="Проверка обновлений не завершена. Можно повторить позже.";}
        catch(Exception){UpdateStatusLabel.Text="GitHub Releases временно недоступен. Текущее подключение продолжает работать.";}
        finally{_updateChecking=false;}
    }
    private async void CheckUpdates_Click(object sender,RoutedEventArgs e)=>await CheckUpdatesAsync(true);
    private void OpenReleases_Click(object sender,RoutedEventArgs e)=>OpenPath((_release?.Page??new Uri("https://github.com/"+ReleaseUpdates.Repository+"/releases")).AbsoluteUri);
    private async void DownloadUpdate_Click(object sender,RoutedEventArgs e)
    {
        if(_release==null||_updateDownloading||_busy)return;
        _updateDownloading=true;DownloadUpdateButton.IsEnabled=false;
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);timeout.CancelAfter(TimeSpan.FromMinutes(20));
            using var http=ReleaseUpdates.CreateClient(_engineAvailable);
            _downloadedPackage=await new ReleaseUpdates(http).Download(_release,_root,new Progress<int>(p=>UpdateStatusLabel.Text="Загрузка обновления: "+p+"%"),timeout.Token);
            UpdateStatusLabel.Text="Обновление проверено. Для установки отключи маршрут. Профили и правила сохраняются.";
            InstallUpdateButton.Visibility=Visibility.Visible;
        }
        catch(OperationCanceledException){UpdateStatusLabel.Text="Загрузка отменена или истекло время ожидания.";}
        catch(Exception ex){UpdateStatusLabel.Text="Обновление не готово: "+Backend.Redact(ex.Message);}
        finally{_updateDownloading=false;DownloadUpdateButton.IsEnabled=true;}
    }
    private void InstallUpdate_Click(object sender,RoutedEventArgs e)
    {
        if(_busy||_updateDownloading||_downloadedPackage==null||_release==null)return;
        if(_engineRunning||_rescueState.BlocksRouting){Notice("Отключи маршрут и заверши диагностику перед установкой обновления.");return;}
        try
        {
            using var self=Process.GetCurrentProcess();
            string Literal(string value)=>"'"+value.Replace("'","''")+"'";
            string command="& "+Literal(Path.Combine(_root,"Install-Release.ps1"))+" -Package "+Literal(_downloadedPackage)+" -Version "+Literal(_release.Version.ToString(3))+" -GuiPid "+self.Id+" -GuiStartedUtc "+Literal(self.StartTime.ToUniversalTime().ToString("O"));
            var info=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=_root,Arguments="-NoProfile -ExecutionPolicy Bypass -EncodedCommand "+Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command))};
            Process.Start(info)?.Dispose();ExitApplication();
        }
        catch(Exception ex){Notice("Не удалось запустить установку: "+Backend.Redact(ex.Message),true);}
    }
}
