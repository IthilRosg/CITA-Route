using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace RoutePilot.Agent;

internal static class BrowserNativeHost
{
    const string Settings=@"Software\CITA\Route\BrowserFeedback";
    internal static readonly string[] RegistryHosts={@"Software\Google\Chrome\NativeMessagingHosts\",@"Software\Google\Chrome for Testing\NativeMessagingHosts\",@"Software\Microsoft\Edge\NativeMessagingHosts\"};
    static string ConfiguredRoot()
    {
        using var key=Registry.CurrentUser.OpenSubKey(Settings);string root=key?.GetValue("Root") as string??"";
        if(root.Length is 0 or >4096||!Path.IsPathFullyQualified(root))throw new IOException("Browser feedback is not configured for this user.");return Path.GetFullPath(root);
    }
    internal static void Register(string root,bool enable)
    {
        root=Path.GetFullPath(root);if(!Directory.Exists(Path.Combine(root,"data")))throw new IOException("Client data directory required.");
        using(var current=Registry.CurrentUser.OpenSubKey(Settings))if(current?.GetValue("Root") is string previous&&!Path.GetFullPath(previous).Equals(root,StringComparison.OrdinalIgnoreCase))throw new IOException("Another installation owns browser feedback; remove its registration first.");
        PolicyOptions? options=null;string policy=Path.Combine(root,"data/routing-policy.json");
        if(enable){if(File.Exists(Path.Combine(root,"runtime/session.json"))&&Storage.Read<PolicySession>(Path.Combine(root,"runtime/session.json")).Running)throw new IOException("Disconnect before enabling browser feedback, then reconnect.");options=Storage.Read<PolicyOptions>(policy);}
        string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CITA Route","BrowserFeedback");Directory.CreateDirectory(folder);
        string manifest=Path.Combine(folder,"host.json");Storage.Write(manifest,new{name=BrowserNativeWire.Host,description="CITA Route resource feedback",path=BrowserNativeIdentity.AgentImage,type="stdio",allowed_origins=new[]{BrowserNativeWire.Origin}});
        using(var key=Registry.CurrentUser.CreateSubKey(Settings)){key.SetValue("Root",root);key.SetValue("Manifest",manifest);}
        foreach(var view in new[]{RegistryView.Registry32,RegistryView.Registry64})using(var user=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,view))foreach(string prefix in RegistryHosts)using(var key=user.CreateSubKey(prefix+BrowserNativeWire.Host))key.SetValue("",manifest);
        if(enable)
        {
            options!.ExperimentalBrowserFeedback=true;Storage.Write(policy,options);
        }
    }
    internal static void Unregister(string root)
    {
        root=Path.GetFullPath(root);if(!ConfiguredRoot().Equals(root,StringComparison.OrdinalIgnoreCase))throw new IOException("Another installation owns browser feedback.");
        using var settings=Registry.CurrentUser.OpenSubKey(Settings);string manifest=settings?.GetValue("Manifest") as string??"";
        foreach(var view in new[]{RegistryView.Registry32,RegistryView.Registry64})using(var user=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,view))foreach(string prefix in RegistryHosts)
        {using var key=user.OpenSubKey(prefix+BrowserNativeWire.Host);if((key?.GetValue("") as string)==manifest)user.DeleteSubKey(prefix+BrowserNativeWire.Host,false);}
        settings?.Dispose();Registry.CurrentUser.DeleteSubKey(Settings,false);
        string policy=Path.Combine(root,"data/routing-policy.json");if(File.Exists(policy)){var options=Storage.Read<PolicyOptions>(policy);options.ExperimentalBrowserFeedback=false;Storage.Write(policy,options);}
        string expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CITA Route","BrowserFeedback","host.json");if(manifest==expected&&File.Exists(expected))File.Delete(expected);
    }
    internal static async Task Run(string[] args)
    {
        if(args.Length is <1 or >2||args[0]!=BrowserNativeWire.Origin||(args.Length==2&&!System.Text.RegularExpressions.Regex.IsMatch(args[1],@"^--parent-window=\d+$"))||BrowserNativeIdentity.Browser(Environment.ProcessId)==null)throw new IOException("Untrusted browser host launch.");
        string root=ConfiguredRoot();var state=ComponentLiveness.Read<BrowserObserverState>(Path.Combine(root,"runtime/browser-observer.json"));
        var session=ComponentLiveness.Read<PolicySession>(Path.Combine(root,"runtime/session.json"));
        if(!session.Running||session.Id!=state.SessionId||state.Pipe!="Cita.Browser.v1."+state.SessionId+"."+state.Owner.Pid||!Guid.TryParseExact(state.SessionId,"N",out _))throw new IOException("Browser observer session is not active.");
        using var pipe=new NamedPipeClientStream(".",state.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly,TokenImpersonationLevel.Identification);
        using var lifetime=new CancellationTokenSource(TimeSpan.FromHours(2));
        using(var connect=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)){connect.CancelAfter(4000);await pipe.ConnectAsync(connect.Token);}
        if(!BrowserNativeIdentity.GetNamedPipeServerProcessId(pipe.SafePipeHandle,out uint pid)||!BrowserNativeIdentity.Server((int)pid,state.Owner))throw new IOException("Untrusted browser observer process.");
        using var input=Console.OpenStandardInput();using var output=Console.OpenStandardOutput();
        async Task Watch()
        {
            try{while(true){await Task.Delay(500,lifetime.Token);var current=ComponentLiveness.Read<PolicySession>(Path.Combine(root,"runtime/session.json"));if(!current.Running||current.Id!=state.SessionId||File.Exists(Path.Combine(root,"runtime/stop.request"))||!File.Exists(Path.Combine(root,"runtime/browser-observer.json"))||!ConfiguredRoot().Equals(root,StringComparison.OrdinalIgnoreCase)||!BrowserNativeIdentity.Server(state.Owner.Pid,state.Owner)||BrowserNativeIdentity.Browser(Environment.ProcessId)==null)return;}}
            catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or OperationCanceledException){return;}
        }
        var ended=Watch();
        try{while(true)
        {
            var read=BrowserNativeWire.Read(input,lifetime.Token);
            if(await Task.WhenAny(read,ended)==ended){lifetime.Cancel();_ = read.ContinueWith(t=>{_ = t.Exception;},TaskContinuationOptions.OnlyOnFaulted);return;}
            var data=await read;if(data==null)break;
            if(!ConfiguredRoot().Equals(root,StringComparison.OrdinalIgnoreCase))throw new IOException("Browser feedback registration changed.");
            var batch=BrowserNativeWire.Parse(data);await BrowserNativeWire.Write(pipe,data,lifetime.Token);
            using var response=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);response.CancelAfter(15000);
            byte[] reply=await BrowserNativeWire.Read(pipe,response.Token)??throw new IOException("Browser observer disconnected.");
            var value=JsonSerializer.Deserialize<BrowserNativeReply>(reply,BrowserNativeWire.Json)??throw new IOException("Empty browser observer reply.");
            if(value.Id!=batch.Id||value.Entries==null||value.Entries.Length>32)throw new IOException("Unexpected browser observer reply.");
            await BrowserNativeWire.Write(output,reply,lifetime.Token);
        }}finally{lifetime.Cancel();await ended;}
    }
}
