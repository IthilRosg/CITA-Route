using System;

namespace RoutePilot;

public partial class MainWindow
{
    public void PreviewConnection(int state)
    {
        if(!_render)throw new InvalidOperationException("Connection previews cannot run in a live window.");
        _connectionCancellation?.Dispose();_connectionCancellation=null;_busy=false;_operationPhase="";
        _lastNoticeError=false;_lastNoticeAt=DateTime.MinValue;
        _engineRunning=state>=2;
        ProfileInput.ItemsSource=new[]{"Личный AmneziaWG"};ProfileInput.SelectedIndex=0;
        DownloadRateLabel.Text=state>=2?"3,2 МБ/с":"—";UploadRateLabel.Text=state>=2?"128 КБ/с":"—";
        UptimeLabel.Text=state>=2?"12:45":"—";CompactTraffic.Text=state>=2?"428 МБ":"—";
        CompactVpnRtt.Text=state==2?"Задержка: ≈48 мс":"Задержка: —";
        CompactVpnLoss.Text=state==2?"Потери: ≈0,2%":"Потери: —";
        if(state==1){_busy=true;_connectionCancellation=new();SetConnectionStage("Verifying");}
        else if(state==2){SetStatus("VPN проверен",true);FooterStatus.Text="Соединение через выбранный VPN проверено.";}
        else if(state==3){SetStatus("Восстановление VPN",false);FooterStatus.Text="Проверяем WireSock и восстанавливаем канал VPN…";}
        else if(state==4){SetStatus("Проверь подключение",false);FooterStatus.Text="Выход VPN не подтверждён. Отключи маршрут и проверь профиль.";}
        else{SetStatus("Отключено",false);FooterStatus.Text="Готово к подключению";}
        SyncCompact();UpdateLayout();
    }
}
