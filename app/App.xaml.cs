using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RoutePilot;

public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _activation;
    private RegisteredWaitHandle? _activationWait;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string root;
        try{root=AppPaths.FindRoot();}
        catch(Exception ex){MessageBox.Show(ex.Message,"RoutePilot",MessageBoxButton.OK,MessageBoxImage.Error);Shutdown(1);return;}
        if (e.Args.Contains("--self-test"))
        {
            Directory.CreateDirectory(Path.Combine(root,"runtime"));Directory.CreateDirectory(Path.Combine(root,"logs"));
            try { SelfTests.Run(root); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(root,"logs","app-self-test.log"),ex.ToString()); Shutdown(1); }
            return;
        }
        bool render = e.Args.Contains("--render") || e.Args.Contains("--render-all");
        if (!render)
        {
            string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root)))[..16];
            _activation = new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\RoutePilot-Activate-" + key);
            _instance = new Mutex(true,"Local\\RoutePilot-" + key,out bool first);
            if (!first) { _activation.Set(); Shutdown(); return; }
        }
        DispatcherUnhandledException += (_,args) => {
            Directory.CreateDirectory(Path.Combine(root,"logs"));
            File.AppendAllText(Path.Combine(root,"logs","app-error.log"),args.Exception + Environment.NewLine);
            MessageBox.Show(args.Exception.Message,"RoutePilot",MessageBoxButton.OK,MessageBoxImage.Error);
            args.Handled = true;
        };
        var window = new MainWindow(root,render);
        MainWindow = window;
        window.Show();
        if (!render)
            _activationWait = ThreadPool.RegisterWaitForSingleObject(_activation!,(_,_)=>
                Dispatcher.BeginInvoke(new Action(window.ShowWindow)),null,Timeout.Infinite,false);
        if (render)
        {
            await System.Threading.Tasks.Task.Delay(100);
            while(window.IsRefreshing)await System.Threading.Tasks.Task.Delay(50);
            await window.RefreshAsync();
            Directory.CreateDirectory(Path.Combine(root,"artifacts"));
            string[] names={"overview","connections","rules","logs","settings"};
            for(int page=0;page<(e.Args.Contains("--render-all")?5:1);page++)
            {
                window.PreviewPage(page);
                await System.Threading.Tasks.Task.Delay(150);
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                string name=page==0?"routepilot.png":"routepilot-"+names[page]+".png";
                using(var stream = File.Create(Path.Combine(root,"artifacts",name)))encoder.Save(stream);
            }
            window.ExitApplication();
        }
    }
    protected override void OnExit(ExitEventArgs e) { _activationWait?.Unregister(null); _activation?.Dispose(); _instance?.Dispose(); base.OnExit(e); }
}
