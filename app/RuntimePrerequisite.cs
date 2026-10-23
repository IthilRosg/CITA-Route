using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RoutePilot;

internal sealed record RuntimeDownload(Version Version,Uri Url,string Sha512);
internal static class RuntimePrerequisite
{
    internal const long MaximumBytes=100L*1024*1024;
    internal static RuntimeDownload Parse(JsonElement value,Version required)
    {
        string version=value.GetProperty("version").GetString()??"";
        string hash=value.GetProperty("sha512").GetString()??"";
        string address=value.GetProperty("url").GetString()??"";
        if(!Regex.IsMatch(version,@"\A10\.0\.\d+\z")||!Version.TryParse(version,out var actual)||actual<required||
           actual<DesktopRuntime.Minimum||!Regex.IsMatch(hash,@"\A[a-fA-F0-9]{128}\z")||
           address!="https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/"+version+"/windowsdesktop-runtime-"+version+"-win-x64.exe")
            throw new InvalidDataException("Некорректные данные компонента Microsoft .NET.");
        return new(actual,new Uri(address),hash.ToUpperInvariant());
    }
    internal static async Task Download(HttpClient http,RuntimeDownload spec,string destination,CancellationToken token)
    {
        using var response=await http.GetAsync(spec.Url,HttpCompletionOption.ResponseHeadersRead,token);
        response.EnsureSuccessStatusCode();
        if(response.RequestMessage?.RequestUri!=spec.Url)throw new InvalidDataException("Неожиданное перенаправление загрузки .NET.");
        if(response.Content.Headers.ContentLength is long length && (length<=0||length>MaximumBytes))throw new InvalidDataException("Недопустимый размер компонента .NET.");
        await using var input=await response.Content.ReadAsStreamAsync(token);
        await using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true);
        byte[] buffer=new byte[65536];long total=0;int count;
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        while((count=await input.ReadAsync(buffer,token))!=0)
        {
            total+=count;if(total>MaximumBytes)throw new InvalidDataException("Компонент .NET превышает допустимый размер.");
            hash.AppendData(buffer,0,count);await output.WriteAsync(buffer.AsMemory(0,count),token);
        }
        if(total==0||(response.Content.Headers.ContentLength is long expected&&total!=expected)||Convert.ToHexString(hash.GetHashAndReset())!=spec.Sha512)
            throw new InvalidDataException("Проверка SHA-512 компонента .NET не пройдена.");
    }
    internal static async Task Ensure(string package,HttpClient http,IProgress<string>? progress,CancellationToken token)
    {
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(package,"manifest.json")));
        if(manifest.RootElement.GetProperty("selfContained").GetBoolean())return;
        var required=Version.Parse(manifest.RootElement.GetProperty("desktopRuntimeMinimum").GetString()!);
        if(DesktopRuntime.Available(required))return;
        string metadata=Path.Combine(package,"desktop-runtime.json");
        if(!File.Exists(metadata)||new FileInfo(metadata).Length>8192)throw new InvalidDataException("В обновлении нет данных для автоматической установки .NET. Запусти установщик CITA Route.");
        using var document=JsonDocument.Parse(File.ReadAllText(metadata));var spec=Parse(document.RootElement,required);
        // Keep the prerequisite outside the immutable release payload.
        string path=Path.Combine(Path.GetDirectoryName(package)!,"dotnet-"+Guid.NewGuid().ToString("N")+".exe");
        try
        {
            progress?.Report("Загружаю необходимый .NET с сервера Microsoft…");
            await Download(http,spec,path,token);
            // Retain a read lease through verification and execution so the
            // checked executable cannot be replaced between those operations.
            using var lease=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(Convert.ToHexString(SHA512.HashData(lease))!=spec.Sha512)throw new InvalidDataException("Компонент .NET изменился после загрузки.");
            string literal="'"+path.Replace("'","''")+"'";
            string script="$s=Get-AuthenticodeSignature -LiteralPath "+literal+"; if($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch '(^|,\\s*)O=Microsoft Corporation(,|$)'){exit 1}";
            var check=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
            foreach(string arg in new[]{"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(script))})check.ArgumentList.Add(arg);
            using(var signature=Process.Start(check)??throw new IOException("Не удалось проверить подпись Microsoft."))
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(45));
                try{await signature.WaitForExitAsync(deadline.Token);}catch{try{signature.Kill();}catch{}throw;}
                if(signature.ExitCode!=0)throw new InvalidDataException("Цифровая подпись Microsoft .NET не подтверждена.");
            }
            token.ThrowIfCancellationRequested();
            progress?.Report("Устанавливаю .NET. Подтверди запрос Windows…");
            var setup=new ProcessStartInfo(path){UseShellExecute=true,Verb="runas",Arguments="/install /passive /norestart",WorkingDirectory=Path.GetDirectoryName(path)!};
            using var installer=Process.Start(setup)??throw new IOException("Не удалось запустить установку .NET.");
            // Do not kill an elevated Microsoft installer midway on cancellation.
            await installer.WaitForExitAsync(token);
            if(installer.ExitCode is not (0 or 3010 or 1641))throw new IOException("Установка .NET завершилась с кодом "+installer.ExitCode+". CITA Route не изменён.");
            if(!DesktopRuntime.Available(required))throw new IOException(".NET ещё не готов. Заверши запрошенную Microsoft перезагрузку и повтори обновление.");
        }
        catch(Win32Exception ex)when(ex.NativeErrorCode==1223){throw new IOException("Установка .NET отменена. Подключение CITA Route сохранено.",ex);}
        finally{try{File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}}
    }
}
