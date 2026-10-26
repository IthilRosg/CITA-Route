using System;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace RoutePilot;

public partial class MainWindow
{
    public void VerifyPresentation()
    {
        if(!_render)throw new InvalidOperationException("UI fixtures require preview mode.");
        static void Assert(bool condition,string text){if(!condition)throw new Exception(text);}
        var selection=new ConnectionSelection(@"C:\Program Files\WireSock\cli.exe","fixture","");
        var local=new AppliedConnection{SourceProfile=selection.Profile,CliPath=selection.Cli};
        var applied=new AppliedConnection{WireSockSourceProfile=selection.Profile,WireSockCliPath=selection.Cli,DataDirectory=@"C:\ProgramData\RoutePilot",AppRoot=_root};
        Assert(ConnectionBindingMatches(local,applied,selection,_root,@"C:\ProgramData\RoutePilot"),"A configured connection must not request setup because its protected data directory differs from the workspace.");
        applied.AppRoot=_root+"-other";
        Assert(!ConnectionBindingMatches(local,applied,selection,_root,@"C:\ProgramData\RoutePilot"),"Another portable installation must apply its own protected connection.");applied.AppRoot=_root;
        applied.ExpectedEgressIp="1.1.1.1";
        Assert(!ConnectionBindingMatches(local,applied,selection,_root,@"C:\ProgramData\RoutePilot"),"A different protected egress setting requires setup.");
        // Exercise real WPF layout without flashing test windows on the user's
        // desktop, adding taskbar entries or stealing focus from another app.
        ShowActivated=false;ShowInTaskbar=false;Opacity=0;
        ApplyView(true,false);Show();UpdateLayout();
        Assert(!ShowActivated&&!ShowInTaskbar&&Opacity==0,"UI self-tests must remain invisible and must not request activation.");
        Assert(Width<=460 && Height<=520 && CompactHost.IsVisible && !AdvancedHost.IsVisible,"Compact mode must use a physically small window.");
        _inventory=new(){CliPath="fixture.exe",Profiles=new[]{"first","second"}};
        ProfileInput.ItemsSource=_inventory.Profiles;ProfileInput.SelectedItem="second";
        _routingPolicy.Mode="Lists";_routingPolicy.AllowListedDpi=true;
        _engineRunning=true;SetStatus("VPN проверен",true);SyncCompact();
        Assert((string)CompactConnectButton.Content=="Отключить"&&!CompactProfileButton.IsEnabled,"Connected compact view must disconnect and lock profile changes.");
        ApplyView(false);
        double advancedWidth=Math.Max(MinWidth,Math.Min(1200,SystemParameters.WorkArea.Width-24)),advancedHeight=Math.Max(MinHeight,Math.Min(780,SystemParameters.WorkArea.Height-24));
        Width=advancedWidth;Height=advancedHeight;UpdateLayout();
        Assert(AdvancedHost.IsVisible&&!CompactHost.IsVisible,"Advanced view must retain the complete interface.");
        _connections=new(){new(){Id="grid",Host="example.com",Upload=1}};UpdateConnectionGrids();
        var stableGrid=ConnectionsGrid.ItemsSource;var stableRecent=RecentGrid.ItemsSource;UpdateConnectionGrids();
        Assert(ReferenceEquals(stableGrid,ConnectionsGrid.ItemsSource)&&ReferenceEquals(stableRecent,RecentGrid.ItemsSource),"Unchanged connection snapshots must preserve DataGrid bindings instead of rebuilding rows each tick.");
        var displayedRow=ConnectionsGrid.Items.Cast<ConnectionRow>().Single();int counterUpdates=0;
        displayedRow.PropertyChanged+=(_,eventArgs)=>{if(eventArgs.PropertyName==nameof(ConnectionRow.Uploaded))counterUpdates++;};
        _connections[0].Upload=2;UpdateConnectionGrids();
        Assert(ReferenceEquals(stableGrid,ConnectionsGrid.ItemsSource)&&displayedRow.Uploaded==Format.Bytes(2)&&counterUpdates>0,"A reused row must notify the bound traffic counter without rebuilding the grid.");
        counterUpdates=0;_connections=new(){new(){Id="grid",Host="example.com",Upload=3}};UpdateConnectionGrids();
        Assert(ReferenceEquals(stableGrid,ConnectionsGrid.ItemsSource)&&displayedRow.Uploaded==Format.Bytes(3)&&counterUpdates>0,"A fresh snapshot with the same connection ID must update the existing bound counter.");
        _inventory=new(){Kind="ApplicationOnly",ExecutablePath="fixture.exe",Version="2.4.23.1"};RefreshWireSockCompatibility();
        Assert(InstallWireSockButton.Visibility==Visibility.Visible&&WireSockCompatibilityLabel.Text.Contains("несовместимая"),"WireSock 2.x must offer an upgrade instead of asking for a nonexistent CLI.");
        _inventory=new(){Kind="SecureConnectCli",CliPath="fixture.exe",Version="3.7.2.1",CliTrusted=true,ManagementReady=true};RefreshWireSockCompatibility();
        Assert(InstallWireSockButton.Visibility==Visibility.Collapsed&&WireSockCompatibilityLabel.Text.Contains("новее"),"A newer WireSock cannot be offered a silent downgrade.");
        _inventory=new(){CliPath="fixture.exe",Profiles=new[]{"first","second"}};
        PreviewPage(2);ApplyView(true);
        Assert(_engineRunning && (string)ProfileInput.SelectedItem=="second" && _routingPolicy.Mode=="Lists" && _routingPolicy.AllowListedDpi,"Changing presentation must not change routing/profile state.");
        ApplyView(false);
        Assert(Math.Abs(Width-advancedWidth)<1 && Math.Abs(Height-advancedHeight)<1 && Pages.SelectedIndex==2,"Advanced size/page must survive a compact round trip.");
        ApplyView(true);
        _busy=true;_connectionCancellation=new();SetConnectionStage("Connecting");
        Assert(CompactVpnRtt.Text=="Задержка: —" && CompactVpnLoss.Text=="Потери: —","A new network operation must clear previous tunnel metrics.");
        Assert((string)CompactConnectButton.Content=="Отменить"&&CompactConnectButton.IsEnabled,"Connecting must expose cancellation.");
        var old=OperationProgress();old.Report(new(){Phase="Verifying"});++_uiOperationGeneration;
        Dispatcher.Invoke(()=>{},DispatcherPriority.Background);
        Assert(_operationPhase=="Connecting","An old queued progress event must not replace a newer operation.");
        _connectionCancellation.Cancel();SetConnectionStage("Cancelling");
        Assert(!CompactConnectButton.IsEnabled && (string)CompactConnectButton.Content=="Отменяем…","Cancellation must block duplicate starts until cleanup finishes.");
        _connectionCancellation.Dispose();_connectionCancellation=null;
        _busy=false;_engineRunning=false;_operationPhase="";SetStatus("Отключено",false);SyncCompact();
        Assert((string)CompactConnectButton.Content=="Подключить"&&CompactConnectButton.IsEnabled,"Completed cleanup must restore Connect.");
        Assert(WindowPlacement.Bounded(double.NaN,420,380,800)==420 && WindowPlacement.Bounded(5000,420,380,800)==800,"Invalid/off-screen saved sizes must be clamped.");
        _exiting=true;Closing-=OnClosing;Close();_backend.Dispose();
    }
}
