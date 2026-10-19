using System;
using System.Globalization;
using RoutePilot.Telemetry;

namespace RoutePilot;

public static class NativeStatisticsPresentation
{
    public static (string Rtt,string Loss,string Detail) Describe(WireSockSample sample,DateTime now)
    {
        if(!sample.Available || sample.ConnectionState!=3 || !SessionHealthCache.Fresh(sample.ObservedUtc,now,12))
        {
            string reason=sample.Code switch
            {
                "UnsupportedVersion"=>"Эта версия WireSock пока не поддерживает чтение статистики в CITA Route.",
                "ForeignProfile"=>"WireSock использует другой профиль. Его показатели не относятся к этому подключению.",
                "UntrustedService"=>"Не удалось подтвердить владельца службы WireSock.",
                "NoStatistics"=>"WireSock ещё не передал статистику туннеля.",
                "Timeout"=>"Служба WireSock не ответила вовремя.",
                _=>"Свежая статистика туннеля временно недоступна. Это само по себе не означает обрыв VPN."
            };
            return("—","—",reason);
        }
        string rtt=sample.RttMs is >0 and <=120000?"≈"+sample.RttMs.Value.ToString(CultureInfo.CurrentCulture)+" мс":"—";
        string loss=sample.EstimatedLossPercent is {} value && double.IsFinite(value) && value is >=0 and <=100 && sample.RxBytes>0?
            "≈"+value.ToString("0.##",CultureInfo.CurrentCulture)+"%":"—";
        string detail="Внутренняя статистика действующего туннеля WireSock. Получена "+sample.ObservedUtc.ToLocalTime().ToString("HH:mm:ss")+".\n"+
            "Задержка оценена по последнему handshake. API не сообщает возраст этого handshake; это не постоянный ping.\n"+
            "Потери — оценка ядра WireSock; размер её окна API не сообщает. Она не описывает прямой трафик и все приложения.\n"+
            "Без входящего трафика оценка потерь не показывается. Отдельный UDP-сервер не требуется.";
        return(rtt,loss,detail);
    }
}
