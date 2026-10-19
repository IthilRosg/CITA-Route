using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Windows;
using System.Threading;
using System.Threading.Tasks;
using RoutePilot.Quality;
using RoutePilot.Telemetry;

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
    WireSockSample _nativeSample=new();
    string _nativeScope="";
    DateTime _nextNativeRead;
    CancellationTokenSource? _nativeStop;
    static string NativeScope(EngineSession session,string cli)=>session.Id+":"+session.SourceProfile+":"+session.NetworkSignature+":"+session.BridgeGeneration+":"+session.BridgePid+":"+cli;
    void ClearNativeQuality()
    {
        _nativeStop?.Cancel();_nativeSample=new();_nativeScope="";_nextNativeRead=DateTime.MinValue;
        CompactVpnRtt.Text="Задержка: —";CompactVpnLoss.Text="Потери: —";
        CompactVpnRtt.ToolTip=CompactVpnLoss.ToolTip="Статистика туннеля появится после подключения.";
        NativeQualitySummary.Text="Статистика туннеля WireSock ещё не получена.";
    }
    async Task<bool> RefreshQualityAsync(EngineSession session,bool owned)
    {
        bool eligible=owned && session.Running && session.BridgeReady && !session.Recovering && string.IsNullOrEmpty(session.VpnHoldReason);
        if(!eligible)ClearNativeQuality();
        else
        {
            string scope=NativeScope(session,_backend.CliPath);
            if(scope!=_nativeScope){ClearNativeQuality();_nativeScope=scope;}
            if(DateTime.UtcNow>=_nextNativeRead)
            {
                _nextNativeRead=DateTime.UtcNow.AddSeconds(3);
                _nativeStop?.Dispose();_nativeStop=new();int operation=_uiOperationGeneration;
                try
                {
                    var sample=await WireSockStatistics.Read(_backend.CliPath,stop:_nativeStop.Token);
                    var current=AppPaths.Read(Path.Combine(_root,"runtime","session.json"),()=>new EngineSession());
                    if(_exiting || _nativeStop.IsCancellationRequested || _nativeScope!=scope || operation!=_uiOperationGeneration || !current.Running || !current.BridgeReady || current.Recovering ||
                        !string.IsNullOrEmpty(current.VpnHoldReason) || NativeScope(current,_backend.CliPath)!=scope)
                    {ClearNativeQuality();return false;}
                    _nativeSample=sample;
                }
                catch(OperationCanceledException){ClearNativeQuality();return false;}
            }
            var native=NativeStatisticsPresentation.Describe(_nativeSample,DateTime.UtcNow);
            CompactVpnRtt.Text="Задержка: "+native.Rtt;CompactVpnRtt.ToolTip=native.Detail;
            CompactVpnLoss.Text="Потери: "+native.Loss;CompactVpnLoss.ToolTip=native.Detail;
        }
        NativeQualitySummary.Text="Туннель WireSock · "+CompactVpnRtt.Text+" · "+CompactVpnLoss.Text;
        var options=AppPaths.Read(Path.Combine(_root,"data","udp-quality.json"),()=>new UdpQualityOptions());
        var state=AppPaths.Read(Path.Combine(_root,"runtime","udp-quality.json"),()=>new UdpQualityState());
        var direct=UdpQualityPresentation.Describe(state,options,session,owned,false,DateTime.UtcNow);
        var vpn=UdpQualityPresentation.Describe(state,options,session,owned,true,DateTime.UtcNow);
        QualitySummary.Text="UDP-пробы · Напрямую: "+direct.Text+" · VPN: "+vpn.Text;
        QualitySummary.ToolTip=direct.Detail+"\n\n"+vpn.Detail;
        return true;
    }
}
