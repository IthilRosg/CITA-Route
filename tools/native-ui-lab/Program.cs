using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using RoutePilot;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if(args.Length==2&&args[0]=="--service-import"&&args[1]==@"C:\CitaLab\NativeAwg")
        {
            using var deadline=new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));var client=new NativeRouteClient();var before=client.Command("status",cancellation:deadline.Token).GetAwaiter().GetResult().Native;
            if(before?.Phase!="Disconnected")return 1;
            string text=File.ReadAllText(Path.Combine(args[1],"private","peer.conf"))+"\n# Desktop client import acceptance\n";
            string request=Guid.NewGuid().ToString("N");var reply=client.Import(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)),"Desktop imported profile",before.Operation,deadline.Token,request).GetAwaiter().GetResult();
            if(!reply.Success||reply.Native?.Operation!=request)return 1;
            var selected=client.WaitImport(request,null,deadline.Token).GetAwaiter().GetResult();
            if(selected.ProfileName!="Desktop imported profile"||selected.Desired||selected.ProfileId==before.ProfileId)return 1;
            Console.WriteLine(JsonSerializer.Serialize(new{success=true,desktopImportVerified=true}));return 0;
        }
        if(args.Length==2&&args[0]=="--service-status"&&args[1]==@"C:\CitaLab\NativeAwg")
        {
            var reply=new NativeRouteClient().Command("status").GetAwaiter().GetResult();
            if(!reply.Success||reply.Native is not {Connected:true,Desired:true,PolicyMode:"FullVpn"} state||state.ProfileId.Length!=64)return 1;
            Console.WriteLine(JsonSerializer.Serialize(new{success=true,profileIdentified=true,policyIdentified=true,connected=true}));return 0;
        }
        if(args.Length!=1)return 2;
        string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
        var app=new App();app.InitializeComponent();
        var window=new MainWindow(root,true);
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var apply=typeof(MainWindow).GetMethod("ApplyNativeView",flags)!;
        var choice=typeof(MainWindow).GetField("_nativeChoice",flags)!.GetValue(window)!;
        choice.GetType().GetProperty("Selected")!.SetValue(choice,true);
        string Text(string name)=>((TextBlock)typeof(MainWindow).GetField(name,flags|BindingFlags.Public)!.GetValue(window)!).Text;
        void Check(bool condition,string reason){if(!condition)throw new Exception(reason);}
        string tag="v0.6.4",file="CITA-Route-native-0.6.4-win-x64.zip",url="https://github.com/"+ReleaseUpdates.Repository+"/releases/download/"+tag+"/"+file;
        JsonElement Asset(string value)=>JsonSerializer.SerializeToElement(new{browser_download_url=value});
        var assetMethod=typeof(MainWindow).Assembly.GetType("RoutePilot.NativeComponentDownload")!.GetMethod("Asset",BindingFlags.NonPublic|BindingFlags.Static)!;
        Uri NativeAsset(string value)=>(Uri)assetMethod.Invoke(null,new object[]{Asset(value),tag,file})!;
        Check(NativeAsset(url).AbsoluteUri==url,"Native asset scope");
        foreach(string invalid in new[]{url.Replace("https:","http:"),url.Replace("github.com","github.com:444"),url+"?download=1",url+"#fragment",url.Replace("v0.6.4/","v0.6.5/"),url.Replace("github.com","github.com.attacker.invalid")})
        {bool refused=false;try{NativeAsset(invalid);}catch(TargetInvocationException error)when(error.InnerException is IOException){refused=true;}Check(refused,"Untrusted native asset accepted");}
        var fetch=typeof(MainWindow).Assembly.GetType("RoutePilot.NativeComponentDownload")!.GetMethod("Fetch",BindingFlags.NonPublic|BindingFlags.Static,null,new[]{typeof(System.Net.Http.HttpClient),typeof(string),typeof(IProgress<int>),typeof(System.Threading.CancellationToken)},null)!;
        string downloadRoot=Path.Combine(root,"download-fixture-"+Guid.NewGuid().ToString("N"));
        Task Download(System.Net.Http.HttpClient http)=>(Task)fetch.Invoke(null,new object?[]{http,downloadRoot,null,System.Threading.CancellationToken.None})!;
        using(var http=new System.Net.Http.HttpClient(new NativeDownloadFixture(false)))Task.Run(()=>Download(http)).GetAwaiter().GetResult();
        int downloaded=Directory.GetFiles(downloadRoot,"*.zip",SearchOption.AllDirectories).Length;Check(downloaded==1,"Verified native download missing");
        using(var http=new System.Net.Http.HttpClient(new NativeDownloadFixture(true))){bool refused=false;try{Task.Run(()=>Download(http)).GetAwaiter().GetResult();}catch(IOException){refused=true;}Check(refused&&Directory.GetFiles(downloadRoot,"*.zip",SearchOption.AllDirectories).Length==downloaded,"Corrupt native archive retained");}
        var remembered=JsonSerializer.Deserialize("{\"Selected\":true,\"ConfirmedProfile\":\"fixture\"}",choice.GetType());
        Check(remembered!=null&&(bool)choice.GetType().GetProperty("Selected")!.GetValue(remembered)!&&(string?)choice.GetType().GetProperty("ConfirmedProfile")!.GetValue(remembered)=="fixture","Profile choice persistence");
        foreach(string phase in new[]{"Importing","Preparing","Connected","Recovering","Stopping","Disconnected","RecoveryFailed"})
        {
            var state=new NativeRouteView{Phase=phase,Desired=phase!="Disconnected",ProfileName="Lab profile",PolicyMode="FullVpn",Runtime=new(){CoreState=phase=="Connected"?"Active":"Recovering"}};
            apply.Invoke(window,new object?[]{state});
            Check(Text("CompactState")==NativeRoutePresentation.Title(state),"State mismatch: "+phase);
            Check(Text("CompactProfile")=="Lab profile","Profile replaced by legacy selection");
            Check(Text("CompactVpnRtt")=="Задержка: —"&&Text("CompactVpnLoss")=="Потери: —","Stale quality values");
            Check(Text("CompactDownload")=="—"&&Text("CompactTraffic")=="—","Stale rate or traffic");
        }
        Check(!new NativeRouteView{Phase="Connected",Runtime=new(){CoreState="Recovering"}}.Connected,"False connected status");
        var firstUse=new NativeRouteView{Phase="Unconfigured",Desired=false,ProfileName=""};
        apply.Invoke(window,new object?[]{firstUse});
        Check(Text("CompactState")=="Выбери профиль"&&!firstUse.Connected,"First use must request a profile without claiming connection");
        var parsed=JsonSerializer.Deserialize<NativeControlReply>("{\"success\":true,\"native\":{\"phase\":\"Connected\",\"desired\":true,\"profileName\":\"Fixture\",\"runtime\":{\"coreState\":\"Active\"}}}",new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
        Check(parsed?.Success==true&&parsed.Native?.Connected==true&&parsed.Native.ProfileName=="Fixture","Service JSON contract");
        apply.Invoke(window,new object?[]{null});Check(Text("CompactState")=="Служба недоступна","Missing service state");
        Console.WriteLine(JsonSerializer.Serialize(new{success=true,states=9,profileRetained=true,firstUseVerified=true,staleMetricsCleared=true,noNetworkChanges=true}));
        return 0;
    }
}

