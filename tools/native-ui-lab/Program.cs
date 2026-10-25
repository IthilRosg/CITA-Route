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
        var parsed=JsonSerializer.Deserialize<NativeControlReply>("{\"success\":true,\"native\":{\"phase\":\"Connected\",\"desired\":true,\"profileName\":\"Fixture\",\"runtime\":{\"coreState\":\"Active\"}}}",new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
        Check(parsed?.Success==true&&parsed.Native?.Connected==true&&parsed.Native.ProfileName=="Fixture","Service JSON contract");
        apply.Invoke(window,new object?[]{null});Check(Text("CompactState")=="Служба недоступна","Missing service state");
        Console.WriteLine(JsonSerializer.Serialize(new{success=true,states=8,profileRetained=true,staleMetricsCleared=true,noNetworkChanges=true}));
        return 0;
    }
}
