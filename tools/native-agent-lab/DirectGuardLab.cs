using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RoutePilot.Agent;

internal static class DirectGuardLab
{
    static async Task GuestOnly(CancellationToken stop)
    {
        var info=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(string arg in new[]{"-NoProfile","-NonInteractive","-Command","(Get-CimInstance Win32_ComputerSystemProduct).UUID"})info.ArgumentList.Add(arg);
        using var p=Process.Start(info)!;string value=await p.StandardOutput.ReadToEndAsync(stop);await p.WaitForExitAsync(stop);if(p.ExitCode!=0||value.Trim()!="E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83")throw new IOException("Wrong guest.");
    }
    static void Check(bool value){if(!value)throw new IOException("Direct guard acceptance failed.");}
    static async Task<JsonElement> Probe(string root,int port,string token,CancellationToken stop)
    {
        var info=new ProcessStartInfo(Path.Combine(root,"native-owner.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add("--proxy-client");using var p=Process.Start(info)!;_=p.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try{await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{Port=port,Token=token,LiteralIPv4=true}));p.StandardInput.Close();var reply=await NativeAwgBackend.ReadReply(p.StandardOutput,stop);await p.WaitForExitAsync(stop);return reply;}finally{if(!p.HasExited){p.Kill(true);await p.WaitForExitAsync();}}
    }
    static async Task<bool> Port80(int port,string token,CancellationToken stop)
    {
        using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,port,stop);var stream=client.GetStream();await stream.WriteAsync(new byte[]{5,1,2},stop);byte[] reply=new byte[2];await stream.ReadExactlyAsync(reply,stop);if(reply[1]!=2)return false;
        byte[] user=Encoding.ASCII.GetBytes("cita"),pass=Encoding.ASCII.GetBytes(token);byte[] auth=new byte[3+user.Length+pass.Length];auth[0]=1;auth[1]=(byte)user.Length;user.CopyTo(auth,2);auth[2+user.Length]=(byte)pass.Length;pass.CopyTo(auth,3+user.Length);await stream.WriteAsync(auth,stop);await stream.ReadExactlyAsync(reply,stop);if(reply[1]!=0)return false;
        await stream.WriteAsync(new byte[]{5,1,0,1,1,1,1,1,0,80},stop);await stream.ReadExactlyAsync(reply,stop);return reply[1]==0;
    }
    internal static async Task<int> Run(string root,string run)
    {
        string stage="guest";NativeWfpGuard? guard=null;Process? core=null;var report=new Dictionary<string,object?>{{"success",false}};using var job=new NativeProcessJob();
        try
        {
            using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(100));var stop=budget.Token;await GuestOnly(stop);
            if(!root.StartsWith(@"C:\CitaLab\NativeAwg\DirectGuard-",StringComparison.OrdinalIgnoreCase)||Path.GetFileName(root)!="DirectGuard-"+run||run.Length!=32||!run.All(Uri.IsHexDigit))throw new IOException("Wrong owned test path.");
            var uplink=NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.OperationalStatus==OperationalStatus.Up&&n.GetIPProperties().GatewayAddresses.Any(a=>a.Address.AddressFamily==AddressFamily.InterNetwork));
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();string token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var config=new{log=new{disabled=true},inbounds=new[]{new{type="socks",tag="local",listen="127.0.0.1",listen_port=port,users=new[]{new{username="cita",password=token}}}},outbounds=new[]{new{type="direct",tag="direct",bind_interface=uplink.Name}},route=new{final="direct"}};
            string configPath=Path.Combine(root,"direct.json");await File.WriteAllTextAsync(configPath,JsonSerializer.Serialize(config),stop);
            var info=new ProcessStartInfo(Path.Combine(root,"native-core.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(string arg in new[]{"run","-c",configPath})info.ArgumentList.Add(arg);core=Process.Start(info)!;job.Attach(core);_=core.StandardOutput.BaseStream.CopyToAsync(Stream.Null);_=core.StandardError.BaseStream.CopyToAsync(Stream.Null);
            await File.WriteAllTextAsync(Path.Combine(root,"core-pid.json"),JsonSerializer.Serialize(new{pid=core.Id}),stop);
            for(int i=0;i<40;i++){try{using var c=new TcpClient();await c.ConnectAsync(IPAddress.Loopback,port,stop);break;}catch(SocketException){await Task.Delay(100,stop);}}
            stage="baseline";Check((await Probe(root,port,token,stop)).GetProperty("success").GetBoolean());
            var policy=JsonSerializer.SerializeToElement(new{Vpn=new[]{"1.1.1.1/32"},Direct=Array.Empty<string>()});guard=new(Path.Combine(root,"native-guard.exe"),run,policy,endpoint:new(Path.Combine(root,"native-owner.exe"),"203.0.113.1",3080));
            stage="protected";await guard.PlanAsync(stop);await guard.ArmAsync(stop);Check(await guard.VerifyAsync(stop));Check(!(await Probe(root,port,token,stop)).GetProperty("success").GetBoolean());report["coreInitiallyBlocked"]=true;
            var scope=new NativeDirectPermission(Path.Combine(root,"native-core.exe"),"1.1.1.1",443,"tcp",checked((uint)uplink.GetIPProperties().GetIPv4Properties().Index));
            stage="grant";await guard.SetDirectAsync(scope,true,stop);Check(guard.DirectPermits==1&&await guard.VerifyAsync(stop));await guard.ArmAsync(stop);Check(await guard.VerifyAsync(stop));Check((await Probe(root,port,token,stop)).GetProperty("success").GetBoolean());report["exactCoreHttpsAllowed"]=true;report["rearmRetainsNarrowScope"]=true;
            stage="other-app";Check(await CoreSafetyLab.OsClosed(Path.Combine(root,"native-owner.exe"),"1.1.1.1",stop));report["otherExecutableStillBlocked"]=true;
            stage="other-port";using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop)){deadline.CancelAfter(TimeSpan.FromSeconds(5));Check(!await Port80(port,token,deadline.Token));}report["otherPortStillBlocked"]=true;
            stage="revoke";await guard.SetDirectAsync(scope,false,stop);Check(guard.DirectPermits==0&&await guard.VerifyAsync(stop));Check(!(await Probe(root,port,token,stop)).GetProperty("success").GetBoolean());report["revocationVerified"]=true;
            stage="close";await guard.SetDirectAsync(scope,true,stop);await guard.CloseAsync(stop);Check(guard.DirectPermits==0&&await guard.VerifyAsync(stop));Check(!(await Probe(root,port,token,stop)).GetProperty("success").GetBoolean());report["closeRevokesDirect"]=true;
            stage="cleanup";await guard.RemoveAsync(stop);Check((await Probe(root,port,token,stop)).GetProperty("success").GetBoolean());report["cleanupRestoresBaseline"]=true;report["success"]=true;
        }
        catch(Exception error){report["failedStage"]=stage;report["errorType"]=error.GetType().Name;report["guardStage"]=guard?.LastFailureStage;report["guardReason"]=guard?.LastFailureReason;}
        finally
        {
            using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(15));try{if(guard!=null)await guard.RemoveAsync(cleanup.Token);await job.TerminateAsync(cleanup.Token);report["cleaned"]=true;}catch{report["success"]=false;report["cleaned"]=false;}
            core?.Dispose();await File.WriteAllTextAsync(Path.Combine(root,"result.json"),JsonSerializer.Serialize(report));
        }
        return report["success"] is true?0:1;
    }
}
