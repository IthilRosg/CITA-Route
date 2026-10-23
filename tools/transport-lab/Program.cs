using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text.Json;

namespace Cita.TransportLab;

internal static class Program
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    static async Task<int> Main(string[] args)
    {
        try
        {
            if(args.Length==1 && args[0]=="--self-test")
            {
                Wfp.ValidateAbi();string runtime=Path.Combine(Path.GetTempPath(),"runtime"),valid=Path.Combine(runtime,"transport-lab-"+new string('a',32));
                Fixture.ValidatePath(valid,runtime);
                foreach(string bad in new[]{runtime,Path.Combine(runtime,"agent-build"),Path.Combine(runtime,"transport-lab-..")} )
                {bool rejected=false;try{Fixture.ValidatePath(bad,runtime);}catch(IOException){rejected=true;}Assert(rejected,"Unsafe fixture path accepted.");}
                var fixture=new Fixture(valid,1,2);Assert(fixture.FilterKeys.Distinct().Count()==4&&!fixture.FilterKeys.Contains(fixture.SubLayer),"Fixture object identities overlap.");
                Console.WriteLine("PASS: x64 native ABI, bounded fixture scope and distinct stable object identities. No filters installed.");return 0;
            }
            if(args.Length==4 && args[0] is "--peer" or "--once"){await Peer.Run(args,args[0]=="--once");return 0;}
            if(args.Length==5 && args[0]=="--tunnel-peer"){await Peer.Run(args,false);return 0;}
            if(args.Length==2 && args[0]=="--wiresock"){await WireSockExperiment.Run(args[1]);return 0;}
            if(args.Length==2 && args[0]=="--crypto-control"){await WireSockExperiment.Run(args[1],controlOnly:true);return 0;}
            if(args.Length==3 && args[0] is "--arm" or "--arm-uncommitted" && args[2] is "loopback" or "foreign" or "blocked")
            {
                var fixture=Fixture.Load(args[1]);using var wfp=new Wfp();
                wfp.Replace(fixture,args[2]=="loopback"?fixture.LoopbackLuid:args[2]=="foreign"?fixture.OtherLuid:null,
                    args[0]=="--arm-uncommitted"?()=>{Console.WriteLine("PREPARED");Thread.Sleep(Timeout.Infinite);}:null);
                Console.WriteLine("ARMED");await Task.Delay(Timeout.Infinite);return 0;
            }
            if(args.Length==2 && args[0]=="--cleanup")
            {var fixture=Fixture.Load(args[1]);using var wfp=new Wfp();wfp.Remove(fixture);Console.WriteLine("CLEAN");return 0;}
            if(args.Length==1 && args[0]=="--integration"){await Integration();return 0;}
            Console.Error.WriteLine("Development-only transport lab: --self-test | --integration | --cleanup <generated-fixture-root>");return 2;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    static Process Start(string image,params string[] args)
    {
        var info=new ProcessStartInfo(image){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in args)info.ArgumentList.Add(arg);
        return Process.Start(info)??throw new IOException("Fixture process did not start.");
    }
    static async Task<string> Line(Process process)
    {
        string? value=await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12));
        return value??throw new IOException("Fixture exited: "+await process.StandardError.ReadToEndAsync());
    }
    static void Stop(Process process){if(!process.HasExited){process.Kill();Assert(process.WaitForExit(4000),"Fixture process remained alive.");}}
    static async Task Arm(Fixture fixture,string mode,bool beforeCommit=false)
    {
        using var controller=Start(Environment.ProcessPath!,beforeCommit?"--arm-uncommitted":"--arm",fixture.Root,mode);
        try{Assert(await Line(controller)==(beforeCommit?"PREPARED":"ARMED"),"Fixture policy did not reach its checkpoint.");}
        finally{Stop(controller);}
        // The owner was killed without Dispose. Only persistent BFE objects can survive.
    }
    static async Task<Pulse> Pulse(Process peer)
    {await peer.StandardInput.WriteLineAsync("pulse");await peer.StandardInput.FlushAsync();return JsonSerializer.Deserialize<Pulse>(await Line(peer))!;}
    static async Task<Pulse> Once(Fixture fixture,int family,Echo echo)
    {
        using var peer=Start(fixture.Image,"--once",family.ToString(),echo.TcpPort.ToString(),echo.UdpPort.ToString());
        try{var value=JsonSerializer.Deserialize<Pulse>(await Line(peer))!;await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));Assert(peer.ExitCode==0,"Probe failed unexpectedly.");return value;}
        finally{Stop(peer);}
    }
    static void Good(Pulse pulse,string context)=>Assert(pulse.Tcp&&pulse.Udp,context+": "+JsonSerializer.Serialize(pulse));
    static void Denied(Pulse pulse)
    {
        // Windows can silently discard UDP after Send succeeds. A timeout alone
        // is not proof: callers also check receiver counters and an unguarded
        // positive control to that SAME receiver, before/after recovery.
        Assert(!pulse.Tcp&&!pulse.Udp&&pulse.TcpError==10013&&pulse.UdpError is 10013 or -2,
            "New flows were not denied as expected: "+JsonSerializer.Serialize(pulse));
    }
    static async Task Control(int family,Echo echo)
    {
        using var peer=new Peer(family==4?IPAddress.Loopback:IPAddress.IPv6Loopback,echo.TcpPort,echo.UdpPort);
        await peer.Connect();Good(await peer.Check(),"Unguarded positive control to the same guarded receiver");
    }
    static async Task Integration()
    {
        using(var identity=WindowsIdentity.GetCurrent())
            Assert(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),"Run this isolated fixture test as administrator.");
        var loopback=NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.NetworkInterfaceType==NetworkInterfaceType.Loopback);
        var other=NetworkInterface.GetAllNetworkInterfaces().First(n=>n.NetworkInterfaceType!=NetworkInterfaceType.Loopback && n.Supports(NetworkInterfaceComponent.IPv4));
        var fixture=Fixture.Create(Wfp.Luid((uint)loopback.GetIPProperties().GetIPv4Properties().Index),Wfp.Luid((uint)other.GetIPProperties().GetIPv4Properties().Index));
        var checks=new List<string>();var peers=new List<Process>();var echoes=new List<Echo>();var direct=new List<Peer>();
        string receipt=Path.Combine(fixture.Root,"result.json");bool cleanup=false,passed=false;string? failure=null;
        Console.WriteLine("Fixture: "+fixture.Root);
        try
        {
            foreach(int family in new[]{4,6})
            {
                var address=family==4?IPAddress.Loopback:IPAddress.IPv6Loopback;
                var guardedEcho=new Echo(address);echoes.Add(guardedEcho);
                var directEcho=new Echo(address);echoes.Add(directEcho);
                var directPeer=new Peer(address,directEcho.TcpPort,directEcho.UdpPort);direct.Add(directPeer);await directPeer.Connect();
                var guardedPeer=Start(fixture.Image,"--peer",family.ToString(),guardedEcho.TcpPort.ToString(),guardedEcho.UdpPort.ToString());peers.Add(guardedPeer);
                Assert(await Line(guardedPeer)=="READY","Guarded peer was not ready.");Good(await Pulse(guardedPeer),"Baseline guarded "+family);Good(await directPeer.Check(),"Baseline direct "+family);
            }
            checks.Add("IPv4/IPv6 TCP/UDP baseline and distinct application identities");
            await Arm(fixture,"loopback");
            for(int i=0;i<2;i++){Good(await Pulse(peers[i]),"Allowed existing loopback");Good(await Once(fixture,i==0?4:6,echoes[i*2]),"Allowed new loopback");Good(await direct[i].Check(),"Direct preserved");}
            checks.Add("Interface LUID restriction allows existing/new loopback TCP/UDP after controller death");
            await Arm(fixture,"foreign");
            for(int i=0;i<2;i++)
            {
                long before=echoes[i*2].Received;var old=await Pulse(peers[i]);var fresh=await Once(fixture,i==0?4:6,echoes[i*2]);
                Assert(!old.Tcp&&!old.Udp,"An established guarded flow bypassed the block.");
                Denied(fresh);
                Assert(echoes[i*2].Received==before,"Guarded payload reached the receiver during block.");
                await Control(i==0?4:6,echoes[i*2]);Good(await direct[i].Check(),"The same direct TCP/UDP sockets must survive");
            }
            checks.Add("Persistent interface restriction after controller death: existing/new IPv4/IPv6 TCP/UDP denied, no guarded payload, same direct sockets survive");
            await Arm(fixture,"loopback",beforeCommit:true);
            for(int i=0;i<2;i++)
            {
                long before=echoes[i*2].Received;
                var fresh=await Once(fixture,i==0?4:6,echoes[i*2]);
                Denied(fresh);Assert(echoes[i*2].Received==before,"Uncommitted replacement admitted guarded payload.");
                await Control(i==0?4:6,echoes[i*2]);
                Good(await direct[i].Check(),"Direct preserved during aborted policy replacement");
            }
            checks.Add("Controller death before transaction commit preserves the previous restriction");
            await Arm(fixture,"blocked");
            for(int i=0;i<2;i++)
            {
                long before=echoes[i*2].Received;
                var fresh=await Once(fixture,i==0?4:6,echoes[i*2]);
                Denied(fresh);Assert(echoes[i*2].Received==before,"Unconditional hold admitted guarded payload.");
                await Control(i==0?4:6,echoes[i*2]);
                Good(await direct[i].Check(),"Direct preserved under unconditional fixture block");
            }
            checks.Add("Unconditional application-specific hold blocks both address families without a broad network rule");
            await Arm(fixture,"loopback");
            for(int i=0;i<2;i++){Good(await Once(fixture,i==0?4:6,echoes[i*2]),"Guarded recovery");Good(await direct[i].Check(),"Direct preserved on recovery");}
            checks.Add("Atomic policy replacement restores new guarded flows without restarting direct");
            using(var wfp=new Wfp()){wfp.Remove(fixture);cleanup=true;}
            for(int i=0;i<2;i++)Good(await Once(fixture,i==0?4:6,echoes[i*2]),"After cleanup");
            checks.Add("Exact fixture filters/sublayer removed and absence verified");passed=true;
        }
        catch(Exception ex){failure=ex.Message;throw;}
        finally
        {
            // Stop test senders first. Persistent objects are removed only by exact ledger identity.
            foreach(var peer in peers){try{Stop(peer);}finally{peer.Dispose();}}
            foreach(var peer in direct)peer.Dispose();foreach(var echo in echoes)echo.Dispose();
            string? cleanupError=null;
            try{if(!cleanup){using var wfp=new Wfp();wfp.Remove(fixture);cleanup=true;}}
            catch(Exception ex){cleanupError=ex.Message;}
            File.WriteAllText(receipt,JsonSerializer.Serialize(new{schema=1,passed,cleanup,checks,failure,checkedUtc=DateTime.UtcNow,
                cleanupError,wiresockDriverTested=false,physicalInterfaceFailoverTested=false,perPacketIsolationProved=false},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine("Receipt: "+receipt);
            Assert(cleanup,"Fixture cleanup failed. Retain the ledger and use --cleanup on the isolated test machine: "+cleanupError);
        }
        foreach(string check in checks)Console.WriteLine("PASS: "+check);
        Console.WriteLine("LIMIT: loopback ALE/BFE experiment only. No production WireSock, adapter, route or client policy changed.");
    }
}
