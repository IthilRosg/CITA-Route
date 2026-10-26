using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace RoutePilot;

public partial class MainWindow
{
    bool _routeDiagnosticBusy;
    async void DiagnoseRoute_Click(object sender,RoutedEventArgs e)
    {
        if(_render||_routeDiagnosticBusy||_busy||_exiting)return;
        _routeDiagnosticBusy=true;DiagnoseRouteButton.IsEnabled=false;
        try
        {
            string domain=RouteDiagnosticPresentation.Domain(DiagnoseRouteInput.Text);
            RouteDiagnosticLabel.Text="Сравниваем DNS и HTTPS через прямой выход, DPI и VPN. До 12 секунд…";
            string agent=Path.Combine(_root,"runtime","agent-build","RoutePilot.Agent.exe");
            var result=await _backend.RunAsync(agent,new[]{"--diagnose-route",Cita.Startup.StartupPaths.RoutingRoot(_root),domain},15000);
            if(result.Code!=0)throw new IOException("Проверка не завершена. Убедись, что маршрут подключён, и повтори после восстановления.");
            if(result.Output.Length>16384)throw new IOException("Ответ проверки превышает допустимый размер.");
            using var report=JsonDocument.Parse(result.Output);
            RouteDiagnosticLabel.Text=RouteDiagnosticPresentation.Describe(report.RootElement,domain);
        }
        catch(Exception ex){RouteDiagnosticLabel.Text=Backend.FriendlyError(ex.Message);}
        finally{_routeDiagnosticBusy=false;DiagnoseRouteButton.IsEnabled=true;}
    }
}
