using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using RoutePilot.Agent;

// Fresh owners, no hidden retries: observations survive a failed individual probe.
internal static class StartupLab
{
    static Process Start(string image,string mode)
    {
        var info=new ProcessStartInfo(image){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add(mode);var p=Process.Start(info)??throw new IOException();_=p.StandardError.BaseStream.CopyToAsync(Stream.Null);return p;
    }
    internal static async Task<JsonElement> Probe(string image,string mode,object spec,CancellationToken stop)
    {
        using var p=Start(image,mode);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(spec));p.StandardInput.Close();
            var result=await NativeAwgBackend.ReadReply(p.StandardOutput,deadline.Token);await p.WaitForExitAsync(deadline.Token);return result;
        }
        finally{if(!p.HasExited){p.Kill(true);await p.WaitForExitAsync();}}
    }
    internal static async Task<int> Run(string root)
    {
        var observations=new List<object>();bool completed=false;string? errorType=null;int failedProbes=0;
        try
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(420));var stop=deadline.Token;
            string image=Path.Combine(root,"native-awg-lab.exe"),config=await File.ReadAllTextAsync(Path.Combine(root,"private","peer.conf"),stop);
            string dns=config.Split('\n').Select(x=>x.Trim()).First(x=>x.StartsWith("DNS",StringComparison.Ordinal)&&x.Contains('=')).Split('=',2)[1].Split(',')[0].Trim();
            using var reference=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"..","server-egress-reference.json"),stop));string expected=reference.RootElement.GetProperty("serverEgressHash").GetString()!;
            for(int cycle=0;cycle<6;cycle++)
            {
                await using var owner=new NativeAwgBackend(image);string session=Guid.NewGuid().ToString("N"),token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));ulong generation=(ulong)cycle+1;
                var clock=Stopwatch.StartNew();var ready=await owner.StartAsync(JsonSerializer.SerializeToElement(new{Session=session,Generation=generation,Token=token,Config=config}),stop);int port=ready.GetProperty("port").GetInt32();long readyMs=clock.ElapsedMilliseconds;
                JsonElement Command(string action)=>JsonSerializer.SerializeToElement(new{Session=session,Generation=generation,Action=action});
                var initial=await owner.SendAsync(Command("Snapshot"),stop);
                for(int sample=0;sample<3;sample++)
                {
                    long began=clock.ElapsedMilliseconds;
                    var tasks=new[]{Probe(image,"--proxy-client",new{Port=port,Token=token,LiteralIPv4=true},stop),Probe(image,"--proxy-client",new{Port=port,Token=token},stop),Probe(image,"--proxy-udp-client",new{Port=port,Token=token,Server=dns},stop)};
                    var results=await Task.WhenAll(tasks);var snapshot=await owner.SendAsync(Command("Snapshot"),stop);
                    failedProbes+=results.Count(x=>!x.GetProperty("success").GetBoolean());
                    foreach(var https in results.Take(2))if(https.GetProperty("success").GetBoolean()&&(!https.TryGetProperty("egressHash",out var exit)||exit.GetString()!=expected))failedProbes++;
                    observations.Add(new{cycle,sample,readyMs,beganMs=began,endedMs=clock.ElapsedMilliseconds,initial,snapshot,literal=results[0],hostname=results[1],udp=results[2],literalEgressMatched=results[0].TryGetProperty("egressHash",out var a)&&a.GetString()==expected,hostnameEgressMatched=results[1].TryGetProperty("egressHash",out var b)&&b.GetString()==expected});
                    await Task.Delay(200,stop);
                }
                await owner.SendAsync(Command("Stop"),stop);
            }
            completed=true;
        }
        catch(Exception e){errorType=e.GetType().Name;}
        Console.WriteLine(JsonSerializer.Serialize(new{schema=1,completed,healthy=completed&&failedProbes==0,failedProbes,scope="Six fresh coordinated owners, three concurrent HTTPS/IP, HTTPS/name, UDP lifecycle samples each; bounded DNS retransmissions are recorded, no whole probe retry",observations,errorType}));return completed?0:1;
    }
}
