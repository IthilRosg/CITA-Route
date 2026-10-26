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
        if(InstallationMaintenance.Problem(root) is string maintenanceProblem)
        {
            if(e.Args.Contains("--self-test"))
            {
                Directory.CreateDirectory(Path.Combine(root,"logs"));
                File.WriteAllText(Path.Combine(root,"logs","app-self-test.log"),maintenanceProblem);
                Shutdown(1);return;
            }
            MessageBox.Show(maintenanceProblem,"CITA Route",MessageBoxButton.OK,MessageBoxImage.Information);Shutdown(1);return;
        }
        if (e.Args.Contains("--self-test"))
        {
            Directory.CreateDirectory(Path.Combine(root,"runtime"));Directory.CreateDirectory(Path.Combine(root,"logs"));
            try { SelfTests.Run(root); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(root,"logs","app-self-test.log"),ex.ToString()); Shutdown(1); }
            return;
        }
        bool render = e.Args.Contains("--render") || e.Args.Contains("--render-all") || e.Args.Contains("--render-update") || e.Args.Contains("--render-resources") || e.Args.Contains("--benchmark-ui");
        if (!render)
        {
            string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root)))[..16];
            _activation = new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\RoutePilot-Activate-" + key);
            _instance = new Mutex(true,"Local\\RoutePilot-" + key,out bool first);
            if (!first) { if(!e.Args.Contains("--startup"))_activation.Set(); Shutdown(); return; }
        }
        DispatcherUnhandledException += (_,args) => {
            Directory.CreateDirectory(Path.Combine(root,"logs"));
            File.AppendAllText(Path.Combine(root,"logs","app-error.log"),args.Exception + Environment.NewLine);
            MessageBox.Show(args.Exception.Message,"RoutePilot",MessageBoxButton.OK,MessageBoxImage.Error);
            args.Handled = true;
        };
        var window = new MainWindow(root,render);
        if(render)window.ApplyView(e.Args.Contains("--compact"),false);
        MainWindow = window;
        if (!render)
            _activationWait = ThreadPool.RegisterWaitForSingleObject(_activation!,(_,_)=>
                Dispatcher.BeginInvoke(new Action(window.ShowWindow)),null,Timeout.Infinite,false);
        if(!render&&e.Args.Contains("--startup"))await window.InitializeBackgroundAsync();
        else window.Show();
        if (render)
        {
            if(e.Args.Contains("--benchmark-ui")){await window.BenchmarkPresentation();window.ExitApplication();return;}
            await System.Threading.Tasks.Task.Delay(100);
            while(window.IsRefreshing)await System.Threading.Tasks.Task.Delay(50);
            await window.RefreshAsync();
            Directory.CreateDirectory(Path.Combine(root,"artifacts"));
            if(e.Args.Contains("--render-update"))
            {
                var version=new Version(ReleaseUpdates.Current.Major,ReleaseUpdates.Current.Minor,ReleaseUpdates.Current.Build+1);
                var prompt=new UpdatePrompt(new ReleaseUpdate(version,"v"+version,new Uri("https://github.com/IthilRosg/CITA-Route/releases"),new Uri("https://github.com/archive.zip"),new Uri("https://github.com/SHA256SUMS.txt"),"preview.zip",219000000,
                    "• Обновление сохраняется между запусками.\n• Автоустановка ждёт отключения маршрута.\n• Последний профиль восстанавливается автоматически.\n\nПрофили и пользовательские правила сохраняются."),false){Owner=window};
                prompt.Show();await System.Threading.Tasks.Task.Delay(150);prompt.UpdateLayout();
                var preview=new RenderTargetBitmap((int)prompt.ActualWidth,(int)prompt.ActualHeight,96,96,PixelFormats.Pbgra32);preview.Render(prompt);
                var output=new PngBitmapEncoder();output.Frames.Add(BitmapFrame.Create(preview));
                using(var file=File.Create(Path.Combine(root,"artifacts","update-popup.png")))output.Save(file);
                prompt.Close();window.ExitApplication();return;
            }
            string[] names=e.Args.Contains("--compact")?new[]{"disconnected","connecting","connected","recovering","attention"}:new[]{"overview","connections","rules","logs","settings"};
            for(int page=0;page<(e.Args.Contains("--render-all")?5:1);page++)
            {
                if(e.Args.Contains("--compact"))window.PreviewConnection(page);else window.PreviewPage(page);
                if(e.Args.Contains("--render-resources"))window.PreviewResourceWarning();
                await System.Threading.Tasks.Task.Delay(150);
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                string prefix=e.Args.Contains("--compact")?"routepilot-compact":"routepilot";
                if(e.Args.Contains("--render-resources"))prefix+="-resources";
                string name=page==0?prefix+".png":prefix+"-"+names[page]+".png";
                using(var stream = File.Create(Path.Combine(root,"artifacts",name)))encoder.Save(stream);
            }
            if(e.Args.Contains("--render-all")&&!e.Args.Contains("--compact"))
            {
                window.PreviewSettingsEnd();await System.Threading.Tasks.Task.Delay(150);window.UpdateLayout();
                var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream=File.Create(Path.Combine(root,"artifacts",e.Args.Contains("--compact")?"routepilot-compact-updates.png":"routepilot-updates.png"));encoder.Save(stream);
            }
            window.ExitApplication();
        }
    }
    protected override void OnExit(ExitEventArgs e) { _activationWait?.Unregister(null); _activation?.Dispose(); _instance?.Dispose(); base.OnExit(e); }
}
