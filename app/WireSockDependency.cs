using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RoutePilot;

internal sealed record WireSockRelease(Version Version,Uri Url,string Sha256,long Bytes);
internal static class WireSockDependency
{
    // Reviewed vendor artifact, not scraped from an unauthenticated "latest"
    // field. Updating this catalog requires another reviewed CITA build.
    internal static readonly WireSockRelease Reviewed=new(WireSockCompatibility.Reviewed,
        new("https://wiresock.net/_api/download-release.php?platform=windows_x64&product=wiresock-secure-connect&version=3.6.1.1"),
        "734F3090BA095A698F7293FF90EBD6DC64FCD646277011DFA3648AC5DBDF5D37",17659128);
    internal const long MaximumBytes=64L*1024*1024;
    internal static bool Disconnected(string value)=>Regex.IsMatch(value.Trim(),@"\A(?:Not connected|NotConnected|No connection|Disconnected|Отключено|Нет подключения)\z",RegexOptions.IgnoreCase);
    internal static async Task Download(HttpClient http,WireSockRelease release,string path,CancellationToken token)
    {
        if(release.Url.Scheme!="https"||release.Url.Host!="wiresock.net"||release.Url.UserInfo.Length!=0||release.Url.Fragment.Length!=0||
           release.Bytes<=0||release.Bytes>MaximumBytes||!Regex.IsMatch(release.Sha256,@"\A[A-F0-9]{64}\z"))throw new InvalidDataException("Некорректный каталог WireSock.");
        bool created=false;
        try
        {
            using var response=await http.GetAsync(release.Url,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();
            if(response.RequestMessage?.RequestUri!=release.Url)throw new InvalidDataException("Неожиданное перенаправление загрузки WireSock.");
            if(response.Content.Headers.ContentLength is long length&&length!=release.Bytes)throw new InvalidDataException("Размер установщика WireSock отличается от проверенного.");
            await using var input=await response.Content.ReadAsStreamAsync(token);
            await using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true);
            created=true;
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer=new byte[65536];long total=0;int count;
            while((count=await input.ReadAsync(buffer,token))>0)
            {
                total+=count;if(total>release.Bytes)throw new InvalidDataException("Установщик WireSock превышает допустимый размер.");
                hash.AppendData(buffer,0,count);await output.WriteAsync(buffer.AsMemory(0,count),token);
            }
            if(total!=release.Bytes||Convert.ToHexString(hash.GetHashAndReset())!=release.Sha256)throw new InvalidDataException("Проверка SHA-256 WireSock не пройдена.");
        }
        catch{if(created)try{File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}throw;}
    }
    internal static string VerificationCommand(string path)
    {
        string literal="'"+path.Replace("'","''")+"'";
        return "$ErrorActionPreference='Stop'; $s=Get-AuthenticodeSignature -LiteralPath "+literal+
            "; if($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch '(^|,\\s*)O=The Anti-Cloud Corporation(,|$)'){exit 1}; "+
            "if((Get-FileHash -LiteralPath "+literal+" -Algorithm SHA256).Hash -ne '"+Reviewed.Sha256+"'){exit 1}";
    }
    internal static async Task Verify(string path,Backend backend)
    {
        var result=await backend.RunAsync("powershell.exe",new[]{"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(VerificationCommand(path)))},45000);
        if(result.Code!=0)throw new InvalidDataException("Подпись или SHA-256 установщика WireSock не подтверждены.");
    }
    internal static async Task AssertIdle(ConnectionInventory inventory,Backend backend)
    {
        if(UpdateJournal.GlobalEngineRunning()||Cita.Startup.StartupPaths.Read().Active)throw new IOException("Сначала отключи маршрут CITA Route, включая подключение до входа в Windows.");
        if(inventory.CliPath.Length>0)
        {
            if(!inventory.CliTrusted)throw new IOException("Подпись и расположение CLI WireSock не подтверждены. Повтори поиск установленного WireSock.");
            // Discovery verifies Program Files, reparse points and Authenticode
            // before this inventory reaches the UI. Never use exit code alone.
            var status=await backend.RunAsync(inventory.CliPath,new[]{"status"},4000);
            if(status.Code!=0||!Disconnected(status.Output))throw new IOException("Сначала отключи подключение в WireSock. Его состояние должно быть подтверждено перед обновлением.");
        }
        else
        {
            foreach(string name in new[]{"wiresock-client","WireSockConnect","WireSockUI","wiresock-connect-service"})
            {
                var processes=Process.GetProcessesByName(name);
                try{if(processes.Length>0)throw new IOException("Закрой старую редакцию WireSock и её подключение перед установкой новой версии.");}
                finally{foreach(var process in processes)process.Dispose();}
            }
        }
    }
}
