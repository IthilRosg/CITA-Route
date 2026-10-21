using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace RoutePilot;

public partial class MainWindow
{
    bool _exportingDiagnostics;
    private async void ExportDiagnostics_Click(object sender,RoutedEventArgs e)=>await ExportDiagnostics();
    async Task ExportDiagnostics()
    {
        if(_exportingDiagnostics)return;
        var picker=new SaveFileDialog{Title="Сохранить диагностику CITA Route",Filter="ZIP-архив (*.zip)|*.zip",DefaultExt=".zip",
            FileName="CITA-Route-diagnostics-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".zip",OverwritePrompt=true};
        if(picker.ShowDialog(this)!=true)return;
        // Write a sibling first: a failed collection must not destroy an older report.
        string temporary=picker.FileName+"."+Guid.NewGuid().ToString("N")+".tmp";
        _exportingDiagnostics=true;
        try
        {
            Notice("Собираем диагностику. Подключение продолжает работать.");
            await Task.Run(()=>Cita.Diagnostics.SessionEvidence.Export(_root,temporary));
            File.Move(temporary,picker.FileName,true);
            Notice("Диагностика сохранена: "+picker.FileName);Event("Диагностика сохранена пользователем.");
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {Notice("Не удалось сохранить диагностику. Выбери другую папку.",true);}
        finally{_exportingDiagnostics=false;try{File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}
    }
}
