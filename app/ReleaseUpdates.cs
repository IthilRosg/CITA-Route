using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RoutePilot;

public sealed record ReleaseUpdate(Version Version,string Tag,Uri Page,Uri Archive,Uri Checksums,string FileName,long Bytes,string Notes="");

public sealed class ReleaseUpdates
{
    public const string Repository="IthilRosg/CITA-Route";
    public const long MaximumDownload=450L*1024*1024;
    public static Version Current=>typeof(ReleaseUpdates).Assembly.GetName().Version??new Version(0,3,0);
    readonly HttpClient _http;
    public ReleaseUpdates(HttpClient http){_http=http;}
    public static HttpClient CreateClient(bool routed)
    {
        // Release verification must finish before an update stops our route.
        // Use the existing VPN bridge directly while connected, rather than
        // making a GitHub API request depend on first-failure auto calibration.
        var handler=new SocketsHttpHandler{UseProxy=routed,Proxy=routed?new WebProxy("socks5://127.0.0.1:20808"):null,AllowAutoRedirect=true,MaxAutomaticRedirections=5,ConnectTimeout=TimeSpan.FromSeconds(8)};
        var client=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CITA-Route/"+Current.ToString(3));
        return client;
    }
    public async Task<ReleaseUpdate?> Check(CancellationToken token)
    {
        using var response=await _http.GetAsync("https://api.github.com/repos/"+Repository+"/releases/latest",HttpCompletionOption.ResponseHeadersRead,token);
        TrustedResponse(response);
        if(response.StatusCode==HttpStatusCode.NotFound)return null;
        response.EnsureSuccessStatusCode();
        using var document=JsonDocument.Parse(await ReadBounded(response,1024*1024,token));
        return Parse(document.RootElement,Current);
    }
    public static ReleaseUpdate? Parse(JsonElement release,Version current,bool? frameworkReady=null,string? archivedFile=null)
    {
        if(release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())return null;
        string tag=release.GetProperty("tag_name").GetString()??"";
        if(!Regex.IsMatch(tag,@"^v\d+\.\d+\.\d+$") || !Version.TryParse(tag[1..],out var version))throw new InvalidDataException("Некорректная версия выпуска.");
        if(Compare(version,current)<=0)return null;
        string full="CITA-Route-"+version.ToString(3)+"-win-x64.zip";
        var assets=release.GetProperty("assets").EnumerateArray().ToArray();
        string thin="CITA-Route-"+version.ToString(3)+"-win-x64-framework.zip";
        string name=thin;
        if(archivedFile!=null)
        {
            if(archivedFile!=full && archivedFile!=thin)throw new InvalidDataException("Неизвестный вид сохранённого архива.");
            name=archivedFile;
        }
        var archives=assets.Where(a=>a.GetProperty("name").GetString()==name).ToArray();
        var sums=assets.Where(a=>a.GetProperty("name").GetString()=="SHA256SUMS.txt").ToArray();
        if(archives.Length!=1 || sums.Length!=1)throw new InvalidDataException("В выпуске нет облегчённого ZIP и SHA256SUMS.txt.");
        long length=archives[0].GetProperty("size").GetInt64();
        if(length<=0 || length>MaximumDownload)throw new InvalidDataException("Неподдерживаемый размер обновления.");
        Uri Asset(JsonElement asset,string file)
        {
            var uri=new Uri(asset.GetProperty("browser_download_url").GetString()!);
            string expected="/"+Repository+"/releases/download/"+tag+"/"+file;
            if(uri.Scheme!="https" || uri.Host!="github.com" || uri.AbsolutePath!=expected || uri.Query.Length!=0 || uri.UserInfo.Length!=0)throw new InvalidDataException("Обновление с неизвестного адреса отклонено.");
            return uri;
        }
        var page=new Uri("https://github.com/"+Repository+"/releases/tag/"+tag);
        string notes=release.TryGetProperty("body",out var body)?body.GetString()??"":"";
        return new(version,tag,page,Asset(archives[0],name),Asset(sums[0],"SHA256SUMS.txt"),name,length,notes[..Math.Min(notes.Length,4000)].Replace("\0",""));
    }
    public static int Compare(Version first,Version second)=>new Version(first.Major,first.Minor,Math.Max(0,first.Build)).CompareTo(new Version(second.Major,second.Minor,Math.Max(0,second.Build)));
    public static string ExpectedHash(string text,string name)
    {
        var hashes=text.Split('\n').Select(line=>Regex.Match(line.TrimEnd('\r'),@"^([a-fA-F0-9]{64})[ \t]+\*?([^/\\\r\n]+)$"))
            .Where(m=>m.Success && m.Groups[2].Value==name).Select(m=>m.Groups[1].Value.ToUpperInvariant()).ToArray();
        if(hashes.Length!=1)throw new InvalidDataException("Контрольная сумма архива отсутствует или неоднозначна.");
        return hashes[0];
    }
    public async Task<string> Download(ReleaseUpdate update,string root,IProgress<int>? progress,CancellationToken token)
    {
        string folder=Path.Combine(root,"runtime","updates",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string archive=Path.Combine(folder,update.FileName);
        try
        {
            using var sums=await _http.GetAsync(update.Checksums,HttpCompletionOption.ResponseHeadersRead,token);sums.EnsureSuccessStatusCode();
            TrustedResponse(sums);
            string expected=ExpectedHash(System.Text.Encoding.UTF8.GetString(await ReadBounded(sums,65536,token)),update.FileName);
            using var response=await _http.GetAsync(update.Archive,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();
            TrustedResponse(response);
            if(response.Content.Headers.ContentLength is long reported && reported!=update.Bytes)throw new InvalidDataException("Размер ответа отличается от GitHub Release.");
            await using(var input=await response.Content.ReadAsStreamAsync(token))
            await using(var output=new FileStream(archive,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
            {
                byte[] buffer=new byte[65536];long total=0;int count,lastPercent=-1;
                while((count=await input.ReadAsync(buffer,token))>0){total+=count;if(total>update.Bytes || total>MaximumDownload)throw new InvalidDataException("Превышен размер обновления.");await output.WriteAsync(buffer.AsMemory(0,count),token);int percent=(int)(100*total/update.Bytes);if(percent!=lastPercent){lastPercent=percent;progress?.Report(percent);}}
                if(total!=update.Bytes)throw new InvalidDataException("Архив загружен не полностью.");
            }
            await using(var input=File.OpenRead(archive))if(Convert.ToHexString(await SHA256.HashDataAsync(input,token))!=expected)throw new InvalidDataException("SHA-256 не совпал. Установка запрещена.");
            string extracted=Path.Combine(folder,"package");Extract(archive,extracted);
            string package=PackageDirectory(archive,extracted);VerifyPackage(package,update.Version,requireRuntime:false);
            return package;
        }
        catch
        {
            // This newly created operation owns the entire directory. Rejected
            // downloads must not accumulate fully extracted packages on disk.
            try{Directory.Delete(folder,true);}catch(IOException){}catch(UnauthorizedAccessException){}
            throw;
        }
    }
    public async Task<(string Package,string ManifestHash)> Restore(PreparedRelease prepared,string root,CancellationToken token)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(prepared.Tag,@"^v\d+\.\d+\.\d+$"))throw new InvalidDataException("Некорректный журнал обновления.");
        using var response=await _http.GetAsync("https://api.github.com/repos/"+Repository+"/releases/tags/"+prepared.Tag,token);TrustedResponse(response);response.EnsureSuccessStatusCode();
        using var document=JsonDocument.Parse(await ReadBounded(response,1024*1024,token));
        string savedArchive=Path.GetFileName(UpdateJournal.StagedPath(root,prepared.Archive));
        var update=Parse(document.RootElement,new Version(0,0,0),archivedFile:savedArchive)??throw new InvalidDataException("Выпуск больше не доступен в стабильном канале.");
        if(update.Tag!=prepared.Tag || Compare(update.Version,Current)<=0)throw new InvalidDataException("Обновление уже установлено или устарело.");
        string package=UpdateJournal.StagedPath(root,prepared.Package);
        string archive=UpdateJournal.StagedPath(root,prepared.Archive);
        if(Path.GetFileName(archive)!=update.FileName)throw new InvalidDataException("Архив не соответствует версии.");
        using var sums=await _http.GetAsync(update.Checksums,token);TrustedResponse(sums);sums.EnsureSuccessStatusCode();
        string expected=ExpectedHash(System.Text.Encoding.UTF8.GetString(await ReadBounded(sums,65536,token)),update.FileName);
        await using(var input=File.OpenRead(archive))
            if(input.Length!=update.Bytes || Convert.ToHexString(await SHA256.HashDataAsync(input,token))!=expected)throw new InvalidDataException("Сохранённый архив повреждён.");
        string manifestHash=VerifyExtractedArchive(archive,package,update.Version,requireRuntime:false);
        return(package,manifestHash);
    }
    internal static string VerifyExtractedArchive(string archive,string package,Version version,bool requireRuntime=true)
    {
        using var zip=ZipFile.OpenRead(archive);
        using var original=PackageManifest(zip).Open();string expected=Convert.ToHexString(SHA256.HashData(original));
        using(var extracted=File.OpenRead(Path.Combine(package,"manifest.json")))
            if(Convert.ToHexString(SHA256.HashData(extracted))!=expected)throw new InvalidDataException("Manifest изменён после распаковки.");
        VerifyPackage(package,version,requireRuntime);return expected;
    }
    internal static string PackageDirectory(string archive,string extracted)
    {
        using var zip=ZipFile.OpenRead(archive);
        string relative=PackageManifest(zip).FullName.Replace('\\','/');
        string directory=Path.GetDirectoryName(Path.Combine(extracted,relative.Replace('/',Path.DirectorySeparatorChar)))!;
        string prefix=Path.GetFullPath(extracted).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        string package=Path.GetFullPath(directory);
        if(!package.Equals(prefix.TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase)&&!package.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Путь пакета выходит за папку обновления.");
        return package;
    }
    static ZipArchiveEntry PackageManifest(ZipArchive zip)
    {
        // A release is flat or wrapped in one directory. A browser extension or
        // another payload may legitimately contain its own nested manifest.
        var names=zip.Entries.Select(entry=>new{Entry=entry,Name=entry.FullName.Replace('\\','/')}).ToArray();
        var roots=names.Where(x=>x.Name.Equals("manifest.json",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(roots.Length==0)roots=names.Where(x=>x.Name.Split('/') is var parts&&parts.Length==2&&parts[1].Equals("manifest.json",StringComparison.OrdinalIgnoreCase)&&parts[0] is not ("" or "." or "..")&&!parts[0].Contains(':')).ToArray();
        if(roots.Length!=1||roots[0].Entry.Length>4*1024*1024)throw new InvalidDataException("В архиве должен быть один корневой manifest.json пакета.");
        return roots[0].Entry;
    }
    public static void Extract(string archive,string directory)
    {
        string prefix=Path.GetFullPath(directory)+Path.DirectorySeparatorChar;
        using var zip=ZipFile.OpenRead(archive);
        if(zip.Entries.Count>4096 || zip.Entries.Sum(e=>e.Length)>1500L*1024*1024)throw new InvalidDataException("Архив превышает допустимые пределы.");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in zip.Entries)
        {
            string name=entry.FullName.Replace('\\','/');
            if(name.StartsWith('/') || name.Split('/').Any(p=>p is "." or ".." || p.Contains(':') || p.EndsWith(' ') || p.EndsWith('.')) || ((entry.ExternalAttributes>>16)&0xF000)==0xA000)throw new InvalidDataException("Небезопасный путь в архиве.");
            string target=Path.GetFullPath(Path.Combine(directory,name.Replace('/',Path.DirectorySeparatorChar)));
            if(!target.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) || !names.Add(target))throw new InvalidDataException("Путь или дубликат в архиве отклонён.");
        }
        Directory.CreateDirectory(directory);zip.ExtractToDirectory(directory);
    }
    public static void VerifyPackage(string package,Version expected,bool requireRuntime=true)
    {
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(package,"manifest.json")));
        var data=manifest.RootElement;
        if(!Version.TryParse(data.GetProperty("version").GetString(),out var actual) || Compare(actual,expected)!=0 || data.GetProperty("architecture").GetString()!="win-x64")throw new InvalidDataException("Архив относится к другой версии или платформе.");
        if(!data.GetProperty("selfContained").GetBoolean() && (!data.TryGetProperty("desktopRuntimeMajor",out var major) || !major.TryGetInt32(out int runtimeMajor) || runtimeMajor!=10 ||
            !data.TryGetProperty("desktopRuntimeMinimum",out var minimum) || minimum.ValueKind!=JsonValueKind.String || !Version.TryParse(minimum.GetString(),out var required) || required<DesktopRuntime.Minimum || required.Major!=10 || required.Minor!=0 || required.Revision>=0 || (requireRuntime && !DesktopRuntime.Available(required))))
            throw new InvalidDataException("Для облегчённого обновления требуется системный .NET Desktop 10 x64 указанной версии. Запусти установщик.");
        string prefix=Path.GetFullPath(package)+Path.DirectorySeparatorChar;var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in data.GetProperty("files").EnumerateArray())
        {
            string relative=file.GetProperty("path").GetString()!;
            if(Path.IsPathRooted(relative) || relative.Contains(':'))throw new InvalidDataException("Некорректный путь manifest.");
            string path=Path.GetFullPath(Path.Combine(package,relative));
            if(!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) || !names.Add(path))throw new InvalidDataException("Некорректный manifest.");
            using var input=File.OpenRead(path);
            if(input.Length!=file.GetProperty("bytes").GetInt64() || Convert.ToHexString(SHA256.HashData(input))!=file.GetProperty("sha256").GetString()?.ToUpperInvariant())throw new InvalidDataException("Повреждён файл обновления: "+Path.GetFileName(path));
        }
        foreach(string name in new[]{"RoutePilot.exe","SmartRouting.ps1","Install-Agent.ps1","Configure-Connection.ps1","product.json"}.Concat(data.GetProperty("selfContained").GetBoolean()?Array.Empty<string>():new[]{"DesktopRuntime.ps1"}))
            if(!names.Contains(Path.Combine(package,name)))throw new InvalidDataException("Неполная поставка: "+name);
    }
    static async Task<byte[]> ReadBounded(HttpResponseMessage response,int limit,CancellationToken token)
    {
        await using var input=await response.Content.ReadAsStreamAsync(token);using var output=new MemoryStream();byte[] buffer=new byte[8192];int count;
        while((count=await input.ReadAsync(buffer,token))>0){if(output.Length+count>limit)throw new InvalidDataException("Ответ сервера слишком большой.");output.Write(buffer,0,count);}
        return output.ToArray();
    }
    static void TrustedResponse(HttpResponseMessage response)
    {
        var uri=response.RequestMessage?.RequestUri;
        if(uri==null || uri.Scheme!="https" || uri.UserInfo.Length!=0 || uri.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))throw new InvalidDataException("Перенаправление обновления на неизвестный сервер отклонено.");
    }
}
