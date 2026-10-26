using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace RoutePilot;

public partial class MainWindow
{
    void AutoUpdateWireSock_Changed(object sender,RoutedEventArgs e)
    {
        if(!_ready)return;
        _preferences.AutoUpdateWireSock=AutoUpdateWireSockBox.IsChecked==true;SaveData();
    }
    async Task CheckWireSockMaintenanceAsync()
    {
        var assessment=WireSockCompatibility.Assess(_inventory);
        if(!assessment.OfferInstall)return;
        if(_preferences.AutoUpdateWireSock&&assessment.Code=="UpdateAvailable"&&!_busy&&!_engineRunning&&
            !_rescueState.BlocksRouting&&!UpdateJournal.GlobalEngineRunning()&&!Cita.Startup.StartupPaths.Read().Active)
        {await InstallWireSockAsync(true);return;}
        string key=assessment.Code+":"+_inventory.Version+":"+WireSockCompatibility.Reviewed;
        if(_preferences.WireSockNotifiedVersion==key)return;
        _preferences.WireSockNotifiedVersion=key;SaveData();Notice(assessment.Message,!assessment.CanManage);
    }
    void RefreshWireSockCompatibility()
    {
        var assessment=WireSockCompatibility.Assess(_inventory);
        WireSockCompatibilityLabel.Text=assessment.Message;
        InstallWireSockButton.Visibility=assessment.OfferInstall?Visibility.Visible:Visibility.Collapsed;
    }
    async void InstallWireSock_Click(object sender,RoutedEventArgs e)=>await InstallWireSockAsync(false);
    async Task InstallWireSockAsync(bool automatic)
    {
        if(_render||_busy||_updateInstalling||_updateDownloading||_exiting||_rescueState.BlocksRouting)return;
        var assessment=WireSockCompatibility.Assess(_inventory);
        if(!assessment.OfferInstall){Notice(assessment.Message);return;}
        if(automatic&&assessment.Code!="UpdateAvailable")return;
        if(!automatic&&MessageBox.Show(this,"Будет загружен официальный WireSock Secure Connect "+WireSockDependency.Reviewed.Version+
            ". После проверки загрузки CITA отключит свой маршрут и откроет установщик. Другие подключения WireSock нужно отключить самостоятельно. Сохрани исходные конфиги перед переходом со старой редакции.\n\nПродолжить?","WireSock · установка и обновление",MessageBoxButton.OKCancel,MessageBoxImage.Information)!=MessageBoxResult.OK)return;
        _busy=true;RefreshRescueState();SyncCompact();
        string folder=Path.Combine(_root,"runtime","dependencies",Guid.NewGuid().ToString("N"));
        IDisposable? preparation=null;
        bool armed=false;
        bool coordinated=false;
        try
        {
            Directory.CreateDirectory(folder);
            // Shared reservation covers other CITA copies and the full lifetime
            // of the vendor installer, including its UAC prompt.
            preparation=UpdateJournal.ReservePreparation(_root);
            var session=AppPaths.Read(Path.Combine(_root,"runtime","session.json"),()=>new EngineSession());
            bool owned=_backend.EngineRunning(session);
            string? blocked=UpdateJournal.PreparationBlockReason(_root,false,owned,_rescueState.BlocksRouting,UpdateJournal.GlobalEngineRunning());
            if(blocked!=null)throw new IOException(blocked);
            SetConnectionStage("DownloadingWireSock");
            WireSockCompatibilityLabel.Text="Загружаем проверенный установщик WireSock…";
            string installerFile=Path.Combine(folder,"wiresock-secure-connect-x64-3.6.1.1.exe");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);timeout.CancelAfter(automatic?TimeSpan.FromSeconds(30):TimeSpan.FromMinutes(3));
            using(var http=ReleaseUpdates.CreateClient(owned))await WireSockDependency.Download(http,WireSockDependency.Reviewed,installerFile,timeout.Token);
            using var verifiedFile=new FileStream(installerFile,FileMode.Open,FileAccess.Read,FileShare.Read);
            await WireSockDependency.Verify(installerFile,_backend);
            if(owned)
            {
                SetConnectionStage("Stopping");
                var stopped=await ConnectionOperation.Run(_root,"Stop",true,progress:OperationProgress(),maintenanceReserved:true);
                if(!stopped.Success)throw new IOException("Не удалось отключить маршрут перед обновлением WireSock: "+stopped.Message);
                _engineRunning=false;_engineAvailable=false;ClearNativeQuality();
            }
            RefreshRescueState();
            blocked=UpdateJournal.BlockReason(_root,false,false,_rescueState.BlocksRouting,UpdateJournal.GlobalEngineRunning());
            if(blocked!=null)throw new IOException(blocked);
            await DiscoverConnectionsAsync();
            if(!WireSockCompatibility.Assess(_inventory).OfferInstall)throw new IOException("Установка WireSock изменилась во время загрузки. Повтори проверку совместимости.");
            if(_rescue.Installed)
            {
                var reservation=await _rescue.Command("begin-operation");
                if(!reservation.Success)throw new IOException("Обслуживание WireSock отложено: "+reservation.Message);
                coordinated=true;
            }
            await WireSockDependency.AssertIdle(_inventory,_backend);
            SetConnectionStage("InstallingWireSock");
            WireSockCompatibilityLabel.Text=automatic?"Обновляем WireSock. Дождись завершения…":"Открыт официальный установщик WireSock. Заверши установку…";
            var start=new ProcessStartInfo(installerFile){UseShellExecute=true,Verb="runas",WorkingDirectory=folder,Arguments=automatic?"/s":""};
            WireSockMaintenance.Write(_root,"Armed");armed=true;
            using var installer=Process.Start(start)??throw new IOException("Не удалось запустить установщик WireSock.");
            WireSockMaintenance.Write(_root,"Installing",installer);
            // Never kill a vendor driver installer or release the gates while it
            // is still running. The user can cancel in the installer itself.
            await installer.WaitForExitAsync();
            if(installer.ExitCode is not (0 or 3010))throw new IOException("Установщик WireSock завершился с кодом "+installer.ExitCode+". Проверь установку перед подключением.");
            await DiscoverConnectionsAsync();
            if(WireSockCompatibility.Parse(_inventory.Version)!=WireSockCompatibility.Reviewed||_inventory.CliPath.Length==0||_inventory.Message.Length>0)
                throw new IOException("Установщик завершён, но готовность WireSock ещё не подтверждена. Повтори поиск после завершения установки или перезагрузки.");
            WireSockMaintenance.Write(_root,"Verified");armed=false;
            Notice(installer.ExitCode==3010?"WireSock обновлён. Установщик запросил перезагрузку Windows.":"WireSock готов. Выбери исходный профиль и примени подключение.");
        }
        catch(Win32Exception ex)when(ex.NativeErrorCode==1223){WireSockMaintenance.Write(_root,"Cancelled");armed=false;Notice("Установка WireSock отменена.");}
        catch(Exception ex){Notice(Backend.FriendlyError(ex.Message),true);}
        finally
        {
            if(armed)try{WireSockMaintenance.Write(_root,"NeedsAttention");}catch(IOException){}catch(UnauthorizedAccessException){}
            if(coordinated)try{await _rescue.Command("end-operation");}catch(Exception){Notice("Проверь состояние службы восстановления после обслуживания WireSock.",true);}
            preparation?.Dispose();
            // Only remove this operation's freshly allocated download directory.
            string allowed=Path.GetFullPath(Path.Combine(_root,"runtime","dependencies"))+Path.DirectorySeparatorChar;
            if(Path.GetFullPath(folder).StartsWith(allowed,StringComparison.OrdinalIgnoreCase))
                try{Directory.Delete(folder,true);}catch(IOException){}catch(UnauthorizedAccessException){}
            _busy=false;_operationPhase="";++_uiOperationGeneration;RefreshWireSockCompatibility();RefreshRescueState();await RefreshAsync();SyncCompact();
        }
    }
}
