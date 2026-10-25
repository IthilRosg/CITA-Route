using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RoutePilot;

internal sealed record NativeComponentArchive(string Path,string Hash);
internal static class NativeComponentDownload
{
    static void Trusted(HttpResponseMessage response)
    {
        var uri=response.RequestMessage?.RequestUri;if(uri==null||uri.Scheme!="https"||uri.Port!=443||uri.UserInfo.Length!=0||uri.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))throw new IOException("Источник компонента не подтверждён.");
    }
    internal static Uri Asset(JsonElement asset,string tag,string file)
    {var uri=new Uri(asset.GetProperty("browser_download_url").GetString()!);if(uri.Scheme!="https"||uri.Port!=443||uri.Host!="github.com"||uri.AbsolutePath!="/"+ReleaseUpdates.Repository+"/releases/download/"+tag+"/"+file||uri.Query.Length!=0||uri.Fragment.Length!=0||uri.UserInfo.Length!=0)throw new IOException("Адрес компонента не подтверждён.");return uri;}
    static async Task<byte[]> Read(HttpResponseMessage response,int maximum,CancellationToken stop)
    {
        Trusted(response);await using var input=await response.Content.ReadAsStreamAsync(stop);using var data=new MemoryStream();byte[] buffer=new byte[16384];int count;while((count=await input.ReadAsync(buffer,stop))>0){if(data.Length+count>maximum)throw new IOException("Ответ сервера слишком большой.");data.Write(buffer,0,count);}return data.ToArray();
    }
    internal static async Task<NativeComponentArchive> Fetch(string root,IProgress<int>? progress,CancellationToken stop)
    {using var http=ReleaseUpdates.CreateClient(false);return await Fetch(http,root,progress,stop);}
    internal static async Task<NativeComponentArchive> Fetch(HttpClient http,string root,IProgress<int>? progress,CancellationToken stop)
    {
        string tag="v"+ReleaseUpdates.Current.ToString(3),name="CITA-Route-native-"+ReleaseUpdates.Current.ToString(3)+"-win-x64.zip";
        using var release=await http.GetAsync("https://api.github.com/repos/"+ReleaseUpdates.Repository+"/releases/tags/"+tag,stop);
        if(release.StatusCode==HttpStatusCode.NotFound)throw new IOException("Для этой версии встроенный модуль ещё не опубликован.");release.EnsureSuccessStatusCode();
        using var json=JsonDocument.Parse(await Read(release,1048576,stop));var document=json.RootElement;if(document.GetProperty("tag_name").GetString()!=tag||document.GetProperty("draft").GetBoolean())throw new IOException("Выпуск встроенного модуля не подтверждён.");
        var assets=document.GetProperty("assets").EnumerateArray().ToArray();var archive=assets.SingleOrDefault(x=>x.GetProperty("name").GetString()==name);var sums=assets.SingleOrDefault(x=>x.GetProperty("name").GetString()=="SHA256SUMS.txt");if(archive.ValueKind==JsonValueKind.Undefined||sums.ValueKind==JsonValueKind.Undefined)throw new IOException("В выпуске нет встроенного модуля и его контрольной суммы.");
        long expectedBytes=archive.GetProperty("size").GetInt64();if(expectedBytes is <1 or >201326592)throw new IOException("Неподдерживаемый размер встроенного модуля.");
        using var checksum=await http.GetAsync(Asset(sums,tag,"SHA256SUMS.txt"),stop);checksum.EnsureSuccessStatusCode();string expected=ReleaseUpdates.ExpectedHash(Encoding.UTF8.GetString(await Read(checksum,65536,stop)),name);
        string folder=System.IO.Path.Combine(root,"runtime","native-download",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);string path=System.IO.Path.Combine(folder,name);
        using var response=await http.GetAsync(Asset(archive,tag,name),HttpCompletionOption.ResponseHeadersRead,stop);response.EnsureSuccessStatusCode();Trusted(response);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long length=0;
        try{await using var input=await response.Content.ReadAsStreamAsync(stop);await using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true)){byte[] buffer=new byte[65536];int count;while((count=await input.ReadAsync(buffer,stop))>0){length+=count;if(length>expectedBytes)throw new IOException("Размер компонента изменился.");await output.WriteAsync(buffer.AsMemory(0,count),stop);hash.AppendData(buffer,0,count);progress?.Report((int)(length*100/expectedBytes));}}
            if(length!=expectedBytes||Convert.ToHexString(hash.GetHashAndReset())!=expected)throw new IOException("Контрольная сумма компонента не совпала.");return new(path,expected);
        }catch{if(File.Exists(path))File.Delete(path);throw;}
    }
}
