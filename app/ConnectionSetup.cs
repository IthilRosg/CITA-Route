using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RoutePilot;

public sealed class ConnectionInventory
{
    public string CliPath{get;set;}="";
    public string Version{get;set;}="";
    public string[] Profiles{get;set;}=Array.Empty<string>();
    public string SelectedProfile{get;set;}="";
    public string ExpectedEgressIp{get;set;}="";
    public string Message{get;set;}="";
}

public sealed class ConnectionSetup
{
    readonly string _root;
    readonly Backend _backend;
    public ConnectionSetup(string root,Backend backend){_root=root;_backend=backend;}
    public async Task<ConnectionInventory> Discover(string cli="")
    {
        var result=await _backend.RunAsync("powershell.exe",new[]{"-NoProfile","-ExecutionPolicy","Bypass","-File",Path.Combine(_root,"Configure-Connection.ps1"),"-Action","Discover","-CliPath",cli},15000);
        if(result.Code!=0)throw new IOException("Не удалось найти WireSock. Проверь установку и службу WireSock Secure Connect.");
        return JsonSerializer.Deserialize<ConnectionInventory>(result.Output.Trim(),AppPaths.Json)??throw new IOException("Нет ответа от поиска подключений.");
    }
    public async Task Apply(string action,string cli,string profile="",string import="",string egress="")
    {
        if(action is not ("Apply" or "Import"))throw new ArgumentException("Invalid setup action.");
        Directory.CreateDirectory(Path.Combine(_root,"runtime"));Directory.CreateDirectory(Path.Combine(_root,"logs"));
        string id=Guid.NewGuid().ToString("N");
        string Literal(string value)=>"'"+value.Replace("'","''")+"'";
        string report=Path.Combine(_root,"runtime","connection-operation.json");
        string sid=System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        string user=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string command="$ErrorActionPreference='Stop'; . "+Literal(Path.Combine(_root,"FileStorage.ps1"))+"; "+
            "try{ & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "+Literal(Path.Combine(_root,"Configure-Connection.ps1"))+
            " -Action "+action+" -CliPath "+Literal(cli)+" -SourceProfile "+Literal(profile)+" -ImportPath "+Literal(import)+" -ExpectedEgressIp "+Literal(egress)+
            " -UserSid "+Literal(sid)+" -UserProfile "+Literal(user)+" *> "+Literal(Path.Combine(_root,"logs","connection-setup.log"))+"; "+
            "if($LASTEXITCODE -ne 0){throw 'Настройка не завершена. Подробности: logs/connection-setup.log'}; "+
            "Write-JsonFile "+Literal(report)+" @{id='"+id+"';success=$true;message=''} }catch{Write-JsonFile "+Literal(report)+" @{id='"+id+"';success=$false;message=$_.Exception.Message};exit 1}";
        var info=new ProcessStartInfo("powershell.exe"){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=_root,
            Arguments="-NoProfile -ExecutionPolicy Bypass -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(command))};
        using var process=Process.Start(info)??throw new IOException("Не удалось запустить настройку.");
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(4));await process.WaitForExitAsync(timeout.Token);
        using var result=JsonDocument.Parse(await File.ReadAllTextAsync(report));
        if(result.RootElement.GetProperty("id").GetString()!=id || !result.RootElement.GetProperty("success").GetBoolean())
            throw new IOException(Backend.Redact(result.RootElement.GetProperty("message").GetString()??"Настройка не завершена."));
    }
}
