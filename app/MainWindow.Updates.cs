using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace RoutePilot;

public partial class MainWindow
{
    private ReleaseUpdate? _release;
    private DateTime _lastUpdateCheck=DateTime.MinValue,_lastAutoAttempt=DateTime.MinValue;
    private bool _updateChecking,_updateDownloading,_updateInstalling,_updatePromptOpen,_pendingUpdatePrompt;
    private UpdateState _updates=new();
    private readonly CancellationTokenSource _updateLifetime=new();
    private void InitializeUpdates()
    {
        _updates=UpdateJournal.Read(_root);AutoInstallUpdatesBox.IsChecked=_preferences.AutoInstallUpdates;
        if(_render)return;
        if(_updates.Prepared is { } prepared && Version.TryParse(prepared.Tag.TrimStart('v'),out var version) && ReleaseUpdates.Compare(version,ReleaseUpdates.Current)<=0)
        {_updates.Phase="Installed";_updates.Prepared=null;SaveUpdates();}
        else if(_updates.Phase is "Installing" or "Verifying" or "Downloading" or "Disconnecting")
        {
            _updates.Phase="Failed";_updates.Error="Предыдущая установка не завершена. Повтори установку вручную.";
            SaveUpdates();
        }
        Activated+=async(_,_)=>{if(_pendingUpdatePrompt)await ShowUpdatePromptAsync();};
        RefreshUpdateControls();
    }
    private void SaveUpdates(){if(!_render)UpdateJournal.Save(_root,_updates);}
    private void RefreshUpdateControls()
    {
        DownloadUpdateButton.Visibility=_release!=null?Visibility.Visible:Visibility.Collapsed;
        InstallUpdateButton.Visibility=_updates.Prepared!=null?Visibility.Visible:Visibility.Collapsed;
        DownloadUpdateButton.IsEnabled=!_updateDownloading&&!_updateInstalling;
        InstallUpdateButton.IsEnabled=!_updateDownloading&&!_updateInstalling;
        if(_updates.Phase=="Failed")UpdateStatusLabel.Text="Обновление отложено: "+Backend.Redact(_updates.Error);
        else if(_updates.Phase=="WaitingForNetwork")UpdateStatusLabel.Text="Пакет готов. Повторная проверка GitHub ожидает доступ к интернету.";
        else if(_updates.Prepared!=null && _updates.Phase is "Verified" or "WaitingForDisconnect")
            UpdateStatusLabel.Text=_preferences.AutoInstallUpdates?"Пакет "+_updates.Prepared.Tag+" готов. Перед автоустановкой CITA Route отключит свой маршрут.":"Пакет "+_updates.Prepared.Tag+" готов к ручной установке.";
    }
    private async Task CheckUpdatesAsync(bool manual=false)
    {
        if(_render||_updateChecking||_updateDownloading||_updateInstalling||_exiting)return;
        _updateChecking=true;_lastUpdateCheck=DateTime.UtcNow;
        try
        {
            UpdateStatusLabel.Text="Проверяю GitHub Releases…";
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var http=ReleaseUpdates.CreateClient(_engineAvailable);
            _release=await new ReleaseUpdates(http).Check(timeout.Token);
            UpdateStatusLabel.Text=_release==null?"Новых стабильных версий нет. Текущая: "+ReleaseUpdates.Current.ToString(3):"Доступна CITA Route "+_release.Version.ToString(3);
            RefreshUpdateControls();
            if(manual&&_release==null)Notice(UpdateStatusLabel.Text);
            if(_release!=null && (manual||_updates.NotifiedTag!=_release.Tag))
            {
                _updates.NotifiedTag=_release.Tag;SaveUpdates();_pendingUpdatePrompt=true;
                if(IsVisible && IsActive && WindowState!=WindowState.Minimized)await ShowUpdatePromptAsync();
                else if(_tray!=null){_tray.BalloonTipTitle="CITA Route "+_release.Version.ToString(3);_tray.BalloonTipText="Доступно обновление. Открой приложение для загрузки.";_tray.ShowBalloonTip(7000);}
            }
        }
        catch(OperationCanceledException){if(!_exiting)UpdateStatusLabel.Text="Проверка обновлений не завершена. Можно повторить позже.";}
        catch(Exception){if(!_exiting)UpdateStatusLabel.Text="GitHub Releases временно недоступен. Можно повторить проверку позже.";}
        finally{_updateChecking=false;}
    }
    private async Task ShowUpdatePromptAsync()
    {
        if(!_pendingUpdatePrompt||_updatePromptOpen||_release==null||_busy||_updateInstalling||_updateDownloading||_rescueState.BlocksRouting||_exiting||!IsVisible||!IsActive||WindowState==WindowState.Minimized)return;
        _pendingUpdatePrompt=false;_updatePromptOpen=true;
        bool ready=_updates.Prepared?.Tag==_release.Tag;
        try
        {
            var prompt=new UpdatePrompt(_release,ready){Owner=this};prompt.ShowDialog();
            if(prompt.Accepted){if(ready)await InstallUpdateAsync(true);else await DownloadUpdateAsync();}
            else if(ready){_updates.DeferredUntilUtc=DateTime.UtcNow.AddHours(4);SaveUpdates();UpdateStatusLabel.Text="Автоустановка отложена на 4 часа. Можно установить вручную.";}
        }
        finally{_updatePromptOpen=false;}
    }
    private async void CheckUpdates_Click(object sender,RoutedEventArgs e)=>await CheckUpdatesAsync(true);
    private void OpenReleases_Click(object sender,RoutedEventArgs e)=>OpenPath((_release?.Page??new Uri("https://github.com/"+ReleaseUpdates.Repository+"/releases")).AbsoluteUri);
    private async void DownloadUpdate_Click(object sender,RoutedEventArgs e)=>await DownloadUpdateAsync();
    private async Task DownloadUpdateAsync()
    {
        if(_release==null||_updateDownloading||_updateInstalling||_busy)return;
        var release=_release;_updateDownloading=true;DownloadUpdateButton.IsEnabled=false;
        _updates.Phase="Downloading";_updates.Prepared=null;_updates.Error="";_updates.DeferredUntilUtc=DateTime.MinValue;_updates.NetworkRetries=0;_updates.NextRetryUtc=DateTime.MinValue;SaveUpdates();
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);timeout.CancelAfter(TimeSpan.FromMinutes(20));
            using var http=ReleaseUpdates.CreateClient(_engineAvailable);
            string package=await new ReleaseUpdates(http).Download(release,_root,new Progress<int>(p=>{if(!_exiting)UpdateStatusLabel.Text="Загрузка обновления: "+p+"%";}),timeout.Token);
            _updates.Prepared=UpdateJournal.Prepare(_root,release,package);_updates.Phase="Verified";SaveUpdates();
        }
        catch(OperationCanceledException){_updates.Phase="Failed";_updates.Error="Загрузка отменена или истекло время ожидания.";SaveUpdates();}
        catch(Exception ex){_updates.Phase="Failed";_updates.Error=Backend.Redact(ex.Message);SaveUpdates();}
        finally{_updateDownloading=false;RefreshUpdateControls();}
        await TryAutoInstallAsync();
    }
    private async Task TryAutoInstallAsync()
    {
        if(_render||_exiting||_updatePromptOpen||_updates.DeferredUntilUtc>DateTime.UtcNow||_updates.NextRetryUtc>DateTime.UtcNow||!_preferences.AutoInstallUpdates||_updates.Prepared==null||_updates.Phase is not ("Verified" or "WaitingForDisconnect" or "WaitingForNetwork") || (DateTime.UtcNow-_lastAutoAttempt).TotalSeconds<10)return;
        _lastAutoAttempt=DateTime.UtcNow;
        await InstallUpdateAsync(false);
    }
    private async void InstallUpdate_Click(object sender,RoutedEventArgs e)=>await InstallUpdateAsync(true);
    private async Task InstallUpdateAsync(bool manual)
    {
        if(_busy||_updateDownloading||_updateInstalling||_updates.Prepared==null||_exiting)return;
        if(manual){_updates.DeferredUntilUtc=DateTime.MinValue;_updates.NextRetryUtc=DateTime.MinValue;_updates.NetworkRetries=0;}
        RefreshRescueState();
        var currentSession=AppPaths.Read(Path.Combine(_root,"runtime","session.json"),()=>new EngineSession());
        bool owned=_backend.EngineRunning(currentSession)||(Cita.Startup.StartupPaths.Read() is {Active:true} startup&&startup.Owns(_root));
        string? blocked=UpdateJournal.PreparationBlockReason(_root,_busy,owned,_rescueState.BlocksRouting,UpdateJournal.GlobalEngineRunning());
        if(blocked!=null)
        {
            if(_updates.Phase!="Failed" && _updates.Phase!="WaitingForDisconnect"){_updates.Phase="WaitingForDisconnect";SaveUpdates();}
            UpdateStatusLabel.Text=blocked;if(manual)Notice(blocked);return;
        }
        _updateInstalling=true;_busy=true;RefreshRescueState();ConnectButton.IsEnabled=false;
        Process? installer=null;Task<string>? stdout=null,stderr=null;FileStream? preparation=null;
        try
        {
            var prepared=_updates.Prepared;
            preparation=UpdateJournal.ReservePreparation(_root);
            _updates.Phase="Verifying";_updates.Error="";SaveUpdates();UpdateStatusLabel.Text="Повторно проверяю выпуск и сохранённый пакет…";
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);timeout.CancelAfter(TimeSpan.FromMinutes(2));
            using var http=ReleaseUpdates.CreateClient(owned);
            var verified=await Task.Run(()=>new ReleaseUpdates(http).Restore(prepared,_root,timeout.Token),timeout.Token);
            // Finish every network-dependent verification before stopping this VPN.
            if(owned)
            {
                _updates.Phase="Disconnecting";SaveUpdates();UpdateStatusLabel.Text="Отключаем маршрут перед установкой…";SetConnectionStage("Stopping");
                var stopped=await ConnectionOperation.Run(_root,"Stop",true,progress:OperationProgress(),cancellation:_updateLifetime.Token);
                if(!stopped.Success)throw new IOException("Не удалось отключить маршрут: "+Backend.FriendlyError(stopped.Message));
                _engineRunning=false;_engineAvailable=false;ClearNativeQuality();
            }
            RefreshRescueState();
            string? afterStop=UpdateJournal.BlockReason(_root,false,false,_rescueState.BlocksRouting,UpdateJournal.GlobalEngineRunning());
            if(afterStop!=null)throw new IOException(afterStop);
            string id=Guid.NewGuid().ToString("N"),handoff=Path.Combine(_root,"runtime","update-handoff-"+id+".json");
            using var self=Process.GetCurrentProcess();
            string Literal(string value)=>"'"+value.Replace("'","''")+"'";
            string command="& "+Literal(Path.Combine(_root,"Install-Release.ps1"))+" -Package "+Literal(verified.Package)+" -Version "+Literal(prepared.Tag[1..])+" -GuiPid "+self.Id+" -GuiStartedUtc "+Literal(self.StartTime.ToUniversalTime().ToString("O"))+" -OperationId "+id+" -ManifestSha256 "+verified.ManifestHash;
            var info=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=_root,RedirectStandardOutput=true,RedirectStandardError=true,Arguments="-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(command))};
            _updates.Phase="Installing";_updates.OperationId=id;SaveUpdates();
            installer=Process.Start(info)??throw new IOException("Не удалось запустить установку.");
            stdout=installer.StandardOutput.ReadToEndAsync();stderr=installer.StandardError.ReadToEndAsync();
            while(true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if(File.Exists(handoff))
                {
                    var state=AppPaths.Read(handoff,()=>new UpdateHandoff());
                    if(state.Id==id && state.GuiPid==self.Id)
                    {
                        if(state.Phase=="Ready"){_busy=false;ExitApplication();return;}
                        if(state.Phase=="Failed")throw new IOException(state.Message);
                    }
                }
                if(installer.HasExited)throw new IOException("Установщик завершился до подтверждения. Подробности: logs/release-update-last.json.");
                await Task.Delay(100,timeout.Token);
            }
        }
        catch(Exception ex)
        {
            if(installer!=null && !installer.HasExited){try{installer.Kill(true);}catch{}}
            bool network=installer==null && (ex is System.Net.Http.HttpRequestException or OperationCanceledException) && !_exiting;
            _updates.Error=Backend.Redact(ex.GetBaseException().Message);
            if(network)UpdateJournal.DeferNetwork(_updates,DateTime.UtcNow);
            else _updates.Phase="Failed";
            SaveUpdates();if(manual||_updates.Phase=="Failed")Notice("Обновление не установлено: "+_updates.Error,true);
        }
        finally
        {
            // Streams are drained asynchronously; the installer is deliberately
            // allowed to outlive this GUI after acknowledging exclusive ownership.
            installer?.Dispose();preparation?.Dispose();_updateInstalling=false;_busy=false;_operationPhase="";++_uiOperationGeneration;
            if(!_exiting){ConnectButton.IsEnabled=true;RefreshRescueState();RefreshUpdateControls();}
        }
    }
    private void AutoInstallUpdates_Changed(object sender,RoutedEventArgs e)
    {
        if(!_ready||_render)return;
        _preferences.AutoInstallUpdates=AutoInstallUpdatesBox.IsChecked==true;SaveData();RefreshUpdateControls();
        if(_preferences.AutoInstallUpdates)_=TryAutoInstallAsync();
    }
    private sealed class UpdateHandoff
    {
        public string Id{get;set;}="";public string Phase{get;set;}="";public int GuiPid{get;set;}public string Message{get;set;}="";
    }
}
