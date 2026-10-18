using System;
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
        ApplyView(true,false);Show();UpdateLayout();
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
        PreviewPage(2);ApplyView(true);
        Assert(_engineRunning && (string)ProfileInput.SelectedItem=="second" && _routingPolicy.Mode=="Lists" && _routingPolicy.AllowListedDpi,"Changing presentation must not change routing/profile state.");
        ApplyView(false);
        Assert(Math.Abs(Width-advancedWidth)<1 && Math.Abs(Height-advancedHeight)<1 && Pages.SelectedIndex==2,"Advanced size/page must survive a compact round trip.");
        ApplyView(true);
        _busy=true;_connectionCancellation=new();SetConnectionStage("Connecting");
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