internal sealed class NativeDownloadFixture(bool corrupt):System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,System.Threading.CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();string version=ReleaseUpdates.Current.ToString(3),tag="v"+version,name="CITA-Route-native-"+version+"-win-x64.zip",baseUrl="https://github.com/"+ReleaseUpdates.Repository+"/releases/download/"+tag+"/";
        byte[] bytes=System.Text.Encoding.UTF8.GetBytes("Bounded native download fixture");string hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        System.Net.Http.HttpContent content;
        if(request.RequestUri!.Host=="api.github.com")content=new System.Net.Http.StringContent(JsonSerializer.Serialize(new{tag_name=tag,draft=false,assets=new[]{new{name,size=bytes.Length,browser_download_url=baseUrl+name},new{name="SHA256SUMS.txt",size=200,browser_download_url=baseUrl+"SHA256SUMS.txt"}}}));
        else if(request.RequestUri.AbsoluteUri==baseUrl+"SHA256SUMS.txt")content=new System.Net.Http.StringContent(hash+"  "+name+"\n");
        else if(request.RequestUri.AbsoluteUri==baseUrl+name){if(corrupt)bytes[0]^=1;content=new System.Net.Http.ByteArrayContent(bytes);}
        else throw new IOException("Unexpected download fixture URL");
        return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=content,RequestMessage=request});
    }
}
