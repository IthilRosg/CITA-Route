using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Windows;
using RoutePilot.Quality;

namespace RoutePilot;

public static class UdpQualityPresentation
{
    public static (string Text,string Detail) Describe(UdpQualityState state,UdpQualityOptions options,EngineSession session,bool owned,bool vpn,DateTime now)
    {
        if(!options.Enabled || !owned || !session.Running)return("не измерено","Измерение работает только при подключении и настроенном контрольном UDP-сервере.");
        if(state.Schema!=1 || state.SessionId!=session.Id || state.BridgeGeneration!=session.BridgeGeneration || state.SourceProfile!=session.SourceProfile ||
            state.NetworkSignature!=session.NetworkSignature || state.ConfigurationId!=options.Identity || !SessionHealthCache.Fresh(state.UpdatedUtc,now,15))
            return("нет проверки","Свежие данные текущего подключения ещё не получены.");
        if(session.Recovering || vpn && (!session.BridgeReady || session.VpnHoldReason.Length>0))return("нет проверки","Путь восстанавливается или не подтверждён.");
        var path=vpn?state.Vpn:state.Direct;
        if(!SessionHealthCache.Fresh(path.UpdatedUtc,now,25))return("нет проверки","Измерение устарело; старые результаты не используются.");
        if(path.Status=="Collecting")return("сбор данных","Собираем первые 10 проб. HTTPS не используется для оценки потерь UDP.");
        if(path.Status!="Measured" || path.Sent<10 || path.Sent>60 || path.Received<=0 || path.Received>path.Sent || path.LossPercent is not {} loss ||
            !double.IsFinite(loss) || Math.Abs(loss-100.0*(path.Sent-path.Received)/path.Sent)>0.11)
            return("нет проверки",path.Reason=="NoReplies"?"Контрольный сервер не ответил. Это не доказывает 100% потерь сети.":"Контрольный UDP-сервер или выбранный канал проверки недоступен.");
        string detail=$"{(vpn?"VPN":"Напрямую")} · {state.Transport}\nОтветы: {path.Received}/{path.Sent}; окно с {path.WindowStartedUtc.ToLocalTime():HH:mm:ss}; проверено {path.UpdatedUtc.ToLocalTime():HH:mm:ss}.\n"+
            $"Срок ответа: {path.DeadlineMs} мс. Повторы: {path.Duplicates}; вне порядка: {path.Reordered}; поздние: {path.Late}.\n"+
            "Доля проб без своевременного ответа в обе стороны. Маленькая выборка; это не потери всех приложений и не оценка одного направления.";
        return(loss.ToString("0.#",CultureInfo.CurrentCulture)+"%",detail);
    }
}

public partial class MainWindow
{
    void InitializeQuality()
    {
        var options=AppPaths.Read(Path.Combine(_root,"data","udp-quality.json"),()=>new UdpQualityOptions());
        QualityEnabledBox.IsChecked=options.Enabled;QualityAddressInput.Text=options.Address;
        QualityPortInput.Text=options.Port.ToString(CultureInfo.InvariantCulture);QualityKeyInput.Password=options.Key;
    }
    private void SaveQuality_Click(object sender,RoutedEventArgs e)
    {
        if(_render)return;
        try
        {
            var options=new UdpQualityOptions{Enabled=QualityEnabledBox.IsChecked==true,Address=QualityAddressInput.Text.Trim(),Key=QualityKeyInput.Password.Trim()};
            if(!int.TryParse(QualityPortInput.Text,out int port))throw new IOException("Укажи UDP-порт контрольного сервера.");options.Port=port;
            if(options.Enabled && (!options.ValidKey || options.Port is <1024 or >65535 || !IPAddress.TryParse(options.Address,out var ip) ||
                !UdpQualityOptions.PublicIpv4(ip)))
                throw new IOException("Укажи публичный IPv4, порт 1024–65535 и ключ из 64 шестнадцатеричных символов от своего контрольного сервера.");
            AppPaths.Write(Path.Combine(_root,"data","udp-quality.json"),options);
            Notice(options.Enabled?"Измерение UDP включено. Первые данные появятся после проверки контрольного сервера.":"Измерение UDP выключено.");
        }
        catch(Exception ex){Notice(ex.Message,true);}
    }
    void RefreshQuality(EngineSession session,bool owned)
    {
        var options=AppPaths.Read(Path.Combine(_root,"data","udp-quality.json"),()=>new UdpQualityOptions());
        var state=AppPaths.Read(Path.Combine(_root,"runtime","udp-quality.json"),()=>new UdpQualityState());
        var direct=UdpQualityPresentation.Describe(state,options,session,owned,false,DateTime.UtcNow);
        var vpn=UdpQualityPresentation.Describe(state,options,session,owned,true,DateTime.UtcNow);
        CompactDirectLoss.Text="Напрямую: "+direct.Text;CompactDirectLoss.ToolTip=direct.Detail;
        CompactVpnLoss.Text="VPN: "+vpn.Text;CompactVpnLoss.ToolTip=vpn.Detail;
        QualitySummary.Text=CompactDirectLoss.Text+" · "+CompactVpnLoss.Text;
    }
}
