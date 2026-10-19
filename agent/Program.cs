using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace RoutePilot.Agent;

public static class Program
{
    public const string ServiceName="RoutePilotRescue";
    public const string PipeName="RoutePilot.Rescue.v1";
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if(args.Length==1 && args[0]=="--self-test"){await AgentTests.Run();return 0;}
            if(args.Length==1 && args[0]=="--statistics-self-test"){await WireSockStatisticsTests.Run();return 0;}
            if(args.Length==1 && args[0]=="--service"){NativeService.Run();return 0;}
            if(args.Length==1 && args[0]=="--configure-service-recovery"){ServiceRecovery.Configure();return 0;}
            if(args.Length==1 && args[0]=="--session-health"){var health=await NetworkProbe.CheckEgressDetailed();Console.WriteLine(JsonSerializer.Serialize(new{schema=2,healthy=health.Verified,code=health.Code,message=health.Message}));return 0;}
            if(args.Length==1 && args[0]=="--wiresock-statistics")
            {
                string installed=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RoutePilot","Agent","agent-config.json");
                string cli=File.Exists(installed)?Storage.Read<AgentConfig>(installed).WireSockCliPath:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"WireSock Secure Connect","command-line","wiresock-connect-cli.exe");
                var stats=await Telemetry.WireSockStatistics.Read(cli);
                Console.WriteLine(JsonSerializer.Serialize(new{schema=1,stats.Available,stats.Code,stats.RttMs,stats.EstimatedLossPercent,stats.UptimeSeconds,stats.ObservedUtc}));return 0;
            }
            if(args.Length>=1 && args[0]=="--probe-worker"){await ProbeWorker.Run(args);return 0;}
            if(args.Length==2 && args[0]=="--cleanup-worker"){await ProbeWorker.Cleanup(args[1]);return 0;}
            if(args.Length==2 && args[0]=="--policy-worker"){await PolicyWorker.Run(args[1]);return 0;}
            if(args.Length==2 && args[0]=="--network-signature"){Console.WriteLine(PolicyNetwork.Signature(Path.GetFullPath(args[1])));return 0;}
            if(args.Length==2 && args[0]=="--prepare-policy"){PolicyWorker.Prepare(args[1]);return 0;}
            if(args.Length==2 && args[0]=="--integration-policy"){await PolicyIntegration.Run(args[1]);return 0;}
            if(args.Length==2 && args[0]=="--optimization-integration"){await PolicyEngineTests.Run(Path.GetFullPath(args[1]));return 0;}
            if(args.Length==2 && args[0]=="--component-integration"){await ComponentRecoveryTests.Run(Path.GetFullPath(args[1]));return 0;}
            if(args.Length==2 && args[0]=="--udp-integration"){await UdpEngineTests.Run(Path.GetFullPath(args[1]));return 0;}
            if(args.Length==2 && args[0]=="--optimization-live"){await PolicyLiveCheck.Run(Path.GetFullPath(args[1]));return 0;}
            if(args.Length==3 && args[0]=="--optimization-auto"){await PolicyAutoCheck.Run(Path.GetFullPath(args[1]),args[2]);return 0;}
            if(args.Length is <1 or >2 || args[0] is not ("status" or "dry-run" or "test-guard-restart" or "test-vpnus" or "test-wiresock" or "test-system" or "recover"))
            {Console.Error.WriteLine("Commands: status, dry-run, test-vpnus, test-wiresock, recover [runId]");return 2;}
            using var client=new NamedPipeClientStream(".",PipeName,PipeDirection.InOut,PipeOptions.Asynchronous,System.Security.Principal.TokenImpersonationLevel.Identification);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(timeout.Token);
            string? runId=args.Length==2?args[1]:null;
            if(args[0]=="recover" && runId==null)
            {
                string journal=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RoutePilot","run.json");
                runId=File.Exists(journal)?Storage.Read<RunState>(journal).Id:"";
            }
            byte[] request=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new AgentRequest(args[0],runId))+"\n");
            await client.WriteAsync(request,timeout.Token);await client.FlushAsync(timeout.Token);
            string reply=await PipeMessages.Read(client,timeout.Token);
            var value=JsonSerializer.Deserialize<AgentReply>(reply,Storage.Json)??throw new InvalidDataException("Empty reply.");
            Console.WriteLine(JsonSerializer.Serialize(value,Storage.Json));return value.Success?0:1;
        }
        catch(Exception ex){Console.Error.WriteLine(args.Contains("--self-test") || args.Contains("--optimization-integration") || args.Contains("--component-integration")?ex.ToString():ex.Message);return 1;}
    }
}
public static class PipeMessages
{
    public static async Task<string> Read(Stream stream,CancellationToken cancellation)
    {
        using var buffer=new MemoryStream();byte[] one=new byte[1];
        while(buffer.Length<32768)
        {
            int read=await stream.ReadAsync(one,cancellation);
            if(read==0)throw new EndOfStreamException("Client disconnected.");
            if(one[0]==10)return Encoding.UTF8.GetString(buffer.ToArray());
            buffer.WriteByte(one[0]);
        }
        throw new InvalidDataException("Request exceeds the size limit.");
    }
}
