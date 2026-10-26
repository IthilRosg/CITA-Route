using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RoutePilot;

public partial class MainWindow
{
    public async Task BenchmarkPresentation(int rows=200,int seconds=30)
    {
        if(!_render)throw new InvalidOperationException("Resource fixture requires preview mode.");
        if(Environment.GetEnvironmentVariable("CITA_PRESENTATION_ROWS") is { } rowSetting)rows=int.Parse(rowSetting,System.Globalization.CultureInfo.InvariantCulture);
        if(Environment.GetEnvironmentVariable("CITA_PRESENTATION_SECONDS") is { } secondSetting)seconds=int.Parse(secondSetting,System.Globalization.CultureInfo.InvariantCulture);
        if(rows is not (200 or 1000))throw new ArgumentOutOfRangeException(nameof(rows));
        if(seconds is <1 or >30)throw new ArgumentOutOfRangeException(nameof(seconds));
        bool dynamic=Environment.GetEnvironmentVariable("CITA_PRESENTATION_DYNAMIC")=="1";
        var samples=new List<object>();
        var retained=new ConnectionRow{Id="check",Download=1};var changedProperties=new List<string?>();retained.PropertyChanged+=(_,e)=>changedProperties.Add(e.PropertyName);
        var displayed=new List<ConnectionRow>{retained};var incoming=new List<ConnectionRow>{new(){Id="check",Host="updated.example",Download=2048,IsVpn=true}};
        if(!ConnectionGridSnapshot.RefreshInPlace(displayed,incoming)||!ReferenceEquals(displayed[0],retained)||retained.Host!="updated.example"||!changedProperties.Contains("Downloaded")||!changedProperties.Contains("RouteColor"))throw new InvalidOperationException("Row notification dependencies failed.");
        if(ConnectionGridSnapshot.RefreshInPlace(displayed,new[]{new ConnectionRow{Id="replacement"}}))throw new InvalidOperationException("Changed membership reused an old row.");
        var label=new System.Windows.Controls.TextBlock();label.SetBinding(System.Windows.Controls.TextBlock.TextProperty,new System.Windows.Data.Binding(nameof(ConnectionRow.Downloaded)){Source=retained});
        retained.Download=4096;await Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.DataBind);
        if(label.Text!=retained.Downloaded)throw new InvalidOperationException("Visible downloaded counter did not update.");System.Windows.Data.BindingOperations.ClearAllBindings(label);
        displayed.Add(new ConnectionRow{Id="second",Download=8192});var sorted=System.Windows.Data.CollectionViewSource.GetDefaultView(displayed);
        sorted.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(ConnectionRow.Download),System.ComponentModel.ListSortDirection.Descending));
        if(((ConnectionRow)sorted.Cast<object>().First()).Id!="second")throw new InvalidOperationException("Initial row sorting failed.");
        ConnectionGridSnapshot.RefreshInPlace(displayed,new[]{new ConnectionRow{Id="check",Download=16384},new ConnectionRow{Id="second",Download=0}});
        if(((ConnectionRow)sorted.Cast<object>().First()).Id!="check")throw new InvalidOperationException("Updated row sorting failed.");sorted.SortDescriptions.Clear();
        using var process=Process.GetCurrentProcess();
        _connections=Enumerable.Range(0,rows).Select(i=>new ConnectionRow{Id=i.ToString(),Host="sample-"+i+".example",Process="fixture.exe",Network="TCP",IsVpn=i%3==0,Upload=i*1024,Download=i*32768}).ToList();
        UpdateConnectionGrids();var original=ConnectionsGrid.ItemsSource;var recent=RecentGrid.ItemsSource;ConnectionsGrid.SelectedItem=_connections[0];UpdateConnectionGrids();
        if(!ReferenceEquals(original,ConnectionsGrid.ItemsSource)||!ReferenceEquals(recent,RecentGrid.ItemsSource))throw new InvalidOperationException("Unchanged presentation rebuilt its grids.");
        _connections[0].Upload++;UpdateConnectionGrids();if(!ReferenceEquals(original,ConnectionsGrid.ItemsSource)||(ConnectionsGrid.SelectedItem as ConnectionRow)?.Id!=_connections[0].Id)throw new InvalidOperationException("Changed counter rebuilt the table or selected row was lost.");
        _connections[0].Upload--;ConnectionSearch.Text="sample-0.example";UpdateConnectionGrids();if(ConnectionsGrid.Items.Count!=1)throw new InvalidOperationException("Presentation filter did not refresh.");ConnectionSearch.Text="";UpdateConnectionGrids();
        foreach(bool compact in new[]{true,false})
        {
            ApplyView(compact,false);PreviewPage(compact?0:1);_engineRunning=true;SetStatus("Демонстрация статистики",true);
            // Warm up WPF resources before each measured interval.
            UpdateConnectionGrids();TrafficChart.SetPoints(_points);await Task.Delay(2000);
            process.Refresh();var cpu=process.TotalProcessorTime;var clock=Stopwatch.StartNew();
            long initialPrivate=process.PrivateMemorySize64,maxPrivate=initialPrivate,maxWorking=process.WorkingSet64;int initialHandles=process.HandleCount;
            double maxUpdateMs=0,totalUpdateMs=0;
            for(int tick=0;tick<seconds;tick++)
            {
                var update=Stopwatch.StartNew();
                if(dynamic){_connections=_connections.Select(c=>new ConnectionRow{Id=c.Id,Host=c.Host,Process=c.Process,Network=c.Network,IsVpn=c.IsVpn,IsDpi=c.IsDpi,Rule=c.Rule,Upload=c.Upload+256,Download=c.Download+1024,Address=c.Address,Started=c.Started}).ToList();
                    if(tick%5==0)_connections[rows-1]=new ConnectionRow{Id="new-"+tick,Host="rotated-"+tick+".example",Process="fixture.exe",Network="TCP"};}
                var now=DateTime.UtcNow;_points.Add(new(now,64000,1500000));_points.RemoveAll(p=>(now-p.Time).TotalSeconds>60);
                DownloadRateLabel.Text=Format.Rate(1500000+tick*1024);UploadRateLabel.Text=Format.Rate(64000+tick*256);
                UptimeLabel.Text="01:"+tick.ToString("00");CompactTraffic.Text=Format.Bytes(1500000L*(tick+1));
                if(!compact){TrafficChart.SetPoints(_points);UpdateConnectionGrids();}
                SyncCompact();update.Stop();totalUpdateMs+=update.Elapsed.TotalMilliseconds;maxUpdateMs=Math.Max(maxUpdateMs,update.Elapsed.TotalMilliseconds);
                if(dynamic&&!compact&&((ConnectionsGrid.SelectedItem as ConnectionRow)?.Id!="0"||(ConnectionsGrid.Items[0] as ConnectionRow)?.Download!=_connections[0].Download))throw new InvalidOperationException("Dynamic presentation lost selection or current counters.");
                await Task.Delay(1000);process.Refresh();
                maxPrivate=Math.Max(maxPrivate,process.PrivateMemorySize64);maxWorking=Math.Max(maxWorking,process.WorkingSet64);
            }
            process.Refresh();
            samples.Add(new{view=compact?"Compact":"Advanced",seconds=clock.Elapsed.TotalSeconds,
                meanUpdateMs=totalUpdateMs/seconds,maxUpdateMs,
                cpuMachinePercent=100*(process.TotalProcessorTime-cpu).TotalSeconds/clock.Elapsed.TotalSeconds/Environment.ProcessorCount,
                privateMiB=process.PrivateMemorySize64/1048576.0,privateDeltaMiB=(process.PrivateMemorySize64-initialPrivate)/1048576.0,
                maxPrivateMiB=maxPrivate/1048576.0,maxWorkingSetMiB=maxWorking/1048576.0,handleDelta=process.HandleCount-initialHandles});
        }
        AppPaths.Write(Path.Combine(_root,"logs","ui-resource-fixture.json"),new{kind="synthetic-presentation-only",updatedUtc=DateTime.UtcNow,
            rows,dynamic,gridReuseVerified=true,refreshSeconds=1,logicalProcessors=Environment.ProcessorCount,samples,
            notes="WPF rendering only; no live VPN, backend polling, external probes or network changes. Short samples cannot establish absence of leaks."});
        _engineRunning=false;
    }
}
