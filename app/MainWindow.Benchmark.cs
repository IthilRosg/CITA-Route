using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RoutePilot;

public partial class MainWindow
{
    public async Task BenchmarkPresentation()
    {
        if(!_render)throw new InvalidOperationException("Resource fixture requires preview mode.");
        var samples=new List<object>();
        using var process=Process.GetCurrentProcess();
        _connections=Enumerable.Range(0,200).Select(i=>new ConnectionRow{Id=i.ToString(),Host="sample-"+i+".example",Process="fixture.exe",Network="TCP",IsVpn=i%3==0,Upload=i*1024,Download=i*32768}).ToList();
        foreach(bool compact in new[]{true,false})
        {
            ApplyView(compact,false);PreviewPage(0);_engineRunning=true;SetStatus("Демонстрация статистики",true);
            // Warm up WPF resources before each measured interval.
            UpdateConnectionGrids();TrafficChart.SetPoints(_points);await Task.Delay(2000);
            process.Refresh();var cpu=process.TotalProcessorTime;var clock=Stopwatch.StartNew();
            long initialPrivate=process.PrivateMemorySize64,maxPrivate=initialPrivate,maxWorking=process.WorkingSet64;int initialHandles=process.HandleCount;
            for(int tick=0;tick<30;tick++)
            {
                var now=DateTime.UtcNow;_points.Add(new(now,64000,1500000));_points.RemoveAll(p=>(now-p.Time).TotalSeconds>60);
                DownloadRateLabel.Text=Format.Rate(1500000+tick*1024);UploadRateLabel.Text=Format.Rate(64000+tick*256);
                UptimeLabel.Text="01:"+tick.ToString("00");CompactTraffic.Text=Format.Bytes(1500000L*(tick+1));
                if(!compact){TrafficChart.SetPoints(_points);UpdateConnectionGrids();}
                SyncCompact();await Task.Delay(1000);process.Refresh();
                maxPrivate=Math.Max(maxPrivate,process.PrivateMemorySize64);maxWorking=Math.Max(maxWorking,process.WorkingSet64);
            }
            process.Refresh();
            samples.Add(new{view=compact?"Compact":"Advanced",seconds=clock.Elapsed.TotalSeconds,
                cpuMachinePercent=100*(process.TotalProcessorTime-cpu).TotalSeconds/clock.Elapsed.TotalSeconds/Environment.ProcessorCount,
                privateMiB=process.PrivateMemorySize64/1048576.0,privateDeltaMiB=(process.PrivateMemorySize64-initialPrivate)/1048576.0,
                maxPrivateMiB=maxPrivate/1048576.0,maxWorkingSetMiB=maxWorking/1048576.0,handleDelta=process.HandleCount-initialHandles});
        }
        AppPaths.Write(Path.Combine(_root,"logs","ui-resource-fixture.json"),new{kind="synthetic-presentation-only",updatedUtc=DateTime.UtcNow,
            rows=200,refreshSeconds=1,logicalProcessors=Environment.ProcessorCount,samples,
            notes="WPF rendering only; no live VPN, backend polling, external probes or network changes. Short samples cannot establish absence of leaks."});
        _engineRunning=false;
    }
}
