using System;
using RoutePilot.Telemetry;

namespace RoutePilot;

public static class NativeStatisticsPresentationTests
{
    public static void Run()
    {
        static void Assert(bool value,string message){if(!value)throw new Exception(message);}
        DateTime now=DateTime.UtcNow;
        var sample=new WireSockSample{Available=true,ConnectionState=3,ObservedUtc=now,RttMs=46,EstimatedLossPercent=0.23,RxBytes=100};
        var view=NativeStatisticsPresentation.Describe(sample,now);
        Assert(view.Rtt.Contains("46")&&view.Loss.Contains('%')&&view.Detail.Contains("handshake"),"The compact view must show native RTT/loss and explain the handshake estimate without an optional reflector.");
        sample.ObservedUtc=now.AddSeconds(-13);Assert(NativeStatisticsPresentation.Describe(sample,now).Rtt=="—","A stopped or stale native reader must not preserve old metrics.");
        sample.ObservedUtc=now.AddSeconds(10);Assert(NativeStatisticsPresentation.Describe(sample,now).Loss=="—","A future sample cannot prove freshness.");sample.ObservedUtc=now;
        sample.Available=false;sample.Code="Timeout";Assert(NativeStatisticsPresentation.Describe(sample,now).Rtt=="—","A failed read must replace previously valid metrics with unavailable.");sample.Available=true;
        sample.RxBytes=0;Assert(NativeStatisticsPresentation.Describe(sample,now).Loss=="—","No traffic must not show an invented zero loss.");sample.RxBytes=100;
        sample.EstimatedLossPercent=0;Assert(NativeStatisticsPresentation.Describe(sample,now).Loss=="≈0%","A genuine zero native estimate with received traffic may be displayed.");
        sample.EstimatedLossPercent=double.NaN;Assert(NativeStatisticsPresentation.Describe(sample,now).Loss=="—","Invalid percentages cannot enter the compact view.");
        sample.ConnectionState=2;Assert(NativeStatisticsPresentation.Describe(sample,now).Rtt=="—","A connecting tunnel cannot display old connected values.");
    }
}
